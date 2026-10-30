using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    internal static class GoogleEndpoints
    {
        // 測試時（有指定 CALENDARWIDGET_DATA）才允許用環境變數換成本機假伺服器，正常使用時一律連 Google
        private static string Pick(string envName, string real)
        {
            string v = string.IsNullOrEmpty(DataPaths.Override) ? null : Environment.GetEnvironmentVariable(envName);
            return string.IsNullOrEmpty(v) ? real : v;
        }

        public static string Auth { get { return Pick("CALENDARWIDGET_GOOGLE_AUTH", "https://accounts.google.com/o/oauth2/v2/auth"); } }
        public static string Token { get { return Pick("CALENDARWIDGET_GOOGLE_TOKEN", "https://oauth2.googleapis.com/token"); } }
        public static string Revoke { get { return Pick("CALENDARWIDGET_GOOGLE_REVOKE", "https://oauth2.googleapis.com/revoke"); } }
        public static string Calendar { get { return Pick("CALENDARWIDGET_GOOGLE_CALENDAR", "https://www.googleapis.com/calendar/v3"); } }
        public static string Tasks { get { return Pick("CALENDARWIDGET_GOOGLE_TASKS", "https://tasks.googleapis.com/tasks/v1"); } }
    }

    internal sealed class GoogleException : Exception
    {
        public readonly bool NeedsSignIn;       // 登入已失效或被撤銷，需要重新連接帳號
        public readonly bool NeedsWriteScope;   // 還沒有取得「寫入」的授權（只有讀取），需要多授權一次

        public GoogleException(string message, bool needsSignIn) : this(message, needsSignIn, false) { }

        public GoogleException(string message, bool needsSignIn, bool needsWriteScope) : base(message)
        {
            NeedsSignIn = needsSignIn;
            NeedsWriteScope = needsWriteScope;
        }
    }

    internal static class GoogleHttp
    {
        public sealed class Result
        {
            public int Status;
            public string Body;
        }

        public static Result Send(string method, string url, string bearer, string formBody)
        {
            return Send(method, url, bearer, formBody, "application/x-www-form-urlencoded");
        }

        public static Result Send(string method, string url, string bearer, string body, string contentType)
        {
            string formBody = body;
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "CalendarWidget/1.0";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (bearer != null) req.Headers["Authorization"] = "Bearer " + bearer;

            if (formBody != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(formBody);
                req.ContentType = contentType;
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return new Result { Status = (int)resp.StatusCode, Body = Read(resp) };
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;
                using (resp) return new Result { Status = (int)resp.StatusCode, Body = Read(resp) };
            }
        }

        private static string Read(HttpWebResponse resp)
        {
            using (var s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        public static string Form(IEnumerable<KeyValuePair<string, string>> fields)
        {
            return string.Join("&", fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value ?? "")));
        }

        public static string Describe(Exception ex)
        {
            var web = ex as WebException;
            if (web != null)
            {
                if (web.Status == WebExceptionStatus.Timeout) return "連線逾時";
                if (web.Status == WebExceptionStatus.NameResolutionFailure) return "找不到主機（可能沒有網路）";
                return web.Status.ToString();
            }
            return ex.Message;
        }
    }

    // OAuth 2.0（電腦版應用程式）：用瀏覽器登入，本機 127.0.0.1 接回傳的授權碼，並用 PKCE 防止授權碼被別人截走
    internal sealed class GoogleAuth
    {
        public const string Scopes = "https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/tasks.readonly";

        // 新增行程才需要的「寫入」授權：平常不要求，第一次要新增行程時才多授權一次
        public const string WriteScope = "https://www.googleapis.com/auth/calendar.events";

        private readonly GoogleSecrets _secrets;
        private readonly object _gate = new object();          // 只保護欄位的讀寫，不在裡面連線
        private readonly object _refreshGate = new object();   // 同一時間只讓一個執行緒去換存取權杖
        private string _accessToken;
        private DateTime _expiresUtc;
        private TcpListener _listener;
        private volatile bool _cancelled;

        // 這一次登入用的網址：瀏覽器沒有自動開啟、或想換別的瀏覽器時，可以複製起來貼上
        public volatile string AuthUrl;

        // 預設用系統預設瀏覽器開啟登入網址；測試時可以換掉
        public Action<string> OpenBrowser = delegate(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        };

        public GoogleAuth(GoogleSecrets secrets)
        {
            _secrets = secrets;
        }

        public bool HasCredentials { get { return _secrets.ClientId.Length > 0 && _secrets.ClientSecret.Length > 0; } }
        public bool IsSignedIn { get { return _secrets.RefreshToken.Length > 0; } }

        // 目前的授權有沒有包含可以新增行程的範圍（calendar.events，或整個 calendar）
        public bool HasWriteScope
        {
            get
            {
                foreach (string s in (_secrets.Scopes ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (s == WriteScope || s == "https://www.googleapis.com/auth/calendar") return true;
                }
                return false;
            }
        }

        public void Invalidate()
        {
            lock (_gate) { _accessToken = null; }
        }

        public string AccessToken()
        {
            lock (_gate)
            {
                if (_accessToken != null && DateTime.UtcNow < _expiresUtc.AddSeconds(-60)) return _accessToken;
                if (!IsSignedIn) throw new GoogleException("尚未連接 Google 帳號", true);
            }

            // 換新的權杖要連線，網路不好時可能等很久：不能一直握著 _gate，否則 UI 執行緒（中斷連接、儲存設定）會跟著卡住。
            // _refreshGate 只負責同一時間只讓一個執行緒去換
            lock (_refreshGate)
            {
                string clientId, clientSecret, refresh;
                lock (_gate)
                {
                    if (_accessToken != null && DateTime.UtcNow < _expiresUtc.AddSeconds(-60)) return _accessToken;   // 等鎖的期間別人已經換好了
                    if (!IsSignedIn) throw new GoogleException("尚未連接 Google 帳號", true);
                    clientId = _secrets.ClientId;
                    clientSecret = _secrets.ClientSecret;
                    refresh = _secrets.RefreshToken;
                }

                var result = GoogleHttp.Send("POST", GoogleEndpoints.Token, null, GoogleHttp.Form(new[]
                {
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("client_secret", clientSecret),
                    new KeyValuePair<string, string>("refresh_token", refresh),
                    new KeyValuePair<string, string>("grant_type", "refresh_token")
                }));

                var json = SafeParse(result.Body);
                if (result.Status != 200 || Json.Str(json, "access_token") == null)
                {
                    string error = Json.Str(json, "error") ?? ("HTTP " + result.Status);
                    // invalid_grant：登入被撤銷或過期（例如測試中的應用程式 7 天後失效）
                    bool needsSignIn = error == "invalid_grant" || error == "invalid_client" || error == "unauthorized_client";
                    throw new GoogleException("無法取得存取權杖（" + error + "）", needsSignIn);
                }

                string token = Json.Str(json, "access_token");
                double seconds;
                DateTime expires = DateTime.UtcNow.AddSeconds(double.TryParse(Json.Str(json, "expires_in"), out seconds) ? seconds : 3000);
                string scope = Json.Str(json, "scope");   // Google 回報目前實際有的授權範圍（使用者在同意畫面可能取消勾選其中一部分），以它為準

                bool saveScope = false, replaced = false;
                lock (_gate)
                {
                    if (!IsSignedIn) throw new GoogleException("尚未連接 Google 帳號", true);   // 換的期間被中斷連接了：結果不要留下
                    if (_secrets.RefreshToken != refresh)
                    {
                        replaced = true;   // 換的期間重新登入過，這份是用舊憑證換的，丟掉重來
                    }
                    else
                    {
                        _accessToken = token;
                        _expiresUtc = expires;
                        if (!string.IsNullOrEmpty(scope) && scope != _secrets.Scopes)
                        {
                            _secrets.Scopes = scope;
                            saveScope = true;
                        }
                    }
                }
                if (replaced) return AccessToken();
                if (saveScope) _secrets.Save();
                return token;
            }
        }

        public void Cancel()
        {
            _cancelled = true;
            var l = _listener;
            if (l != null)
            {
                try { l.Stop(); } catch (SocketException) { }
            }
        }

        // 會一直等到你在瀏覽器完成登入（或逾時、取消）。必須在背景執行緒呼叫
        public void Authorize(TimeSpan timeout)
        {
            AuthorizeCore(Scopes, false, timeout);
        }

        // 第一次要新增行程時：只多要「寫入」這一個範圍（並保留原本已經同意的讀取範圍），
        // 需要使用者在瀏覽器再按一次同意。必須在背景執行緒呼叫
        public void AuthorizeWrite(TimeSpan timeout)
        {
            AuthorizeCore(WriteScope, true, timeout);
            if (!HasWriteScope) throw new GoogleException("Google 沒有給這個程式「新增行程」的權限（授權時需要勾選那一項）", false, true);
        }

        private void AuthorizeCore(string scope, bool includeGranted, TimeSpan timeout)
        {
            if (!HasCredentials) throw new GoogleException("請先填入用戶端 ID 和用戶端密鑰", false);
            _cancelled = false;

            string verifier = Base64Url(RandomBytes(48));
            string challenge = Base64Url(SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomBytes(16));

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _listener = listener;
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirect = "http://127.0.0.1:" + port;

                var fields = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("client_id", _secrets.ClientId),
                    new KeyValuePair<string, string>("redirect_uri", redirect),
                    new KeyValuePair<string, string>("response_type", "code"),
                    new KeyValuePair<string, string>("scope", scope),
                    new KeyValuePair<string, string>("code_challenge", challenge),
                    new KeyValuePair<string, string>("code_challenge_method", "S256"),
                    new KeyValuePair<string, string>("state", state),
                    new KeyValuePair<string, string>("access_type", "offline"),
                    new KeyValuePair<string, string>("prompt", "consent")
                };
                if (includeGranted)
                {
                    fields.Add(new KeyValuePair<string, string>("include_granted_scopes", "true"));   // 保留之前已經同意的範圍
                    if (!string.IsNullOrEmpty(_secrets.Email)) fields.Add(new KeyValuePair<string, string>("login_hint", _secrets.Email));
                }

                string url = GoogleEndpoints.Auth + "?" + GoogleHttp.Form(fields);
                AuthUrl = url;
                // 打不開瀏覽器（沒有預設瀏覽器等）也不能就此中止：畫面上有「複製登入網址」，使用者貼到瀏覽器一樣能完成
                try { OpenBrowser(url); }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }

                string code = WaitForCode(listener, state, timeout);
                Exchange(code, verifier, redirect, scope);
            }
            finally
            {
                AuthUrl = null;
                _listener = null;
                try { listener.Stop(); } catch (SocketException) { }
            }
        }

        private string WaitForCode(TcpListener listener, string state, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            Task<TcpClient> pending = null;   // 同一時間只留一個「等待連線」，沒等到就繼續等同一個

            while (true)
            {
                if (_cancelled) throw new GoogleException("已取消登入", false);
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) throw new GoogleException("等待登入逾時（請再按一次「連接」）", false);

                // 重要：不能每次迴圈都重新呼叫 AcceptTcpClientAsync。
                // 前一個還在等的話，瀏覽器送回來的連線會被它收走，而我們已經不看它了，登入結果就永遠收不到。
                if (pending == null)
                {
                    try { pending = listener.AcceptTcpClientAsync(); }
                    catch (ObjectDisposedException) { throw new GoogleException("已取消登入", false); }
                    catch (InvalidOperationException) { throw new GoogleException("已取消登入", false); }
                }

                bool completed;
                try
                {
                    completed = pending.Wait(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
                }
                catch (AggregateException)
                {
                    if (_cancelled) throw new GoogleException("已取消登入", false);
                    throw new GoogleException("等待登入時發生錯誤", false);
                }
                if (!completed) continue;

                Task<TcpClient> done = pending;
                pending = null;

                using (TcpClient client = done.Result)
                {
                    client.ReceiveTimeout = 5000;
                    var stream = client.GetStream();
                    string requestLine = ReadRequestLine(stream);
                    var query = ParseQuery(requestLine);

                    string error;
                    query.TryGetValue("error", out error);
                    string gotCode;
                    query.TryGetValue("code", out gotCode);
                    string gotState;
                    query.TryGetValue("state", out gotState);

                    if (gotCode == null && error == null)
                    {
                        Respond(stream, 404, "Not found");   // 例如瀏覽器順便要 favicon，忽略
                        continue;
                    }
                    if (gotState != state)
                    {
                        // 不是我們發出去的登入（本機其他程式或網頁亂打這個埠）：拒絕並繼續等真正的回應，不要讓別人能讓登入失敗
                        Respond(stream, 400, "State mismatch");
                        continue;
                    }
                    if (error != null)
                    {
                        Respond(stream, 200, "<html><meta charset='utf-8'><body style='font-family:sans-serif'><h3>登入未完成</h3><p>你可以關閉這個頁面，回到月曆小工具。</p></body></html>");
                        throw new GoogleException(error == "access_denied" ? "你拒絕了授權" : "登入失敗（" + error + "）", false);
                    }

                    // 這時還沒拿授權碼去換憑證，換失敗的話小工具會自己顯示原因，所以這裡只說「已收到」
                    Respond(stream, 200, "<html><meta charset='utf-8'><body style='font-family:sans-serif'><h3>已收到 Google 的授權</h3><p>可以關閉這個頁面，回到月曆小工具。</p></body></html>");
                    return gotCode;
                }
            }
        }

        private void Exchange(string code, string verifier, string redirect, string requestedScope)
        {
            var result = GoogleHttp.Send("POST", GoogleEndpoints.Token, null, GoogleHttp.Form(new[]
            {
                new KeyValuePair<string, string>("client_id", _secrets.ClientId),
                new KeyValuePair<string, string>("client_secret", _secrets.ClientSecret),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("code_verifier", verifier),
                new KeyValuePair<string, string>("redirect_uri", redirect),
                new KeyValuePair<string, string>("grant_type", "authorization_code")
            }));

            var json = SafeParse(result.Body);
            string refresh = Json.Str(json, "refresh_token");
            if (result.Status != 200 || Json.Str(json, "access_token") == null)
                throw new GoogleException("換取登入憑證失敗（" + (Json.Str(json, "error") ?? "HTTP " + result.Status) + "）", false);
            if (refresh == null)
                throw new GoogleException("Google 沒有回傳長期登入憑證，請再按一次「連接」", false);

            lock (_gate)
            {
                _secrets.RefreshToken = refresh;
                // 實際取得的範圍以 Google 回傳的為準；沒回傳時，用我們要求的範圍（加上原本就有的）
                string granted = Json.Str(json, "scope");
                _secrets.Scopes = !string.IsNullOrEmpty(granted) ? granted
                    : (requestedScope == Scopes ? Scopes : ((_secrets.Scopes ?? "") + " " + requestedScope).Trim());
                _accessToken = Json.Str(json, "access_token");
                double seconds;
                _expiresUtc = DateTime.UtcNow.AddSeconds(double.TryParse(Json.Str(json, "expires_in"), out seconds) ? seconds : 3000);
            }
            _secrets.Save();
        }

        // 結束登入：盡力通知 Google 撤銷憑證（失敗也沒關係），並清掉本機的憑證
        public void SignOut()
        {
            string token;
            lock (_gate)
            {
                token = _secrets.RefreshToken;
                _secrets.RefreshToken = "";
                _secrets.Email = "";
                _secrets.Scopes = "";
                _accessToken = null;
            }
            _secrets.Save();

            if (token.Length == 0) return;
            Task.Run(() =>
            {
                try { GoogleHttp.Send("POST", GoogleEndpoints.Revoke, null, GoogleHttp.Form(new[] { new KeyValuePair<string, string>("token", token) })); }
                catch (Exception) { }
            });
        }

        // ---------- 小工具 ----------

        internal static Dictionary<string, object> SafeParse(string body)
        {
            try { return Json.ParseObject(body); }
            catch (Exception) { return null; }
        }

        private static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        internal static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string ReadRequestLine(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            var clock = Stopwatch.StartNew();
            try
            {
                // 整個請求行最多讀 3 秒：一個只連線、一點一滴送資料的本機程式，不能把這條等待登入的執行緒卡住
                while (sb.Length < 4096 && clock.ElapsedMilliseconds < 3000 && stream.Read(buf, 0, 1) == 1)
                {
                    if (buf[0] == '\n') break;
                    sb.Append((char)buf[0]);
                }
            }
            catch (IOException) { }
            return sb.ToString().TrimEnd('\r');
        }

        private static Dictionary<string, string> ParseQuery(string requestLine)
        {
            var result = new Dictionary<string, string>();
            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2) return result;
            int q = parts[1].IndexOf('?');
            if (q < 0) return result;
            foreach (string pair in parts[1].Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                result[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
            return result;
        }

        private static void Respond(NetworkStream stream, int status, string body)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(body);
                string head = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Error") + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
                              data.Length + "\r\nConnection: close\r\n\r\n";
                byte[] h = Encoding.ASCII.GetBytes(head);
                stream.Write(h, 0, h.Length);
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
            catch (IOException) { }
        }
    }
}

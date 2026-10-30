using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CalendarWidget
{
    internal enum UpdateStatus { UpToDate, NewVersion, NoRelease, Failed }

    internal sealed class UpdateResult
    {
        public UpdateStatus Status;
        public string Version = "";   // 最新發行的版本號（「0.9.1」）
        public string Url = "";       // 發行頁面；保證是官方網址
        public string Notes = "";     // 發行說明（Release 的內文）
        public string Message = "";   // 失敗的原因，直接給使用者看
    }

    // 檢查 GitHub 上有沒有比目前更新的發行版本。只有使用者按「檢查更新」才會連線，程式不會自己檢查，更不會自己下載或執行任何東西：
    // 有新版時只是告訴使用者，由使用者決定要不要用瀏覽器打開發行頁面
    internal static class UpdateChecker
    {
        private const string LatestApi = "https://api.github.com/repos/meme755144/calendarwidget/releases/latest";
        private static readonly Regex TagPattern = new Regex(@"^[vV]?(?<a>\d{1,9})\.(?<b>\d{1,9})(?:\.(?<c>\d{1,9}))?(?:\.\d{1,9})?(?:[-+].*)?$");

        // 測試時（有設定測試資料夾）可以用環境變數 CALENDARWIDGET_UPDATE_URL 換成自己的伺服器
        public static string ApiUrl
        {
            get
            {
                string url = null;
                if (!string.IsNullOrEmpty(DataPaths.Override)) url = Environment.GetEnvironmentVariable("CALENDARWIDGET_UPDATE_URL");
                return string.IsNullOrEmpty(url) ? LatestApi : url;
            }
        }

        // 連線並判斷結果。會阻塞，請在背景執行緒呼叫。任何失敗都回傳 Failed，不丟例外
        public static UpdateResult Check(Version current)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                var req = (HttpWebRequest)WebRequest.Create(ApiUrl);
                req.UserAgent = "CalendarWidget/" + current.ToString();   // GitHub 的 API 一定要有 User-Agent
                req.Accept = "application/vnd.github+json";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                        return Interpret(current, (int)resp.StatusCode, ReadBody(resp));
                }
                catch (WebException ex)
                {
                    var resp = ex.Response as HttpWebResponse;
                    if (resp == null) return Fail("連不上 GitHub，請確認網路連線後再試。");
                    using (resp) return Interpret(current, (int)resp.StatusCode, "");
                }
            }
            catch (Exception ex)
            {
                return Fail("檢查更新時發生錯誤：" + ex.Message);
            }
        }

        private static string ReadBody(HttpWebResponse resp)
        {
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        // 把 HTTP 結果轉成給使用者看的結果。不連線，方便測試
        public static UpdateResult Interpret(Version current, int httpStatus, string body)
        {
            if (httpStatus == 404) return new UpdateResult { Status = UpdateStatus.NoRelease };
            if (httpStatus == 403 || httpStatus == 429) return Fail("GitHub 暫時限制了這個網路的請求次數，請稍後再試。");
            if (httpStatus != 200) return Fail("GitHub 回應了預期外的狀態（HTTP " + httpStatus.ToString(CultureInfo.InvariantCulture) + "），請稍後再試。");

            Dictionary<string, object> release;
            try { release = Json.ParseObject(body); }
            catch (Exception) { release = null; }
            if (release == null) return Fail("GitHub 回應的內容看不懂，請稍後再試。");

            string tag = Json.Str(release, "tag_name");
            Version latest;
            if (!TryParseTag(tag, out latest)) return Fail("最新發行版本的版本號看不懂（" + tag + "）。");

            if (latest <= current)
                return new UpdateResult { Status = UpdateStatus.UpToDate, Version = latest.ToString() };

            // 發行頁面的網址是伺服器回傳的，不是官方網址就改用固定的發行頁，不照著打開
            string url = Json.Str(release, "html_url");
            return new UpdateResult
            {
                Status = UpdateStatus.NewVersion,
                Version = latest.ToString(),
                Url = AppInfo.IsOfficialUrl(url) ? url : AppInfo.ReleasesUrl,
                Notes = Json.Str(release, "body")
            };
        }

        // 「v0.9.1」「0.9」「v1.2.3-beta」→ 版本（缺的部分補 0，只比前三段）；認不得就回傳 false
        public static bool TryParseTag(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(tag)) return false;
            Match m = TagPattern.Match(tag.Trim());
            if (!m.Success) return false;

            int a, b, c = 0;
            if (!int.TryParse(m.Groups["a"].Value, out a) || !int.TryParse(m.Groups["b"].Value, out b)) return false;
            if (m.Groups["c"].Success && !int.TryParse(m.Groups["c"].Value, out c)) return false;
            version = new Version(a, b, c);
            return true;
        }

        // 發行說明的前幾行（去掉 Markdown 的標題與項目符號、太長的截斷），給提醒框顯示
        public static List<string> NotesPreview(string notes, int maxLines)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(notes)) return lines;
            foreach (string raw in notes.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim().TrimStart('#', '-', '*').Replace("**", "").Replace("`", "").Trim();
                if (line.Length == 0) continue;
                if (line.Length > 60) line = line.Substring(0, 60) + "…";
                lines.Add(line);
                if (lines.Count >= maxLines) break;
            }
            return lines;
        }

        private static UpdateResult Fail(string message)
        {
            return new UpdateResult { Status = UpdateStatus.Failed, Message = message };
        }
    }
}

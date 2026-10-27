using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CalendarWidget
{
    internal sealed class CalendarSource
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "";
        public string Url = "";
        public string Color = "#6CB4FF";
        public ItemKind Kind = ItemKind.Event;   // 只會是 Holiday（節日）或 Event（行程）
        public int IntervalMinutes = 60;          // 0 = 只手動更新
        public bool Enabled = true;

        public CalendarSource Clone() { return (CalendarSource)MemberwiseClone(); }
    }

    // 來源清單：名稱、顏色等放在 %APPDATA%\sources.txt；
    // 網址（等同日曆的鑰匙）另外用 Windows 資料保護加密後放在 %LOCALAPPDATA%\urls.dat，備份或分享設定檔時不會帶出去
    internal static class SourceConfigStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CalendarWidget.Urls");

        private static string SourcesFile { get { return Path.Combine(DataPaths.Dir, "sources.txt"); } }
        private static string UrlsFile { get { return Path.Combine(DataPaths.LocalDir, "urls.dat"); } }

        public static List<CalendarSource> Load()
        {
            var list = new List<CalendarSource>();
            try
            {
                var urls = LoadUrls();
                if (!File.Exists(SourcesFile)) return list;
                foreach (string line in File.ReadAllLines(SourcesFile, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    int interval;
                    if (f.Length < 6 || !int.TryParse(f[3], out interval)) continue;
                    string url;
                    urls.TryGetValue(f[0], out url);
                    list.Add(new CalendarSource
                    {
                        Id = f[0],
                        Enabled = f[1] == "1",
                        Kind = f[2] == "H" ? ItemKind.Holiday : ItemKind.Event,
                        IntervalMinutes = interval,
                        Color = Colors.IsValidHex(f[4]) ? f[4] : "#6CB4FF",
                        Name = f[5],
                        Url = url ?? ""
                    });
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return list;
        }

        public static void Save(IEnumerable<CalendarSource> sources)
        {
            if (Settings.ReadOnly) return;
            var list = sources.ToList();
            try
            {
                Directory.CreateDirectory(DataPaths.Dir);
                File.WriteAllLines(SourcesFile, list.Select(s =>
                    s.Id + "\t" + (s.Enabled ? "1" : "0") + "\t" + (s.Kind == ItemKind.Holiday ? "H" : "E") + "\t" +
                    s.IntervalMinutes.ToString(CultureInfo.InvariantCulture) + "\t" + s.Color + "\t" + Clean(s.Name)),
                    new UTF8Encoding(false));

                Directory.CreateDirectory(DataPaths.LocalDir);
                File.WriteAllLines(UrlsFile, list.Where(s => s.Url.Length > 0).Select(s =>
                    s.Id + "\t" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s.Url), Entropy, DataProtectionScope.CurrentUser))));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (CryptographicException) { }
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static Dictionary<string, string> LoadUrls()
        {
            var map = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(UrlsFile)) return map;
                foreach (string line in File.ReadAllLines(UrlsFile))
                {
                    string[] f = line.Split('\t');
                    if (f.Length != 2) continue;
                    try
                    {
                        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(f[1]), Entropy, DataProtectionScope.CurrentUser);
                        map[f[0]] = Encoding.UTF8.GetString(plain);
                    }
                    catch (CryptographicException) { }   // 換了電腦或使用者就解不開，視為網址遺失
                    catch (FormatException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return map;
        }
    }

    internal sealed class SourceStatus
    {
        public DateTime? LastFetch;     // 上次成功更新的時間（本機時間）
        public DateTime RetryAfter;     // 失敗後至少等到這個時間才再試，避免一直打對方伺服器
        public string Error;
        public int ItemCount;
        public bool Fetching;
    }

    // 負責：載入快取、依各來源的頻率抓取、解析、更新到 ItemStore。所有狀態只在 UI 執行緒上存取，抓取與解析在背景執行
    internal sealed class SourceManager
    {
        private const int MaxDownloadBytes = 10 * 1024 * 1024;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

        private readonly ItemStore _store;
        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private List<CalendarSource> _sources = new List<CalendarSource>();
        private readonly Dictionary<string, SourceStatus> _status = new Dictionary<string, SourceStatus>();

        public event Action Changed;          // 某個來源的項目更新了
        public event Action StatusChanged;    // 狀態文字有變（給設定視窗刷新）

        public SourceManager(ItemStore store, Dispatcher ui)
        {
            _store = store;
            _ui = ui;
            _timer.Tick += delegate { RefreshDue(); };
        }

        private static string CacheDir { get { return Path.Combine(DataPaths.LocalDir, "cache"); } }
        private static string CacheFile(string id) { return Path.Combine(CacheDir, id + ".ics"); }
        private static string MetaFile(string id) { return Path.Combine(CacheDir, id + ".meta"); }
        private static string StoreKey(string id) { return "src:" + id; }

        public List<CalendarSource> Sources { get { return _sources.Select(s => s.Clone()).ToList(); } }

        public SourceStatus StatusOf(string id)
        {
            SourceStatus st;
            return _status.TryGetValue(id, out st) ? st : new SourceStatus();
        }

        public string StatusText(string id)
        {
            var src = _sources.FirstOrDefault(s => s.Id == id);
            if (src == null) return "尚未儲存";
            if (!src.Enabled) return "已停用";
            if (src.Url.Length == 0) return "尚未設定網址";
            var st = StatusOf(id);
            if (st.Fetching) return "更新中…";
            string ok = st.LastFetch.HasValue
                ? "上次更新 " + st.LastFetch.Value.ToString("yyyy-MM-dd HH:mm") + "（" + st.ItemCount + " 筆）"
                : "尚未更新";
            return st.Error != null ? "更新失敗：" + st.Error + (st.LastFetch.HasValue ? "　" + ok : "") : ok;
        }

        public void Start()
        {
            _sources = SourceConfigStore.Load();
            foreach (var src in _sources) BeginLoad(src, true);
            ScheduleNext();
        }

        // 設定視窗按下「儲存」後呼叫
        public void Apply(List<CalendarSource> newList)
        {
            var old = _sources.ToDictionary(s => s.Id);
            var keep = new HashSet<string>(newList.Select(s => s.Id));

            foreach (var removed in _sources.Where(s => !keep.Contains(s.Id)).ToList())
            {
                _store.RemoveSource(StoreKey(removed.Id));
                _status.Remove(removed.Id);
                DeleteCache(removed.Id);
            }

            _sources = newList.Select(s => s.Clone()).ToList();
            SourceConfigStore.Save(_sources);

            foreach (var src in _sources)
            {
                CalendarSource before;
                bool existed = old.TryGetValue(src.Id, out before);

                if (!src.Enabled)
                {
                    _store.RemoveSource(StoreKey(src.Id));
                    continue;
                }
                if (!existed || before.Url != src.Url)
                {
                    DeleteCache(src.Id);
                    _store.RemoveSource(StoreKey(src.Id));
                    _status[src.Id] = new SourceStatus();
                    if (src.Url.Length > 0) Fetch(src);
                }
                else if (!before.Enabled || before.Color != src.Color || before.Kind != src.Kind)
                {
                    BeginLoad(src, false);   // 顏色或類型改了：用快取重新解析，不需要上網
                }
            }

            ScheduleNext();
            RaiseChanged();
            RaiseStatusChanged();
        }

        // 載入範圍改了：用已下載的內容依新範圍重新解析（不需要上網）
        public void Reparse()
        {
            foreach (var src in _sources.Where(s => s.Enabled && s.Url.Length > 0)) BeginLoad(src, false);
        }

        public void RefreshNow(string id)
        {
            var src = _sources.FirstOrDefault(s => s.Id == id);
            if (src != null && src.Enabled && src.Url.Length > 0) Fetch(src);
        }

        public void RefreshDue()
        {
            DateTime now = DateTime.Now;
            foreach (var src in _sources)
            {
                if (IsDue(src, now)) Fetch(src);
            }
            ScheduleNext();
        }

        // ---------- 排程 ----------

        private bool IsDue(CalendarSource src, DateTime now)
        {
            if (!src.Enabled || src.Url.Length == 0) return false;
            var st = StatusOf(src.Id);
            if (st.Fetching || now < st.RetryAfter) return false;
            if (!st.LastFetch.HasValue) return true;   // 從沒成功過（包含「只手動更新」的來源，至少抓一次）
            return src.IntervalMinutes > 0 && now - st.LastFetch.Value >= TimeSpan.FromMinutes(src.IntervalMinutes);
        }

        // 只排「下一個到期時間」的單一計時器，不輪詢
        private void ScheduleNext()
        {
            _timer.Stop();
            DateTime? next = null;
            foreach (var src in _sources)
            {
                if (!src.Enabled || src.Url.Length == 0 || src.IntervalMinutes <= 0) continue;
                var st = StatusOf(src.Id);
                if (st.Fetching) continue;
                DateTime due = st.LastFetch.HasValue ? st.LastFetch.Value.AddMinutes(src.IntervalMinutes) : DateTime.Now;
                if (due < st.RetryAfter) due = st.RetryAfter;
                if (!next.HasValue || due < next.Value) next = due;
            }
            if (!next.HasValue) return;

            TimeSpan wait = next.Value - DateTime.Now;
            if (wait < TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
            _timer.Interval = wait;
            _timer.Start();
        }

        // ---------- 載入快取 ----------

        private void BeginLoad(CalendarSource src, bool thenFetchIfDue)
        {
            if (!src.Enabled || src.Url.Length == 0)
            {
                if (thenFetchIfDue) return;
            }

            string id = src.Id;
            var snapshot = src.Clone();
            Task.Run(() =>
            {
                List<CalendarItem> items = null;
                DateTime? fetched = null;
                try
                {
                    if (File.Exists(CacheFile(id)))
                    {
                        items = ParseText(File.ReadAllText(CacheFile(id), Encoding.UTF8), snapshot);
                        long ticks;
                        if (File.Exists(MetaFile(id)) && long.TryParse(File.ReadAllText(MetaFile(id)).Trim(), out ticks))
                            fetched = new DateTime(ticks, DateTimeKind.Local);
                    }
                }
                catch (Exception) { items = null; }

                _ui.BeginInvoke(new Action(delegate
                {
                    var current = _sources.FirstOrDefault(s => s.Id == id);
                    if (current == null || !current.Enabled) return;

                    var st = EnsureStatus(id);
                    if (items != null)
                    {
                        _store.SetSource(StoreKey(id), items);
                        st.ItemCount = items.Count;
                        if (fetched.HasValue && !st.LastFetch.HasValue) st.LastFetch = fetched;
                    }
                    RaiseChanged();
                    RaiseStatusChanged();

                    if (thenFetchIfDue && IsDue(current, DateTime.Now)) Fetch(current);
                    ScheduleNext();
                }));
            });
        }

        // ---------- 下載 ----------

        private void Fetch(CalendarSource src)
        {
            var st = EnsureStatus(src.Id);
            if (st.Fetching) return;
            st.Fetching = true;
            RaiseStatusChanged();

            var snapshot = src.Clone();
            Task.Run(() =>
            {
                string text = null, error = null;
                List<CalendarItem> items = null;
                try
                {
                    text = Download(snapshot.Url);
                    if (text.IndexOf("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidDataException("內容不是 ICS 日曆");
                    items = ParseText(text, snapshot);
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllText(CacheFile(snapshot.Id), text, new UTF8Encoding(false));
                    File.WriteAllText(MetaFile(snapshot.Id), DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    error = Describe(ex);
                    items = null;
                }

                _ui.BeginInvoke(new Action(delegate
                {
                    var current = _sources.FirstOrDefault(s => s.Id == snapshot.Id);
                    var status = EnsureStatus(snapshot.Id);
                    status.Fetching = false;

                    // 抓的期間來源被刪除或改了網址，結果就丟掉
                    if (current == null || current.Url != snapshot.Url || !current.Enabled)
                    {
                        RaiseStatusChanged();
                        ScheduleNext();
                        return;
                    }

                    if (items != null)
                    {
                        status.Error = null;
                        status.LastFetch = DateTime.Now;
                        status.ItemCount = items.Count;
                        status.RetryAfter = DateTime.MinValue;
                        _store.SetSource(StoreKey(snapshot.Id), items);
                        RaiseChanged();
                    }
                    else
                    {
                        status.Error = error;
                        status.RetryAfter = DateTime.Now + RetryDelay;   // 保留舊資料，稍後再試
                    }
                    RaiseStatusChanged();
                    ScheduleNext();
                }));
            });
        }

        private static List<CalendarItem> ParseText(string text, CalendarSource src)
        {
            DateTime today = DateTime.Today;
            // 本機檔案（例如「ICS 產生」做的節日檔）內容是你自己決定的，不受「載入範圍」限制，免得往後翻幾個月就沒了
            bool local = src.Url.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            DateTime from = local ? today.AddYears(-3) : LoadRange.From;
            DateTime to = local ? today.AddYears(5) : LoadRange.To;
            var items = IcsParser.Parse(text, from, to, src.Kind, src.Color);
            string calendarId = CalendarIds.Ics(src.Id);
            foreach (var item in items) item.CalendarId = calendarId;
            return items;
        }

        private static string Download(string url)
        {
            string target = url.Trim();
            if (target.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) target = "https://" + target.Substring(9);
            Uri uri;
            if (!Uri.TryCreate(target, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
                throw new InvalidDataException("網址格式不正確（只支援 http、https、webcal，或本機檔案 file:///）");

            // 本機檔案（例如「ICS 產生」分頁做出來的節日檔）：直接讀檔
            if (uri.Scheme == Uri.UriSchemeFile)
            {
                var info = new FileInfo(uri.LocalPath);
                if (!info.Exists) throw new InvalidDataException("找不到這個檔案：" + uri.LocalPath);
                if (info.Length > MaxDownloadBytes) throw new InvalidDataException("檔案太大（超過 10 MB）");
                string fileText = File.ReadAllText(uri.LocalPath, Encoding.UTF8);
                return fileText.Length > 0 && fileText[0] == '\ufeff' ? fileText.Substring(1) : fileText;
            }

            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2

            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.UserAgent = "CalendarWidget/1.0";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var mem = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    mem.Write(buffer, 0, n);
                    if (mem.Length > MaxDownloadBytes) throw new InvalidDataException("檔案太大（超過 10 MB）");
                }
                string text = Encoding.UTF8.GetString(mem.ToArray());
                return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
            }
        }

        private static string Describe(Exception ex)
        {
            var web = ex as WebException;
            if (web != null)
            {
                var resp = web.Response as HttpWebResponse;
                if (resp != null) return "HTTP " + (int)resp.StatusCode;
                if (web.Status == WebExceptionStatus.Timeout) return "連線逾時";
                if (web.Status == WebExceptionStatus.NameResolutionFailure) return "找不到主機（可能沒有網路）";
                return web.Status.ToString();
            }
            return ex.Message;
        }

        private SourceStatus EnsureStatus(string id)
        {
            SourceStatus st;
            if (!_status.TryGetValue(id, out st))
            {
                st = new SourceStatus();
                _status[id] = st;
            }
            return st;
        }

        private static void DeleteCache(string id)
        {
            try
            {
                if (File.Exists(CacheFile(id))) File.Delete(CacheFile(id));
                if (File.Exists(MetaFile(id))) File.Delete(MetaFile(id));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void RaiseChanged() { var h = Changed; if (h != null) h(); }
        private void RaiseStatusChanged() { var h = StatusChanged; if (h != null) h(); }
    }
}

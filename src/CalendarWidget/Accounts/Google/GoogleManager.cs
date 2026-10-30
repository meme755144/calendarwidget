using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CalendarWidget
{
    // Google 帳號：登入、日曆清單、行程與待辦的定期更新。狀態只在 UI 執行緒上存取，連線與解析在背景執行
    internal sealed class GoogleManager : ICalendarAccount
    {
        private const string TasksKey = "tasks";
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

        private readonly ItemStore _store;
        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly GoogleSecrets _secrets;
        private readonly GoogleAuth _auth;
        private readonly GoogleClient _client;
        private readonly Dictionary<string, List<CalendarItem>> _items = new Dictionary<string, List<CalendarItem>>();
        private GoogleConfig _config;

        private DateTime? _lastFetch;
        private DateTime _retryAfter;
        private string _error;
        private bool _needsSignIn;
        private bool _fetching;
        private bool _refetchRequested;       // 抓的期間又有變動（新增、刪除、改設定）：這一輪結束後再抓一次
        private volatile int _generation;     // 每次中斷連接加一；還在跑的抓取發現世代變了，結果就整批丟掉
        private bool _connecting;
        private bool _adding;

        public event Action Changed;            // 行程或待辦更新了
        public event Action StatusChanged;      // 狀態文字有變
        public event Action CalendarsChanged;   // 日曆清單有變（連接、重新載入、中斷連接）

        public GoogleManager(ItemStore store, Dispatcher ui)
        {
            _store = store;
            _ui = ui;
            _secrets = GoogleSecrets.Load();
            _config = GoogleConfig.Load();
            _auth = new GoogleAuth(_secrets);
            _client = new GoogleClient(_auth);
            _timer.Tick += delegate { RefreshDue(); };
        }

        private static string CacheDir { get { return Path.Combine(DataPaths.LocalDir, "cache"); } }
        private static string MetaFile { get { return Path.Combine(CacheDir, "google.meta"); } }
        private static string CalKey(string calendarId) { return "cal:" + calendarId; }
        private static string StoreKey(string key) { return "google:" + key; }

        private static string CacheFile(string key)
        {
            using (var sha = SHA1.Create())
            {
                string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
                return Path.Combine(CacheDir, "google-" + hash + ".tsv");
            }
        }

        // ---------- 給設定視窗用 ----------

        // 使用內建用戶端時，輸入框留空（不顯示內建的值）
        public string ClientId { get { return _secrets.UsingBuiltIn ? "" : _secrets.ClientId; } }
        public string ClientSecret { get { return _secrets.UsingBuiltIn ? "" : _secrets.ClientSecret; } }
        public bool HasBuiltInClient { get { return GoogleSecrets.BuiltIn != null; } }
        public string AuthUrl { get { return _auth.AuthUrl; } }
        public bool HasWriteScope { get { return _auth.HasWriteScope; } }
        public bool Adding { get { return _adding; } }

        // 測試用：把「開瀏覽器」換成別的動作
        internal Action<string> BrowserOpener { set { _auth.OpenBrowser = value; } }

        // 可以新增行程的 Google 日曆：有顯示的、而且你有編輯權限的（還不知道有沒有權限的，先讓你選，真的沒權限時 Google 會回錯誤）
        public List<GoogleCalendarEntry> WritableCalendars()
        {
            return _config.Calendars.Where(c => c.Enabled && c.CanWrite != 0).Select(c => c.Clone()).ToList();
        }
        public bool HasCredentials { get { return _auth.HasCredentials; } }
        public bool IsSignedIn { get { return _auth.IsSignedIn; } }
        public bool Connecting { get { return _connecting; } }
        public bool NeedsSignIn { get { return _needsSignIn; } }
        public GoogleConfig Config { get { return _config.Clone(); } }

        public void SetClientCredentials(string clientId, string clientSecret)
        {
            clientId = (clientId ?? "").Trim();
            clientSecret = (clientSecret ?? "").Trim();
            if (clientId == ClientId && clientSecret == ClientSecret) return;
            _secrets.ClientId = clientId;
            _secrets.ClientSecret = clientSecret;
            _secrets.UseBuiltInIfEmpty();   // 兩格都清空 = 改回用內建的
            _secrets.Save();
            _auth.Invalidate();
            RaiseStatusChanged();
        }

        public string StatusText()
        {
            if (!_auth.HasCredentials) return "尚未填入用戶端 ID 和用戶端密鑰";
            if (_connecting) return "等待你在瀏覽器完成登入…（如果瀏覽器沒有自動開啟，可以按「複製登入網址」，貼到你慣用的瀏覽器）";
            if (!_auth.IsSignedIn) return _error != null ? "連接失敗：" + _error : "尚未連接";
            if (_needsSignIn) return "登入已失效，請重新連接" + (_error != null ? "（" + _error + "）" : "");

            string account = "已連接：" + (_secrets.Email.Length > 0 ? _secrets.Email : "（帳號未知）");
            string state;
            if (_fetching) state = "更新中…";
            else if (_lastFetch.HasValue) state = "上次更新 " + _lastFetch.Value.ToString("yyyy-MM-dd HH:mm") + "（" + _items.Values.Sum(l => l.Count) + " 筆）";
            else state = "尚未更新";
            return account + "　" + state + (_error != null ? "　更新失敗：" + _error : "");
        }

        // ---------- 啟動 ----------

        public void Start()
        {
            try
            {
                if (File.Exists(MetaFile))
                {
                    long ticks;
                    if (long.TryParse(File.ReadAllText(MetaFile).Trim(), out ticks)) _lastFetch = new DateTime(ticks, DateTimeKind.Local);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // 先顯示快取（離線或網路慢時也看得到上次的內容）
            bool missingCache = false;
            foreach (string key in WantedKeys())
            {
                var cached = LoadCache(key);
                if (cached == null)
                {
                    missingCache = true;
                    continue;
                }
                _items[key] = cached;
                _store.SetSource(StoreKey(key), cached);
            }
            if (missingCache) _lastFetch = null;   // 有日曆沒有（可用的）快取，不要等到下次更新時間，馬上抓
            RaiseChanged();
            RaiseStatusChanged();

            if (IsDue(DateTime.Now)) Fetch();
            ScheduleNext();

            // 舊版存的日曆清單沒有「編輯權限」的資料：背景默默補上，這樣右鍵新增行程時才知道哪些日曆可以選
            if (_auth.IsSignedIn && _config.Calendars.Any(c => c.CanWrite < 0)) ReloadCalendars(true);
        }

        private IEnumerable<string> WantedKeys()
        {
            foreach (var cal in _config.Calendars.Where(c => c.Enabled)) yield return CalKey(cal.Id);
            if (_config.ShowTasks) yield return TasksKey;
        }

        // ---------- 連接與中斷 ----------

        public void Connect()
        {
            if (_connecting || _adding) return;   // 正在等寫入授權時再開一個登入，兩個流程會互相蓋掉對方的狀態
            if (!_auth.HasCredentials)
            {
                _error = "請先填入用戶端 ID 和用戶端密鑰";
                RaiseStatusChanged();
                return;
            }

            _connecting = true;
            _error = null;
            RaiseStatusChanged();

            Task.Run(() =>
            {
                string error = null, email = null;
                bool authorized = false;
                List<GoogleCalendarEntry> calendars = null;
                try
                {
                    _auth.Authorize(TimeSpan.FromMinutes(3));
                    authorized = true;
                    calendars = _client.ListCalendars(out email);
                }
                catch (GoogleException ex) { error = ex.Message; }
                catch (Exception ex) { error = GoogleHttp.Describe(ex); }

                _ui.BeginInvoke(new Action(delegate
                {
                    _connecting = false;
                    // 授權成功就代表新的登入憑證已經存好了；接下來抓日曆清單失敗，也不能繼續顯示「登入已失效」
                    if (authorized) _needsSignIn = false;
                    if (error != null)
                    {
                        _error = error;
                        RaiseStatusChanged();
                        if (authorized) { _retryAfter = DateTime.Now + RetryDelay; ScheduleNext(); }
                        return;
                    }

                    _secrets.Email = email ?? "";
                    _secrets.Save();
                    _needsSignIn = false;
                    _error = null;
                    MergeCalendars(calendars);
                    _config.Save();
                    RaiseCalendarsChanged();
                    RaiseStatusChanged();
                    _retryAfter = DateTime.MinValue;
                    Fetch();
                    ScheduleNext();
                }));
            });
        }

        public void CancelConnect()
        {
            _auth.Cancel();
        }

        // ---------- 共用的「帳號」介面（ICalendarAccount）：新增／修改／刪除視窗只認這個 ----------

        string ICalendarAccount.Name { get { return "Google"; } }
        bool ICalendarAccount.HasWriteAccess { get { return _auth.HasWriteScope; } }
        string ICalendarAccount.DefaultReminderNote { get { return "提醒會使用這個日曆的預設提醒。"; } }

        string ICalendarAccount.WriteAuthNotice(bool editing)
        {
            return editing
                ? "修改 Google 行程需要多一個授權（只需要一次）：按「儲存」後會開啟瀏覽器，請按「允許」。"
                : "第一次新增到 Google 日曆，需要多一個授權（只需要一次）：按「新增」後會開啟瀏覽器，請按「允許」。之後就不用再授權。";
        }

        List<AccountCalendar> ICalendarAccount.WritableCalendars()
        {
            return WritableCalendars().Select(c => new AccountCalendar { StoreId = CalendarIds.Google(c.Id), RemoteId = c.Id, Name = c.Name }).ToList();
        }

        AccountCalendar ICalendarAccount.WritableCalendarOf(CalendarItem item)
        {
            string id = WritableCalendarIdOf(item);
            if (id == null) return null;
            var cal = _config.Calendars.FirstOrDefault(c => c.Id == id);
            return new AccountCalendar { StoreId = CalendarIds.Google(id), RemoteId = id, Name = cal != null ? cal.Name : id };
        }

        void ICalendarAccount.AddEvent(string remoteCalendarId, EventDraft d, Action<string> progress, Action<string> done)
        {
            AddEvent(remoteCalendarId, d.Title, d.AllDay, d.Start, d.End, progress, done);
        }

        void ICalendarAccount.UpdateEvent(string remoteCalendarId, string eventId, EventDraft d, Action<string> progress, Action<string> done)
        {
            UpdateEvent(remoteCalendarId, eventId, d.Title, d.AllDay, d.Start, d.End, progress, done);
        }

        void ICalendarAccount.DeleteEvent(string remoteCalendarId, string eventId, Action<string> progress, Action<string> done)
        {
            DeleteEvent(remoteCalendarId, eventId, progress, done);
        }

        void ICalendarAccount.CancelPendingAuthorization()
        {
            CancelConnect();
        }

        // ---------- 新增行程 ----------

        // 在指定的 Google 日曆新增行程。如果還沒有「寫入」授權，會先開瀏覽器請你多授權一次（只有第一次）。
        // progress：進度訊息（例如「請在瀏覽器完成授權」）；done：完成時呼叫，參數是錯誤訊息，成功是 null。兩個都在 UI 執行緒上呼叫
        public void AddEvent(string calendarId, string title, bool allDay, DateTime start, DateTime end, Action<string> progress, Action<string> done)
        {
            RunWrite("第一次新增到 Google 需要多一個授權：請在瀏覽器按「允許」…",
                delegate { _client.CreateEvent(calendarId, title, allDay, start, end); }, progress, done);
        }

        // 修改 Google 行程的標題與時間（重複行程只改那一次）。參數意義同 AddEvent
        public void UpdateEvent(string calendarId, string eventId, string title, bool allDay, DateTime start, DateTime end, Action<string> progress, Action<string> done)
        {
            RunWrite("需要多一個授權才能修改 Google 日曆：請在瀏覽器按「允許」…",
                delegate { _client.UpdateEvent(calendarId, eventId, title, allDay, start, end); }, progress, done);
        }

        // 這個項目能不能從程式裡改或刪：必須是 Google 日曆的行程（有 id）、而且那個日曆你有編輯權限。回傳 Google 的日曆 id，不行就回 null
        public string WritableCalendarIdOf(CalendarItem item)
        {
            if (item == null || item.Id == null || item.CalendarId == null || item.Kind != ItemKind.Event && item.Kind != ItemKind.Holiday) return null;
            var cal = _config.Calendars.FirstOrDefault(c => CalendarIds.Google(c.Id) == item.CalendarId);
            return cal != null && cal.CanWrite == 1 ? cal.Id : null;
        }

        // 刪除 Google 行程（重複行程只刪那一次）。還沒有「寫入」授權時，會先請你授權一次。參數意義同 AddEvent
        public void DeleteEvent(string calendarId, string eventId, Action<string> progress, Action<string> done)
        {
            RunWrite("需要多一個授權才能修改 Google 日曆：請在瀏覽器按「允許」…",
                delegate { _client.DeleteEvent(calendarId, eventId); }, progress, done);
        }

        // 新增／刪除共用：必要時先取得寫入授權，執行動作，成功後馬上重新抓一次
        private void RunWrite(string authMessage, Action action, Action<string> progress, Action<string> done)
        {
            if (_adding || _connecting)
            {
                done("目前正在處理 Google 的另一個動作，請稍後再試");
                return;
            }
            _adding = true;
            RaiseStatusChanged();

            Task.Run(() =>
            {
                string error = null;
                try
                {
                    if (!_auth.HasWriteScope)
                    {
                        _ui.BeginInvoke(new Action(delegate { progress(authMessage); }));
                        _auth.AuthorizeWrite(TimeSpan.FromMinutes(3));
                    }
                    action();
                }
                catch (GoogleException ex) { error = ex.Message; }
                catch (Exception ex) { error = GoogleHttp.Describe(ex); }

                _ui.BeginInvoke(new Action(delegate
                {
                    _adding = false;
                    if (error == null)
                    {
                        _retryAfter = DateTime.MinValue;
                        Fetch();   // 成功：馬上重新抓，讓新增／刪除的結果出現在月曆上
                    }
                    RaiseStatusChanged();
                    done(error);
                }));
            });
        }

        public void ReloadCalendars()
        {
            ReloadCalendars(false);
        }

        // quiet = true：背景默默更新日曆清單（例如補上「有沒有編輯權限」），失敗也不要顯示錯誤
        private void ReloadCalendars(bool quiet)
        {
            if (!_auth.IsSignedIn) return;
            Task.Run(() =>
            {
                string error = null, email = null;
                List<GoogleCalendarEntry> calendars = null;
                try { calendars = _client.ListCalendars(out email); }
                catch (GoogleException ex) { error = ex.Message; if (ex.NeedsSignIn && !quiet) _ui.BeginInvoke(new Action(delegate { _needsSignIn = true; })); }
                catch (Exception ex) { error = GoogleHttp.Describe(ex); }

                _ui.BeginInvoke(new Action(delegate
                {
                    if (error != null)
                    {
                        if (quiet) return;
                        _error = error;
                        RaiseStatusChanged();
                        return;
                    }
                    _error = null;
                    MergeCalendars(calendars);
                    _config.Save();
                    RaiseCalendarsChanged();
                    RaiseStatusChanged();
                    _retryAfter = DateTime.MinValue;
                    Fetch();
                }));
            });
        }

        // 新出現的日曆加進來，已經設定過的保留你的選擇，只更新名稱；Google 上已經不存在的就移除
        private void MergeCalendars(List<GoogleCalendarEntry> remote)
        {
            var known = _config.Calendars.ToDictionary(c => c.Id);
            var merged = new List<GoogleCalendarEntry>();
            foreach (var r in remote)
            {
                GoogleCalendarEntry existing;
                if (known.TryGetValue(r.Id, out existing))
                {
                    existing.Name = r.Name;
                    existing.CanWrite = r.CanWrite;   // 編輯權限可能變了
                    merged.Add(existing);
                }
                else merged.Add(r);
            }

            var keepIds = new HashSet<string>(merged.Select(m => m.Id));
            foreach (var gone in _config.Calendars.Where(c => !keepIds.Contains(c.Id)))
                RemoveItems(CalKey(gone.Id));
            _config.Calendars = merged;
        }

        public void Disconnect()
        {
            _generation++;   // 還在跑的抓取與還沒存的快取，結果都作廢
            _refetchRequested = false;
            _auth.SignOut();
            foreach (string key in _items.Keys.ToList()) RemoveItems(key);
            _config.Calendars.Clear();
            _config.Save();
            _lastFetch = null;
            _error = null;
            _needsSignIn = false;
            _timer.Stop();
            try { if (File.Exists(MetaFile)) File.Delete(MetaFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            RaiseChanged();
            RaiseCalendarsChanged();
            RaiseStatusChanged();
        }

        // ---------- 儲存設定 ----------

        public void ApplyConfig(List<GoogleCalendarEntry> calendars, bool showTasks, string tasksColor, int intervalMinutes)
        {
            var old = _config;
            _config = new GoogleConfig
            {
                Calendars = calendars.Select(c => c.Clone()).ToList(),
                ShowTasks = showTasks,
                TasksColor = tasksColor,
                IntervalMinutes = intervalMinutes
            };
            _config.Save();

            var wanted = new HashSet<string>(WantedKeys());
            foreach (string key in _items.Keys.ToList())
            {
                if (!wanted.Contains(key)) RemoveItems(key);
            }

            // 顏色或類型改了：直接改記憶體裡已經有的項目，不需要重新上網
            var oldById = old.Calendars.ToDictionary(c => c.Id);
            foreach (var cal in _config.Calendars.Where(c => c.Enabled))
            {
                GoogleCalendarEntry before;
                List<CalendarItem> items;
                if (!oldById.TryGetValue(cal.Id, out before) || !_items.TryGetValue(CalKey(cal.Id), out items)) continue;
                if (before.Color == cal.Color && before.Kind == cal.Kind) continue;
                foreach (var item in items) { item.Color = cal.Color; item.Kind = cal.Kind; }
                _store.SetSource(StoreKey(CalKey(cal.Id)), items);
                SaveCache(CalKey(cal.Id), items);
            }
            List<CalendarItem> taskItems;
            if (showTasks && old.TasksColor != tasksColor && _items.TryGetValue(TasksKey, out taskItems))
            {
                foreach (var item in taskItems) item.Color = tasksColor;
                _store.SetSource(StoreKey(TasksKey), taskItems);
                SaveCache(TasksKey, taskItems);
            }

            RaiseChanged();
            RaiseStatusChanged();

            if (_auth.IsSignedIn && wanted.Any(k => !_items.ContainsKey(k)))
            {
                _retryAfter = DateTime.MinValue;
                Fetch();
            }
            ScheduleNext();
        }

        public void RefreshNow()
        {
            _retryAfter = DateTime.MinValue;
            Fetch();
        }

        public void RefreshDue()
        {
            if (IsDue(DateTime.Now)) Fetch();
            ScheduleNext();
        }

        // ---------- 排程 ----------

        private bool IsDue(DateTime now)
        {
            if (!_auth.IsSignedIn || _needsSignIn || _fetching || now < _retryAfter) return false;
            if (!_lastFetch.HasValue) return true;
            return _config.IntervalMinutes > 0 && now - _lastFetch.Value >= TimeSpan.FromMinutes(_config.IntervalMinutes);
        }

        // 只排「下一個到期時間」的單一計時器，不輪詢
        private void ScheduleNext()
        {
            _timer.Stop();
            if (!_auth.IsSignedIn || _needsSignIn || _fetching || _config.IntervalMinutes <= 0) return;

            DateTime due = _lastFetch.HasValue ? _lastFetch.Value.AddMinutes(_config.IntervalMinutes) : DateTime.Now;
            if (due < _retryAfter) due = _retryAfter;
            TimeSpan wait = due - DateTime.Now;
            if (wait < TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
            _timer.Interval = wait;
            _timer.Start();
        }

        // ---------- 抓取 ----------

        private void Fetch()
        {
            if (!_auth.IsSignedIn) return;
            if (_fetching)
            {
                // 正在抓的是「變動之前」的資料：新增、刪除、改設定之後要的是新的，這一輪結束後再抓一次，不然剛刪掉的行程會又被舊結果蓋回來
                _refetchRequested = true;
                return;
            }
            _fetching = true;
            RaiseStatusChanged();

            int generation = _generation;
            var cfg = _config.Clone();
            Task.Run(() =>
            {
                var results = new Dictionary<string, List<CalendarItem>>();
                var errors = new List<string>();
                bool needsSignIn = false;
                DateTime from = LoadRange.From;
                DateTime to = LoadRange.To;

                foreach (var cal in cfg.Calendars.Where(c => c.Enabled))
                {
                    if (needsSignIn) break;
                    try { results[CalKey(cal.Id)] = _client.FetchEvents(cal.Id, cal.Color, cal.Kind, from, to); }
                    catch (GoogleException ex) { errors.Add(cal.Name + "：" + ex.Message); needsSignIn |= ex.NeedsSignIn; }
                    catch (Exception ex) { errors.Add(cal.Name + "：" + GoogleHttp.Describe(ex)); }
                }
                if (!needsSignIn && cfg.ShowTasks)
                {
                    try { results[TasksKey] = _client.FetchTasks(cfg.TasksColor, from, to); }
                    catch (GoogleException ex) { errors.Add("Google 待辦：" + ex.Message); needsSignIn |= ex.NeedsSignIn; }
                    catch (Exception ex) { errors.Add("Google 待辦：" + GoogleHttp.Describe(ex)); }
                }

                _ui.BeginInvoke(new Action(delegate { ApplyFetch(results, errors, needsSignIn, generation); }));
            });
        }

        private void ApplyFetch(Dictionary<string, List<CalendarItem>> results, List<string> errors, bool needsSignIn, int generation)
        {
            _fetching = false;

            // 抓的期間中斷連接了：結果整批丟掉（包含不能把快取寫回磁碟，隱私權政策承諾中斷連接會刪除本機的行程快取）
            if (generation != _generation)
            {
                _refetchRequested = false;
                RaiseStatusChanged();
                if (_auth.IsSignedIn) Fetch();   // 中斷後又重新連接了：用新的帳號重抓
                return;
            }

            var wanted = new HashSet<string>(WantedKeys());

            foreach (var pair in results)
            {
                if (!wanted.Contains(pair.Key)) continue;   // 抓的期間被取消顯示了，結果丟掉

                // 抓的期間顏色或類型可能又改了，以目前的設定為準
                ApplyCurrentStyle(pair.Key, pair.Value);
                _items[pair.Key] = pair.Value;
                _store.SetSource(StoreKey(pair.Key), pair.Value);

                // 確認這份結果會被採用之後才存快取（在背景存；存之前再確認一次沒有被中斷連接）
                string cacheKey = pair.Key;
                List<CalendarItem> cacheItems = pair.Value;
                Task.Run(() => { if (generation == _generation) SaveCache(cacheKey, cacheItems); });
            }

            _needsSignIn = needsSignIn;
            if (errors.Count == 0)
            {
                _error = null;
                _lastFetch = DateTime.Now;
                _retryAfter = DateTime.MinValue;
                SaveMeta();
            }
            else
            {
                _error = string.Join("；", errors.Take(2)) + (errors.Count > 2 ? "…" : "");
                _retryAfter = DateTime.Now + RetryDelay;   // 保留舊資料，稍後再試
            }

            RaiseChanged();
            RaiseStatusChanged();

            // 抓的期間又勾選了新的日曆、或有新增／刪除行程：補抓一次
            bool refetch = _refetchRequested;
            _refetchRequested = false;
            if (refetch || (errors.Count == 0 && wanted.Any(k => !_items.ContainsKey(k)))) Fetch();
            ScheduleNext();
        }

        // 抓下來或讀快取的項目，一律以目前的設定（顏色、類型）和所屬日曆代號為準
        private void ApplyCurrentStyle(string key, List<CalendarItem> items)
        {
            if (key == TasksKey)
            {
                foreach (var i in items) { i.Color = _config.TasksColor; i.CalendarId = CalendarIds.GoogleTasks; }
                return;
            }
            var cal = _config.Calendars.FirstOrDefault(c => CalKey(c.Id) == key);
            if (cal == null) return;
            string calendarId = CalendarIds.Google(cal.Id);
            foreach (var i in items) { i.Color = cal.Color; i.Kind = cal.Kind; i.CalendarId = calendarId; }
        }

        private void RemoveItems(string key)
        {
            _items.Remove(key);
            _store.RemoveSource(StoreKey(key));
            try { if (File.Exists(CacheFile(key))) File.Delete(CacheFile(key)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---------- 快取 ----------
        // 一行一筆：日期、時間、種類、顏色、是否完成、開始時間、是否整天、提醒（分鐘，逗號分隔）、行程 id、是否重複行程、結束時間、標題
        // 舊版（6 欄沒有提醒資料、9 欄沒有行程 id、11 欄沒有結束時間）的快取視為過期，會重新抓一次

        private const int CacheFields = 12;

        private static void SaveCache(string key, List<CalendarItem> items)
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllLines(CacheFile(key), items.Select(i =>
                    i.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + (i.TimeText ?? "") + "\t" + (int)i.Kind + "\t" +
                    i.Color + "\t" + (i.Done ? "1" : "0") + "\t" +
                    (i.Start.HasValue ? i.Start.Value.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) : "") + "\t" +
                    (i.AllDay ? "1" : "0") + "\t" +
                    (i.Reminders != null ? string.Join(",", i.Reminders) : "") + "\t" +
                    (i.Id ?? "").Replace('\t', ' ') + "\t" + (i.Recurring ? "1" : "0") + "\t" +
                    (i.End.HasValue ? i.End.Value.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) : "") + "\t" +
                    i.Title.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')),
                    new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private List<CalendarItem> LoadCache(string key)
        {
            try
            {
                if (!File.Exists(CacheFile(key))) return null;
                var items = new List<CalendarItem>();
                foreach (string line in File.ReadAllLines(CacheFile(key), Encoding.UTF8))
                {
                    string[] f = line.Split(new[] { '\t' }, CacheFields);
                    DateTime d;
                    int kind;
                    if (f.Length != CacheFields)
                    {
                        if (f.Length == 6 || f.Length == 9 || f.Length == 11) return null;   // 舊格式：缺提醒、行程 id 或結束時間，當作沒有快取
                        continue;
                    }
                    if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                        || !int.TryParse(f[2], out kind)) continue;

                    DateTime start;
                    var item = new CalendarItem
                    {
                        Date = d,
                        TimeText = f[1].Length > 0 ? f[1] : null,
                        Kind = (ItemKind)kind,
                        Color = f[3],
                        Done = f[4] == "1",
                        AllDay = f[6] == "1",
                        Id = f[8].Length > 0 ? f[8] : null,
                        Recurring = f[9] == "1",
                        Title = f[11]
                    };
                    DateTime endTime;
                    if (DateTime.TryParseExact(f[10], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out endTime)) item.End = endTime;
                    if (DateTime.TryParseExact(f[5], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out start)) item.Start = start;
                    if (f[7].Length > 0)
                    {
                        var minutes = new List<int>();
                        foreach (string m in f[7].Split(','))
                        {
                            int n;
                            if (int.TryParse(m, out n)) minutes.Add(n);
                        }
                        if (minutes.Count > 0) item.Reminders = minutes.ToArray();
                    }
                    items.Add(item);
                }
                ApplyCurrentStyle(key, items);
                return items;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private void SaveMeta()
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(MetaFile, DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void RaiseChanged() { var h = Changed; if (h != null) h(); }
        private void RaiseStatusChanged() { var h = StatusChanged; if (h != null) h(); }
        private void RaiseCalendarsChanged() { var h = CalendarsChanged; if (h != null) h(); }
    }
}

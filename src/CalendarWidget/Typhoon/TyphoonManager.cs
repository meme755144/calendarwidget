using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace CalendarWidget
{
    // 颱風假：定時讀取爬蟲產生的停班停課資料，依使用者關注的縣市算出要通知的公告。
    // 功能關閉時沒有計時器、不連網；打開後開機（或從睡眠醒來）立刻讀一次，之後每 10 分鐘讀一次
    // （用 ETag，資料沒變時伺服器只回 304）。讀不到或格式不對時保留上一份資料，不會清空
    internal sealed class TyphoonManager
    {
        public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

        private readonly Dispatcher _ui;
        private readonly Settings _settings;
        private readonly DispatcherTimer _timer;

        private TyphoonData _data;
        private string _signature = "";
        private string _etag;
        private string _etagUrl;         // 這個 ETag 是向哪個網址拿的（網址換了就不能拿來比對）
        private bool _busy;
        private bool _cacheLoaded;
        private DateTime? _lastOk;        // 最近一次讀取成功的時間（本機時間）
        private string _lastError;        // 最近一次讀取失敗的原因；成功就清掉
        private TyphoonWatch _watch = new TyphoonWatch();
        private List<TyphoonAlert> _cache;   // 今天的公告；資料、設定或日期變了就重算
        private DateTime _cacheDay;

        // 公告內容有變（新公告、撤銷、錯誤狀態改變）：要重畫月曆、重新檢查要不要提醒。在 UI 執行緒上引發
        public event Action Changed;
        // 讀取狀態有變（讀取成功、失敗、設定改了）：設定視窗用來更新狀態文字。內容有變時也會引發
        public event Action StatusChanged;

        public TyphoonManager(Dispatcher ui, Settings settings)
        {
            _ui = ui;
            _settings = settings;
            _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = PollInterval };
            _timer.Tick += delegate { RefreshNow(); };
        }

        private static string CachePath { get { return Path.Combine(DataPaths.LocalDir, "typhoon-latest.json"); } }

        private bool Active { get { return _settings.TyphoonEnabled && !Settings.ReadOnly; } }

        // 啟動與設定改變後呼叫：套用開關、關注的縣市、網址
        public void Apply()
        {
            _watch = TyphoonWatch.Parse(_settings.TyphoonWatchList);
            _cache = null;

            if (!Active)
            {
                _timer.Stop();
                RaiseChanged();
                return;
            }

            if (!_cacheLoaded) LoadCache();
            _timer.Start();
            RaiseChanged();
            RefreshNow();
        }

        // 立刻讀一次（正在讀的話略過）
        public void RefreshNow()
        {
            if (!Active || _busy) return;

            string url = TyphoonFeed.ResolveUrl(_settings.TyphoonUrl);
            if (url == null)
            {
                _lastError = "網址不正確（要用 http:// 或 https:// 開頭）";
                RaiseStatus();
                return;
            }

            if (url != _etagUrl)
            {
                _etag = null;
                _etagUrl = url;
            }
            _busy = true;
            string etag = _etag;
            ThreadPool.QueueUserWorkItem(delegate
            {
                TyphoonFeed.Result result = null;
                string error = null;
                try { result = TyphoonFeed.Fetch(url, etag); }
                catch (Exception ex) { error = ex.Message; }   // 網路錯誤、逾時、伺服器錯誤：這一輪放棄，保留舊資料。背景執行緒上的例外不能漏出去
                _ui.BeginInvoke(new Action(delegate { OnFetched(result, error); }));
            });
        }

        // 睡眠醒來、跨日之後呼叫：補讀一次
        public void RefreshDue()
        {
            RefreshNow();
        }

        private void OnFetched(TyphoonFeed.Result result, string error)
        {
            _busy = false;
            if (!Active) return;   // 讀的途中被關掉了

            if (error != null)
            {
                _lastError = "讀取失敗：" + error;
                RaiseStatus();
                return;
            }
            if (result.NotModified)
            {
                _lastOk = DateTime.Now;
                _lastError = null;
                RaiseStatus();
                return;
            }

            string problem;
            var data = TyphoonData.Parse(result.Text, out problem);
            if (data == null)
            {
                _lastError = "資料格式不正確：" + problem;
                RaiseStatus();
                return;
            }

            _lastOk = DateTime.Now;
            _lastError = null;
            _etag = result.ETag;
            SaveCache(result.Text);
            SetData(data);
        }

        private void SetData(TyphoonData data)
        {
            string signature = data.Signature();
            bool changed = signature != _signature;
            _data = data;
            _signature = signature;
            _cache = null;
            if (changed) RaiseChanged();
            else RaiseStatus();
        }

        private void RaiseChanged()
        {
            var changed = Changed;
            if (changed != null) changed();
            RaiseStatus();
        }

        private void RaiseStatus()
        {
            var status = StatusChanged;
            if (status != null) status();
        }

        // 上次讀到的資料存在本機：離線（例如晚上才開機、還沒連上網）時也有東西可以看
        private void LoadCache()
        {
            _cacheLoaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                string problem;
                var data = TyphoonData.Parse(File.ReadAllText(CachePath, Encoding.UTF8), out problem);
                if (data == null) return;
                _data = data;
                _signature = data.Signature();
                _cache = null;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void SaveCache(string text)
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                AtomicFile.WriteAllText(CachePath, text, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // 要通知的公告：功能關閉時一律沒有
        public List<TyphoonAlert> Alerts(DateTime today)
        {
            if (!Active) return new List<TyphoonAlert>();
            if (_cache == null || _cacheDay != today.Date)
            {
                _cache = TyphoonMatcher.Select(_data, _watch, today);
                _cacheDay = today.Date;
            }
            return _cache;
        }

        public bool IsOff(DateTime date)
        {
            return Active && TyphoonMatcher.IsOff(Alerts(DateTime.Today), date);
        }

        public string Describe(DateTime date)
        {
            return Active ? TyphoonMatcher.Describe(Alerts(DateTime.Today), date) : null;
        }

        // 設定視窗顯示的狀態（多行文字）
        public string StatusText()
        {
            if (!_settings.TyphoonEnabled) return "功能目前關閉。";
            if (Settings.ReadOnly) return "示範模式不讀取資料。";

            var lines = new List<string>();
            if (_data == null)
            {
                lines.Add("還沒有讀到資料。");
            }
            else
            {
                if (_data.State == "announced")
                {
                    var cities = _data.Cities.Where(p => p.Value.Any(i => !i.Unknown)).Select(p => p.Key).ToList();
                    lines.Add("目前有停班停課公告的縣市：" + (cities.Count > 0 ? string.Join("、", cities) : "（只有單一學校）") + "（不一定是你關注的）");
                }
                else if (_data.State == "unparsed")
                    lines.Add("人事總處的頁面有停班停課訊息，但程式看不懂它的格式。");
                else
                    lines.Add("目前沒有停班停課公告。");

                if (_data.CheckedAtUtc.HasValue) lines.Add("資料更新時間：" + _data.CheckedAtUtc.Value.ToLocalTime().ToString("M/d HH:mm"));
                if (_data.Error != null) lines.Add("⚠ 資料來源回報錯誤：" + _data.Error.Message + "（目前顯示的是上一次成功的資料）");
                if (TyphoonMatcher.IsStale(_data, DateTime.UtcNow))
                    lines.Add("⚠ 資料已超過 " + (int)TyphoonMatcher.StaleAfter.TotalHours + " 小時沒有更新，資料來源可能停了或被擋了，目前看到的不一定是最新的。");
            }

            if (_lastError != null) lines.Add("⚠ " + _lastError);
            else if (_lastOk.HasValue) lines.Add("最近一次讀取：" + _lastOk.Value.ToString("HH:mm:ss") + "（成功）");
            else if (_busy) lines.Add("讀取中…");
            return string.Join("\n", lines);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;

namespace CalendarWidget
{
    // 一筆「某個行程在某個時間該提醒」
    internal sealed class DueReminder
    {
        public CalendarItem Item;
        public DateTime Due;       // 該提醒的時間
        public DateTime Expires;   // 過了這個時間就不用提醒了（有時間的行程：開始時；整天行程：那天結束）
        public int Minutes;        // 開始前幾分鐘
        public string Key;         // 用來記錄「已經確認過」
    }

    // 已經按過「知道了」的行程提醒，存在 %APPDATA%\CalendarWidget\acked-events.txt（一行一筆：行程開始時間、識別碼）
    internal sealed class EventAckStore
    {
        private readonly Dictionary<string, DateTime> _keys = new Dictionary<string, DateTime>();

        private static string FilePath { get { return Path.Combine(DataPaths.Dir, "acked-events.txt"); } }

        public static EventAckStore Load(DateTime now)
        {
            var store = new EventAckStore();
            if (Settings.ReadOnly) return store;
            try
            {
                if (!File.Exists(FilePath)) return store;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string[] f = line.Split(new[] { '\t' }, 2);
                    DateTime start;
                    if (f.Length != 2 || !DateTime.TryParseExact(f[0], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out start)) continue;
                    if (start >= now.AddDays(-3)) store._keys[f[1]] = start;   // 已經過去很久的行程，紀錄用不到了
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return store;
        }

        public bool IsAcked(string key)
        {
            return _keys.ContainsKey(key);
        }

        public void Ack(IEnumerable<DueReminder> dues)
        {
            foreach (var d in dues) _keys[d.Key] = d.Item.Start ?? d.Due;
            Save();
        }

        private void Save()
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(DataPaths.Dir);
                string temp = FilePath + ".tmp";
                File.WriteAllLines(temp, _keys.OrderBy(k => k.Value).Select(k => k.Value.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) + "\t" + k.Key), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(temp, FilePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // 行程提醒的規則（不碰畫面，方便單獨測試）
    internal static class EventReminderLogic
    {
        internal static string KeyOf(CalendarItem item, int minutes)
        {
            return (item.CalendarId ?? "") + "|" + item.Start.Value.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + "|" + item.Title + "|" + minutes;
        }

        // 從昨天到 daysAhead 天後的所有「行程」（類型是行程的日曆；節日有自己的提醒，待辦不提醒），依提醒方式算出各自的提醒時間：
        //   依各行程自己的設定：用行程自己帶的提醒（Google 的提醒設定、ICS 檔案裡的提醒），沒有的就不提醒
        //   自訂：所有有時間的行程，開始前固定幾分鐘（整天行程沒有「開始前幾分鐘」可言，不提醒）
        //   不提醒：什麼都不產生
        internal static List<DueReminder> Compute(ItemStore store, int mode, int customMinutes, DateTime now, int daysAhead)
        {
            var result = new List<DueReminder>();
            if (mode == Settings.EventReminderOff) return result;

            var seen = new HashSet<string>();
            for (DateTime d = now.Date.AddDays(-1); d <= now.Date.AddDays(daysAhead); d = d.AddDays(1))
            {
                foreach (var item in store.For(d))
                {
                    if (item.Kind != ItemKind.Event || !item.Start.HasValue) continue;

                    IEnumerable<int> minutes;
                    if (mode == Settings.EventReminderCustom) minutes = item.AllDay ? new int[0] : new[] { customMinutes };
                    else minutes = item.Reminders ?? new int[0];

                    foreach (int m in minutes.Distinct())
                    {
                        string key = KeyOf(item, m);
                        if (!seen.Add(key)) continue;
                        DateTime start = item.Start.Value;
                        result.Add(new DueReminder
                        {
                            Item = item,
                            Due = start.AddMinutes(-m),
                            Expires = item.AllDay ? start.AddDays(1) : start,
                            Minutes = m,
                            Key = key
                        });
                    }
                }
            }
            return result;
        }

        // 現在該顯示的：提醒時間到了、還沒過期、沒確認過、沒有被「稍後再提醒」擋住
        internal static List<DueReminder> Pending(IEnumerable<DueReminder> dues, DateTime now, Func<string, bool> isAcked, IDictionary<string, DateTime> snoozedUntil)
        {
            return dues
                .Where(d => d.Due <= now && now < d.Expires && !isAcked(d.Key) && !IsSnoozed(d.Key, now, snoozedUntil))
                .OrderBy(d => d.Item.Start.Value)
                .ThenBy(d => d.Item.Title)
                .ToList();
        }

        private static bool IsSnoozed(string key, DateTime now, IDictionary<string, DateTime> snoozedUntil)
        {
            DateTime until;
            return snoozedUntil.TryGetValue(key, out until) && until > now;
        }

        // 下一次需要醒來看看的時間：下一個還沒到的提醒時間，或者「稍後再提醒」到期的時間
        internal static DateTime? NextWake(IEnumerable<DueReminder> dues, DateTime now, Func<string, bool> isAcked, IDictionary<string, DateTime> snoozedUntil)
        {
            DateTime? next = null;
            foreach (var d in dues)
            {
                if (now >= d.Expires || isAcked(d.Key)) continue;

                DateTime candidate;
                DateTime until;
                if (d.Due > now) candidate = d.Due;
                else if (snoozedUntil.TryGetValue(d.Key, out until) && until > now) candidate = until;
                else continue;

                if (!next.HasValue || candidate < next.Value) next = candidate;
            }
            return next;
        }

        // 同一個行程如果有好幾個提醒同時到時間（例如開始前 60 分鐘和 10 分鐘），提醒框裡只列一行
        internal static List<DueReminder> OnePerEvent(IEnumerable<DueReminder> pending)
        {
            return pending
                .GroupBy(d => (d.Item.CalendarId ?? "") + "|" + d.Item.Start.Value.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + "|" + d.Item.Title)
                .Select(g => g.OrderBy(d => d.Minutes).First())
                .OrderBy(d => d.Item.Start.Value)
                .ThenBy(d => d.Item.Title)
                .ToList();
        }

        // 提醒框裡每一行的文字，例如「14:00  軟體園區主任來訪（10 分鐘後開始）」
        internal static string LineText(DueReminder d, DateTime now)
        {
            var item = d.Item;
            if (item.AllDay)
            {
                string day = item.Start.Value.Date == now.Date ? "今天" : item.Start.Value.Date == now.Date.AddDays(1) ? "明天" : item.Start.Value.ToString("M/d");
                return "全天  " + item.Title + "（" + day + "）";
            }

            int minutes = (int)Math.Ceiling((item.Start.Value - now).TotalMinutes);
            string remaining;
            if (minutes <= 0) remaining = "現在開始";
            else if (minutes < 60) remaining = minutes + " 分鐘後開始";
            else if (minutes % 60 == 0) remaining = minutes / 60 + " 小時後開始";
            else remaining = minutes / 60 + " 小時 " + minutes % 60 + " 分鐘後開始";
            return item.TimeText + "  " + item.Title + "（" + remaining + "）";
        }
    }

    // 行程提醒：算出下一個要提醒的時間，只排一個計時器；到了就跳出提醒框。
    // 提醒框一次只會有一個，內容會更新；「知道了」記下來之後不再提醒，「稍後再提醒」過幾分鐘再跳
    internal sealed class EventReminder
    {
        private const int DaysAhead = 8;
        private const int SnoozeMinutes = 5;
        private static readonly TimeSpan MaxWait = TimeSpan.FromHours(6);   // 就算沒有任何事，每隔一陣子也重新看一次

        private readonly ItemStore _store;
        private readonly Settings _settings;
        private readonly EventAckStore _acks;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly Dictionary<string, DateTime> _snoozed = new Dictionary<string, DateTime>();
        private ReminderWindow _window;
        private List<DueReminder> _shown = new List<DueReminder>();

        public EventReminder(ItemStore store, Settings settings, EventAckStore acks)
        {
            _store = store;
            _settings = settings;
            _acks = acks;
            _timer.Tick += delegate { Recheck(); };
        }

        // 資料更新了、設定改了、睡眠醒來時呼叫：重新看一次，並排好下一次
        public void Recheck()
        {
            CheckNow();
            ScheduleNext();
        }

        private List<DueReminder> Dues(DateTime now)
        {
            return EventReminderLogic.Compute(_store, _settings.EventReminderMode, _settings.EventReminderMinutes, now, DaysAhead);
        }

        public void CheckNow()
        {
            DateTime now = DateTime.Now;
            var pending = EventReminderLogic.Pending(Dues(now), now, _acks.IsAcked, _snoozed);
            if (pending.Count == 0)
            {
                CloseWindow();
                return;
            }

            _shown = pending;
            if (_window == null)
            {
                _window = new ReminderWindow();
                _window.Acknowledged += delegate
                {
                    _acks.Ack(_shown);
                    CloseWindow();
                    ScheduleNext();
                };
                _window.Snoozed += delegate
                {
                    DateTime t = DateTime.Now;
                    foreach (var d in _shown)
                    {
                        DateTime until = t.AddMinutes(SnoozeMinutes);
                        _snoozed[d.Key] = until < d.Expires ? until : d.Expires;   // 不會晚過行程開始
                    }
                    CloseWindow();
                    ScheduleNext();
                };
            }

            // 一個行程只列一行；「知道了」、「稍後再提醒」仍然套用到它所有同時到時間的提醒（_shown）
            var lines = EventReminderLogic.OnePerEvent(pending);
            _window.ShowOrUpdate(
                lines.Count == 1 ? "行程提醒" : "行程提醒（" + lines.Count + " 個）",
                "現在 " + now.ToString("HH:mm"),
                lines.Select(d => new ReminderLine { Text = EventReminderLogic.LineText(d, now), Color = d.Item.Color }).ToList(),
                "知道了",
                SnoozeMinutes + " 分鐘後再提醒");
        }

        private void ScheduleNext()
        {
            _timer.Stop();
            DateTime now = DateTime.Now;
            DateTime? wake = EventReminderLogic.NextWake(Dues(now), now, _acks.IsAcked, _snoozed);

            DateTime limit = now + MaxWait;
            if (_window != null) limit = now.AddSeconds(30);   // 提醒框開著時，每 30 秒更新一次「還有幾分鐘」
            if (!wake.HasValue || wake.Value > limit) wake = limit;

            TimeSpan wait = wake.Value - now + TimeSpan.FromSeconds(1);
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            _timer.Interval = wait;
            _timer.Start();
        }

        private void CloseWindow()
        {
            if (_window == null) return;
            var w = _window;
            _window = null;
            w.Close();
        }
    }
}

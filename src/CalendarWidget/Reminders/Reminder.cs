using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;

namespace CalendarWidget
{
    // 已經按過「好」的節日提醒，存在 %APPDATA%\CalendarWidget\acked.txt（一行一筆：日期、標題）
    internal sealed class AckStore
    {
        private readonly HashSet<string> _keys = new HashSet<string>();
        private readonly string _fileName;

        // 節日提醒用 acked.txt；颱風假通知用另一個檔案（typhoon-acked.txt），兩邊的紀錄不會混在一起
        private AckStore(string fileName) { _fileName = fileName; }

        private string FilePath { get { return Path.Combine(DataPaths.Dir, _fileName); } }

        private static string Key(DateTime date, string title)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + (title ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        public static AckStore Load(DateTime today)
        {
            return Load(today, "acked.txt");
        }

        public static AckStore Load(DateTime today, string fileName)
        {
            var store = new AckStore(fileName);
            if (Settings.ReadOnly) return store;
            try
            {
                if (!File.Exists(store.FilePath)) return store;
                DateTime oldest = today.AddDays(-60);   // 太舊的紀錄用不到，順便清掉
                foreach (string line in File.ReadAllLines(store.FilePath, Encoding.UTF8))
                {
                    string[] f = line.Split(new[] { '\t' }, 2);
                    DateTime d;
                    if (f.Length != 2 || !DateTime.TryParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
                    if (d >= oldest) store._keys.Add(Key(d, f[1]));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return store;
        }

        public bool IsAcked(CalendarItem item)
        {
            return IsAcked(item.Date, item.Title);
        }

        public bool IsAcked(DateTime date, string title)
        {
            return _keys.Contains(Key(date, title));
        }

        public void Ack(IEnumerable<CalendarItem> items)
        {
            Ack(items.Select(i => new KeyValuePair<DateTime, string>(i.Date, i.Title)));
        }

        public void Ack(IEnumerable<KeyValuePair<DateTime, string>> keys)
        {
            foreach (var key in keys) _keys.Add(Key(key.Key, key.Value));
            Save();
        }

        private void Save()
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(DataPaths.Dir);
                AtomicFile.WriteAllLines(FilePath, _keys.OrderBy(k => k), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // 節日提醒：依設定的間隔與時段檢查，「明天」有還沒確認的節日就跳出提醒框。
    // 提醒框一次只會有一個：上一次提醒沒處理的話，直接把同一個框的內容更新，不會再開第二個。
    internal sealed class HolidayReminder
    {
        private readonly ItemStore _store;
        private readonly Settings _settings;
        private readonly AckStore _acks;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private ReminderWindow _window;
        private List<CalendarItem> _shown = new List<CalendarItem>();   // 目前提醒框裡列出的節日

        public HolidayReminder(ItemStore store, Settings settings, AckStore acks)
        {
            _store = store;
            _settings = settings;
            _acks = acks;
            _timer.Tick += delegate
            {
                CheckNow();
                ScheduleNext();
            };
        }

        // 只排「下一次檢查」的單一計時器，不輪詢
        public void Start()
        {
            ScheduleNext();
        }

        // 設定改了、睡眠醒來時，重新對齊下一次檢查的時間
        public void ScheduleNext()
        {
            _timer.Stop();
            DateTime now = DateTime.Now;
            DateTime next = NextCheck(now, _settings.ReminderIntervalMinutes, _settings.ReminderStartHour, _settings.ReminderEndHour);
            _timer.Interval = next - now + TimeSpan.FromSeconds(1);
            _timer.Start();
        }

        // 下一次檢查的時間：每天從「開始時間」起，每隔 interval 分鐘檢查一次，到「結束時間」前為止（結束時間不含）
        // 例如 9:00 到 18:00、每 2 小時：9:00、11:00、13:00、15:00、17:00，下一天再從 9:00 開始
        internal static DateTime NextCheck(DateTime now, int intervalMinutes, int startHour, int endHour)
        {
            if (intervalMinutes < 1) intervalMinutes = 60;
            if (startHour < 0 || startHour > 23 || endHour < 1 || endHour > 24 || startHour >= endHour)
            {
                startHour = 0;
                endHour = 24;
            }

            for (int dayOffset = 0; dayOffset <= 1; dayOffset++)
            {
                DateTime day = now.Date.AddDays(dayOffset);
                DateTime end = day.AddHours(endHour);
                for (DateTime t = day.AddHours(startHour); t < end; t = t.AddMinutes(intervalMinutes))
                {
                    if (t > now) return t;
                }
            }
            return now.Date.AddDays(1).AddHours(startHour);
        }

        // 明天、種類是「節日」、還沒按過「好」的項目（同名只算一個）
        internal static List<CalendarItem> Pending(ItemStore store, AckStore acks, DateTime today)
        {
            return store.For(today.AddDays(1))
                .Where(i => i.Kind == ItemKind.Holiday && !acks.IsAcked(i))
                .GroupBy(i => i.Title.Trim())
                .Select(g => g.First())
                .ToList();
        }

        // 按鈕上的文字：距離下一次提醒還有多久；下一次已經是隔天（例如今天的提醒時段結束了），就直接寫隔天幾點
        internal static string SnoozeText(DateTime now, DateTime next)
        {
            if (next.Date != now.Date) return "明天 " + next.ToString("HH:mm") + " 再問我";

            int minutes = Math.Max(1, (int)Math.Ceiling((next - now).TotalMinutes));
            if (minutes < 60) return minutes + " 分鐘後再問我";
            if (minutes % 60 == 0) return minutes / 60 + " 小時後再問我";
            return minutes / 60 + " 小時 " + minutes % 60 + " 分鐘後再問我";
        }

        public void CheckNow()
        {
            if (!_settings.HolidayReminder)
            {
                CloseWindow();
                return;
            }

            DateTime now = DateTime.Now;
            var pending = Pending(_store, _acks, now.Date);
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
                    _acks.Ack(_shown);   // 按「好」：框裡列出的這些節日都記成已確認
                    CloseWindow();
                };
                _window.Snoozed += delegate { _window.Hide(); };
            }

            DateTime next = NextCheck(now, _settings.ReminderIntervalMinutes, _settings.ReminderStartHour, _settings.ReminderEndHour);
            DateTime date = pending[0].Date;
            string[] weekdays = { "日", "一", "二", "三", "四", "五", "六" };
            _window.ShowOrUpdate(
                pending.Count == 1 ? "明天是節日" : "明天有 " + pending.Count + " 個節日",
                date.ToString("M 月 d 日") + "（週" + weekdays[(int)date.DayOfWeek] + "）",
                pending.Select(i => new ReminderLine { Text = i.Title, Color = i.Color ?? "#FF6B7A" }).ToList(),
                "好",
                SnoozeText(now, next));
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

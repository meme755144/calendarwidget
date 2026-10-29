using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace CalendarWidget
{
    // 「這天放不放假」：用行政院人事行政總處的辦公日曆表（跟「ICS 產生」同一份資料），讓月曆把平日的補假、連假也標成紅字。
    // 每年的資料下載一次存在 %LOCALAPPDATA%\CalendarWidget\holidays\{年}.json；今年與之後的年度每 7 天重新下載一次（官方偶爾會調整）。
    // 沒有資料（還沒下載好、離線、官方還沒公布）時，退回「週六、週日放假」，所以最差也跟以前一樣
    internal sealed class OffDayStore
    {
        public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);
        public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);   // 下載失敗後，這段時間內不再試（沒有網路的時候不要一直連）

        private readonly object _lock = new object();
        private readonly Dictionary<int, Dictionary<DateTime, HolidayEntry>> _years = new Dictionary<int, Dictionary<DateTime, HolidayEntry>>();
        private readonly Dictionary<int, DateTime> _fetchedAt = new Dictionary<int, DateTime>();   // 快取的下載時間（UTC）
        private readonly Dictionary<int, DateTime> _failedAt = new Dictionary<int, DateTime>();    // 最近一次下載失敗的時間（UTC）
        private readonly HashSet<int> _busy = new HashSet<int>();

        // 關掉的話不讀、不下載，只算週六日
        public bool Enabled = true;

        // 背景下載到新資料時引發（在背景執行緒上，要更新畫面的人自己切回 UI 執行緒）
        public event Action Changed;

        private static string CachePath(int year)
        {
            return Path.Combine(DataPaths.LocalDir, "holidays", year.ToString() + ".json");
        }

        // 月曆要畫 from ~ to 這段日期之前先呼叫：載入快取，沒有或太舊的年度就在背景下載
        public void Prefetch(DateTime from, DateTime to, DateTime today)
        {
            if (!Enabled || Settings.ReadOnly) return;
            var allowed = HolidayCalendar.AvailableYears(today);   // 官方還沒公布的年度不用去抓
            for (int year = from.Year; year <= to.Year; year++)
            {
                if (allowed.Contains(year)) Ensure(year, today);
            }
        }

        private void Ensure(int year, DateTime today)
        {
            lock (_lock)
            {
                if (!_years.ContainsKey(year)) LoadCache(year);

                bool have = _years.ContainsKey(year);
                bool stale = !have || (year >= today.Year && DateTime.UtcNow - _fetchedAt[year] > RefreshAfter);   // 過去的年度不會再改，快取一直有效
                if (!stale || _busy.Contains(year)) return;

                DateTime failed;
                if (_failedAt.TryGetValue(year, out failed) && DateTime.UtcNow - failed < RetryAfter) return;
                _busy.Add(year);
            }
            ThreadPool.QueueUserWorkItem(delegate { Fetch(year); });
        }

        // 呼叫端要自己持有 _lock。快取壞了（不是 JSON、天數不對）就當作沒有，之後會重新下載
        private void LoadCache(int year)
        {
            try
            {
                string path = CachePath(year);
                if (!File.Exists(path)) return;
                string error;
                var entries = HolidayCalendar.Parse(File.ReadAllText(path, Encoding.UTF8), year, out error);
                if (entries == null) return;
                _years[year] = ToMap(entries);
                _fetchedAt[year] = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void Fetch(int year)
        {
            bool ok = false;
            try
            {
                string text = HolidayCalendar.FetchText(year);
                string error;
                var entries = HolidayCalendar.Parse(text, year, out error);
                if (entries != null)
                {
                    SaveCache(year, text);
                    lock (_lock)
                    {
                        _years[year] = ToMap(entries);
                        _fetchedAt[year] = DateTime.UtcNow;
                        _failedAt.Remove(year);
                    }
                    ok = true;
                }
            }
            catch (Exception)
            {
                // 網路不通、官方還沒公布、回傳的內容不對：都一樣，這一輪放棄、之後再試。
                // 這裡跑在背景執行緒上，例外不能漏出去（會讓整個程式當掉）
            }

            lock (_lock)
            {
                _busy.Remove(year);
                if (!ok) _failedAt[year] = DateTime.UtcNow;
            }
            if (ok)
            {
                var changed = Changed;
                if (changed != null) changed();
            }
        }

        private static void SaveCache(int year, string text)
        {
            try
            {
                string path = CachePath(year);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (IOException) { }   // 存不下來也沒關係，這次的資料還是會用，只是下次要重抓
            catch (UnauthorizedAccessException) { }
        }

        private static Dictionary<DateTime, HolidayEntry> ToMap(List<HolidayEntry> entries)
        {
            var map = new Dictionary<DateTime, HolidayEntry>();
            foreach (var e in entries) map[e.Date.Date] = e;
            return map;
        }

        private HolidayEntry Find(DateTime date)
        {
            if (!Enabled) return null;
            lock (_lock)
            {
                Dictionary<DateTime, HolidayEntry> year;
                HolidayEntry entry;
                if (_years.TryGetValue(date.Year, out year) && year.TryGetValue(date.Date, out entry)) return entry;
            }
            return null;
        }

        // 這天要不要標成紅字：有資料就照官方的（平日的補假、連假是 true，補班的週六日是 false），沒有資料就看是不是週六日
        public bool IsOff(DateTime date)
        {
            var entry = Find(date);
            if (entry != null) return entry.IsHoliday;
            return date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
        }

        // 這天特別的說明（放在日期標題後面）：節日、補假名稱，或「補班日」；一般的平日與週六日沒有（回傳 null）
        public string Describe(DateTime date)
        {
            var entry = Find(date);
            if (entry == null) return null;
            if (entry.IsHoliday) return entry.Description.Length > 0 ? entry.Description : (entry.IsWeekend ? null : "放假");
            return entry.IsWeekend ? "補班日" : null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CalendarWidget
{
    internal static class DataPaths
    {
        // 測試時可用環境變數 CALENDARWIDGET_DATA 指到別的資料夾，避免動到平常使用的資料
        public static readonly string Override = Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA");

        // 設定與待辦：要保留、可以備份的資料（%APPDATA%，會跟著漫遊）
        public static string Dir
        {
            get
            {
                if (!string.IsNullOrEmpty(Override)) return Override;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CalendarWidget");
            }
        }

        // 快取與登入憑證：只屬於這台電腦、壞了可以重抓（%LOCALAPPDATA%）
        public static string LocalDir
        {
            get
            {
                if (!string.IsNullOrEmpty(Override)) return Path.Combine(Override, "local");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalendarWidget");
            }
        }
    }

    // 每個日曆的固定代號：ICS 來源、Google 日曆、Google 待辦、本機待辦
    internal static class CalendarIds
    {
        public const string Local = "local";
        public const string LocalEvents = "localevent";
        public const string GoogleTasks = "gtasks";
        public static string Ics(string sourceId) { return "ics:" + sourceId; }
        public static string Google(string calendarId) { return "g:" + calendarId; }
    }

    // 日曆的先後順序，存在 %APPDATA%\CalendarWidget\calendar-order.txt（一行一個日曆代號）
    internal static class CalendarOrder
    {
        private static string FilePath { get { return Path.Combine(DataPaths.Dir, "calendar-order.txt"); } }

        public static List<string> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return File.ReadAllLines(FilePath, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new List<string>();
        }

        public static void Save(IEnumerable<string> ids)
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(DataPaths.Dir);
                AtomicFile.WriteAllLines(FilePath, ids.Distinct(), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class LocalEvent
    {
        public string Id;
        public DateTime Date;
        public DateTime EndDate;   // 最後一天（含）；單日行程等於 Date。有時間的行程，結束時間落在這一天
        public bool AllDay;
        public TimeSpan Start;   // 全天行程不用
        public TimeSpan End;
        public string Title;
    }

    // 本機行程：只存在這台電腦，不會同步到任何地方。一行一筆：
    // id、日期、是否全天、開始時間、結束時間、結束日期、標題（Tab 分隔，純文字；舊版沒有「結束日期」欄，讀進來視為單日）。預設提醒：有時間的行程開始前 10 分鐘，全天行程不提醒
    internal sealed class LocalEventStore
    {
        public const int DefaultReminderMinutes = 10;
        public const int MaxSpanDays = 60;   // 全天行程最多展開幾天（月曆上每天一筆）

        private readonly List<LocalEvent> _events = new List<LocalEvent>();

        private static string FilePath { get { return Path.Combine(DataPaths.Dir, "local-events.txt"); } }

        public static LocalEventStore Load()
        {
            var store = new LocalEventStore();
            if (Settings.ReadOnly) return store;
            try
            {
                if (!File.Exists(FilePath)) return store;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    // 新格式 7 欄（第 6 欄是結束日期，第 7 欄是標題）；舊格式 6 欄（第 6 欄就是標題，視為單日）。標題不含 Tab，所以欄數可以用來分辨
                    string[] f = line.Split(new[] { '\t' }, 7);
                    DateTime date;
                    TimeSpan start, end;
                    if (f.Length < 6 || !DateTime.TryParseExact(f[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                    if (!TimeSpan.TryParseExact(f[3], @"hh\:mm", CultureInfo.InvariantCulture, out start)) start = TimeSpan.Zero;
                    if (!TimeSpan.TryParseExact(f[4], @"hh\:mm", CultureInfo.InvariantCulture, out end)) end = start;

                    DateTime endDate = date;
                    if (f.Length == 7 && (!DateTime.TryParseExact(f[5], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out endDate) || endDate < date)) endDate = date;
                    store._events.Add(new LocalEvent { Id = f[0], Date = date, EndDate = endDate, AllDay = f[2] == "1", Start = start, End = end, Title = f.Length == 7 ? f[6] : f[5] });
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return store;
        }

        private static string Clean(string title)
        {
            return (title ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        // endDate：最後一天（含），不給就是單日。有時間的行程，end 是 endDate 那天的結束時間
        public LocalEvent Add(DateTime date, bool allDay, TimeSpan start, TimeSpan end, string title, DateTime? endDate = null)
        {
            var ev = new LocalEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                Date = date.Date,
                EndDate = endDate.HasValue && endDate.Value.Date > date.Date ? endDate.Value.Date : date.Date,
                AllDay = allDay,
                Start = allDay ? TimeSpan.Zero : start,
                End = allDay ? TimeSpan.Zero : end,
                Title = Clean(title)
            };
            _events.Add(ev);
            Save();
            return ev;
        }

        public void Remove(string id)
        {
            if (_events.RemoveAll(e => e.Id == id) > 0) Save();
        }

        // 修改既有的行程（找不到就什麼都不做，回傳 false）
        public bool Update(string id, DateTime date, bool allDay, TimeSpan start, TimeSpan end, string title, DateTime? endDate = null)
        {
            var ev = _events.FirstOrDefault(e => e.Id == id);
            if (ev == null) return false;
            ev.Date = date.Date;
            ev.EndDate = endDate.HasValue && endDate.Value.Date > date.Date ? endDate.Value.Date : date.Date;
            ev.AllDay = allDay;
            ev.Start = allDay ? TimeSpan.Zero : start;
            ev.End = allDay ? TimeSpan.Zero : end;
            ev.Title = Clean(title);
            Save();
            return true;
        }

        // 全天行程跨多天時，每一天各一筆（只有第一天有 Start 和提醒，跟 Google 的行程一樣）；
        // 有時間的行程只放在開始那天，結束時間可以落在後面的日期
        public IEnumerable<CalendarItem> ToItems(string color)
        {
            var items = new List<CalendarItem>();
            foreach (var e in _events)
            {
                int days = e.AllDay ? Math.Max(1, Math.Min(MaxSpanDays, (int)(e.EndDate - e.Date).TotalDays + 1)) : 1;
                for (int i = 0; i < days; i++)
                {
                    DateTime day = e.Date.AddDays(i);
                    items.Add(new CalendarItem
                    {
                        Id = e.Id,
                        Date = day,
                        Title = e.Title,
                        Kind = ItemKind.Event,
                        Color = color,
                        CalendarId = CalendarIds.LocalEvents,
                        AllDay = e.AllDay,
                        End = e.AllDay ? e.EndDate.AddDays(1) : e.EndDate + e.End,
                        Start = e.AllDay ? (i == 0 ? (DateTime?)day : null) : e.Date + e.Start,
                        TimeText = e.AllDay ? null : e.Start.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                        Reminders = e.AllDay ? null : new[] { DefaultReminderMinutes }
                    });
                }
            }
            return items;
        }

        private void Save()
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(DataPaths.Dir);
                AtomicFile.WriteAllLines(FilePath, _events.Select(e =>
                    e.Id + "\t" + e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + (e.AllDay ? "1" : "0") + "\t" +
                    e.Start.ToString(@"hh\:mm", CultureInfo.InvariantCulture) + "\t" + e.End.ToString(@"hh\:mm", CultureInfo.InvariantCulture) + "\t" +
                    e.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + e.Title),
                    new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class LocalTodo
    {
        public string Id;
        public DateTime Date;
        public string Title;
        public bool Done;
    }

    // 本機待辦：一行一筆，欄位用 Tab 分隔（id、日期、是否完成、標題）。純文字，不需要額外套件
    internal sealed class LocalTodoStore
    {
        private readonly List<LocalTodo> _todos = new List<LocalTodo>();

        private static string FilePath
        {
            get { return Path.Combine(DataPaths.Dir, "todos.txt"); }
        }

        public static LocalTodoStore Load()
        {
            var store = new LocalTodoStore();
            if (Settings.ReadOnly) return store;
            try
            {
                if (!File.Exists(FilePath)) return store;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    DateTime date;
                    if (f.Length < 4) continue;
                    if (!DateTime.TryParseExact(f[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                    store._todos.Add(new LocalTodo { Id = f[0], Date = date, Done = f[2] == "1", Title = f[3] });
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return store;
        }

        public LocalTodo Add(DateTime date, string title)
        {
            var todo = new LocalTodo { Id = Guid.NewGuid().ToString("N"), Date = date.Date, Title = Sanitize(title) };
            _todos.Add(todo);
            Save();
            return todo;
        }

        public void Toggle(string id)
        {
            var todo = _todos.FirstOrDefault(t => t.Id == id);
            if (todo == null) return;
            todo.Done = !todo.Done;
            Save();
        }

        public void Remove(string id)
        {
            if (_todos.RemoveAll(t => t.Id == id) > 0) Save();
        }

        public IEnumerable<CalendarItem> ToItems(string color)
        {
            return _todos.Select(t => new CalendarItem
            {
                Id = t.Id,
                Date = t.Date,
                Title = t.Title,
                Done = t.Done,
                Kind = ItemKind.LocalTodo,
                Color = color,
                CalendarId = CalendarIds.Local
            }).ToList();
        }

        private static string Sanitize(string text)
        {
            return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        // 先寫到暫存檔再換掉正式檔，避免寫到一半被中斷而弄壞資料
        private void Save()
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(DataPaths.Dir);
                AtomicFile.WriteAllLines(FilePath, _todos.Select(t =>
                    t.Id + "\t" + t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + (t.Done ? "1" : "0") + "\t" + t.Title),
                    new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

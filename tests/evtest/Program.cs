using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CalendarWidget;

namespace CalendarWidget
{
    internal sealed class ReminderLine
    {
        public string Text;
        public string Color;
    }

    // 測試用的替身：不開真正的視窗，只記錄被呼叫了什麼
    internal class ReminderWindow
    {
        public static int Created;
        public static ReminderWindow Last;

        public int ShowCount;
        public bool Closed;
        public string LastHeading;
        public string LastAck;
        public string LastSnooze;
        public List<ReminderLine> LastLines = new List<ReminderLine>();

        internal event Action Acknowledged;
        internal event Action Snoozed;

        public ReminderWindow() { Created++; Last = this; }

        internal void ShowOrUpdate(string heading, string subHeading, IList<ReminderLine> lines, string ackText, string snoozeText)
        {
            LastHeading = heading;
            LastLines = lines.ToList();
            LastAck = ackText;
            LastSnooze = snoozeText;
            ShowCount++;
        }

        public void Hide() { }
        public void Close() { Closed = true; }
        public void ClickAck() { Acknowledged(); }
        public void ClickSnooze() { Snoozed(); }
    }
}

static class P
{
    static int fails;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail)); if (!ok) fails++; }

    static readonly DateTime Now = new DateTime(2026, 10, 26, 13, 50, 0);

    static CalendarItem Ev(string title, DateTime start, params int[] reminders)
    {
        return new CalendarItem
        {
            CalendarId = "g:me", Kind = ItemKind.Event, Title = title, Color = "#6CB4FF",
            Date = start.Date, Start = start, TimeText = start.ToString("HH:mm"),
            Reminders = reminders.Length > 0 ? reminders : null
        };
    }

    static CalendarItem AllDay(string title, DateTime date, params int[] reminders)
    {
        return new CalendarItem
        {
            CalendarId = "g:me", Kind = ItemKind.Event, Title = title, Color = "#6CB4FF",
            Date = date.Date, Start = date.Date, AllDay = true,
            Reminders = reminders.Length > 0 ? reminders : null
        };
    }

    static ItemStore Store(params CalendarItem[] items)
    {
        var s = new ItemStore();
        s.SetSource("x", items);
        return s;
    }

    static string Fmt(DateTime d) { return d.ToString("MM-dd HH:mm"); }
    static readonly Dictionary<string, DateTime> NoSnooze = new Dictionary<string, DateTime>();

    static int Main()
    {
        string dir = Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA");
        if (string.IsNullOrEmpty(dir)) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        // ===== 滾輪換算 =====
        var w = new WheelStepper();
        Check("wheel: first tiny event after idle steps immediately (down = next)", w.Feed(-30, 1000) == 1);
        Check("wheel: next tiny events accumulate, not a step each", w.Feed(-30, 1030) == 0 && w.Feed(-30, 1060) == 0 && w.Feed(-30, 1090) == 0);
        Check("wheel: accumulated 120 gives one more step", w.Feed(-30, 1120) == 1, "");
        w = new WheelStepper();
        Check("wheel: up = previous", w.Feed(120, 5000) == -1);
        Check("wheel: standard notches in quick succession step every notch", w.Feed(120, 5080) == -1 && w.Feed(120, 5160) == -1);
        Check("wheel: reversing direction steps immediately", w.Feed(-120, 5200) == 1);
        Check("wheel: after idle the first event steps immediately again", w.Feed(-10, 6000) == 1);
        Check("wheel: delta 0 does nothing", w.Feed(0, 6100) == 0);
        w = new WheelStepper();
        w.Feed(-120, 100);
        Check("wheel: big fast delta gives several steps", w.Feed(-360, 150) == 3);
        w = new WheelStepper();
        w.Feed(-120, int.MaxValue - 50);
        Check("wheel: tick counter wraparound is handled", w.Feed(-120, int.MinValue + 50) == 1);

        // ===== 新增行程的規則 =====
        DateTime nowT = new DateTime(2026, 10, 27, 14, 7, 0);
        Check("default start: other day = 09:00", AddEventLogic.DefaultStart(new DateTime(2026, 10, 28), nowT) == TimeSpan.FromHours(9));
        Check("default start: today 14:07 -> 14:30 (at least 15 min ahead, next half hour)", AddEventLogic.DefaultStart(nowT.Date, nowT) == new TimeSpan(14, 30, 0), AddEventLogic.Format(AddEventLogic.DefaultStart(nowT.Date, nowT)));
        Check("default start: today 14:20 -> 15:00", AddEventLogic.DefaultStart(nowT.Date, new DateTime(2026, 10, 27, 14, 20, 0)) == new TimeSpan(15, 0, 0));
        Check("default start: today 14:50 -> 15:30", AddEventLogic.DefaultStart(nowT.Date, new DateTime(2026, 10, 27, 14, 50, 0)) == new TimeSpan(15, 30, 0));
        Check("default start: too late today -> 23:00", AddEventLogic.DefaultStart(nowT.Date, new DateTime(2026, 10, 27, 23, 50, 0)) == new TimeSpan(23, 0, 0));
        TimeSpan tp;
        Check("time parse: 9:30 / 09:30 / full-width colon", AddEventLogic.TryParseTime("9:30", out tp) && tp == new TimeSpan(9, 30, 0) && AddEventLogic.TryParseTime("09:30", out tp) && AddEventLogic.TryParseTime("09：30", out tp) && tp == new TimeSpan(9, 30, 0));
        Check("time parse: rejects 24:00, 9:60, text, empty", !AddEventLogic.TryParseTime("24:00", out tp) && !AddEventLogic.TryParseTime("9:60", out tp) && !AddEventLogic.TryParseTime("abc", out tp) && !AddEventLogic.TryParseTime("", out tp) && !AddEventLogic.TryParseTime(null, out tp));
        TimeSpan vs, ve;
        Check("validate: empty title", AddEventLogic.Validate("  ", false, "9:00", "10:00", out vs, out ve) == "請輸入行程的標題。");
        Check("validate: good input returns no error and the times", AddEventLogic.Validate("會議", false, "9:00", "10:30", out vs, out ve) == null && vs == TimeSpan.FromHours(9) && ve == new TimeSpan(10, 30, 0));
        Check("validate: end must be after start", AddEventLogic.Validate("會議", false, "10:00", "10:00", out vs, out ve) != null && AddEventLogic.Validate("會議", false, "11:00", "10:00", out vs, out ve) != null);
        Check("validate: bad time formats are reported", AddEventLogic.Validate("會議", false, "abc", "10:00", out vs, out ve).Contains("開始") && AddEventLogic.Validate("會議", false, "9:00", "xx", out vs, out ve).Contains("結束"));
        Check("validate: all-day ignores the time fields", AddEventLogic.Validate("全天", true, "", "", out vs, out ve) == null);

        // ===== 本機行程 =====
        var le = LocalEventStore.Load();
        Check("local events: empty at start", !le.ToItems("#4ECDC4").Any());
        var timedLocal = le.Add(new DateTime(2026, 10, 28, 15, 0, 0), false, new TimeSpan(14, 0, 0), new TimeSpan(15, 30, 0), "本機\t會議\n含特殊字元");
        var allDayLocal = le.Add(new DateTime(2026, 10, 30), true, TimeSpan.Zero, TimeSpan.Zero, "本機全天");
        var li = le.ToItems("#4ECDC4").ToList();
        var lt = li.First(i => i.Id == timedLocal.Id);
        Check("local events: timed item has start, time text, color, calendar id, kind and the default reminder",
            lt.Start == new DateTime(2026, 10, 28, 14, 0, 0) && lt.TimeText == "14:00" && !lt.AllDay && lt.Color == "#4ECDC4" && lt.CalendarId == CalendarIds.LocalEvents &&
            lt.Kind == ItemKind.Event && lt.Reminders != null && lt.Reminders.SequenceEqual(new[] { LocalEventStore.DefaultReminderMinutes }));
        Check("local events: date part of the given date is used (time ignored)", lt.Date == new DateTime(2026, 10, 28));
        Check("local events: title is sanitized", lt.Title == "本機 會議 含特殊字元", lt.Title);
        var la = li.First(i => i.Id == allDayLocal.Id);
        Check("local events: all-day item has AllDay, Start = date, no time text and no reminder", la.AllDay && la.Start == new DateTime(2026, 10, 30) && la.TimeText == null && la.Reminders == null);
        var reloaded = LocalEventStore.Load().ToItems("#4ECDC4").ToList();
        Check("local events: survive a restart intact", reloaded.Count == 2 && reloaded.Any(i => i.Id == timedLocal.Id && i.TimeText == "14:00" && i.Title == "本機 會議 含特殊字元") && reloaded.Any(i => i.Id == allDayLocal.Id && i.AllDay));
        le.Remove(timedLocal.Id);
        Check("local events: remove persists", LocalEventStore.Load().ToItems("x").Count() == 1 && LocalEventStore.Load().ToItems("x").First().Id == allDayLocal.Id);
        // 編輯本機行程
        Check("local events: items carry End (timed = end time; all-day = next day) and are single-day",
            lt.End == new DateTime(2026, 10, 28, 15, 30, 0) && lt.IsEditable && la.End == new DateTime(2026, 10, 31) && la.IsEditable);
        Check("local events: update changes title/date/time and keeps the id", le.Update(allDayLocal.Id, new DateTime(2026, 11, 2, 8, 0, 0), false, new TimeSpan(9, 0, 0), new TimeSpan(10, 15, 0), "改\t成\n有時間"));
        var upd = LocalEventStore.Load().ToItems("x").First(i => i.Id == allDayLocal.Id);
        Check("local events: the update persists (now timed, sanitized title, new date)", !upd.AllDay && upd.Date == new DateTime(2026, 11, 2) && upd.TimeText == "09:00" && upd.End == new DateTime(2026, 11, 2, 10, 15, 0) && upd.Title == "改 成 有時間" && upd.Reminders != null, upd.Title);
        Check("local events: update can turn a timed event back into all-day", le.Update(allDayLocal.Id, new DateTime(2026, 11, 3), true, TimeSpan.Zero, TimeSpan.Zero, "又變全天") &&
            LocalEventStore.Load().ToItems("x").First(i => i.Id == allDayLocal.Id).AllDay && LocalEventStore.Load().ToItems("x").First(i => i.Id == allDayLocal.Id).Reminders == null);
        Check("local events: updating an unknown id does nothing", !le.Update("nope", DateTime.Today, true, TimeSpan.Zero, TimeSpan.Zero, "x") && LocalEventStore.Load().ToItems("x").Count() == 1);
        var spanTimed = new CalendarItem { Date = new DateTime(2026, 10, 28), Start = new DateTime(2026, 10, 28, 23, 0, 0), End = new DateTime(2026, 10, 29, 1, 0, 0) };
        var spanAllFirst = new CalendarItem { Date = new DateTime(2026, 10, 28), AllDay = true, Start = new DateTime(2026, 10, 28), End = new DateTime(2026, 10, 31) };
        var spanAllLater = new CalendarItem { Date = new DateTime(2026, 10, 29), AllDay = true, End = new DateTime(2026, 10, 31) };
        var noEnd = new CalendarItem { Date = new DateTime(2026, 10, 28), Start = new DateTime(2026, 10, 28, 9, 0, 0) };
        Check("IsEditable: events crossing midnight and the first day of a multi-day all-day event are editable", spanTimed.IsEditable && spanAllFirst.IsEditable);
        Check("IsEditable: later days of a multi-day all-day event and events with unknown end are not", !spanAllLater.IsEditable && !noEnd.IsEditable);
        Check("LastDay: all-day = the day before the exclusive end; others = Date", spanAllFirst.LastDay == new DateTime(2026, 10, 30) && spanTimed.LastDay == new DateTime(2026, 10, 28));

        // ===== 多日行程 =====
        var mdAll = le.Add(new DateTime(2026, 12, 1), true, TimeSpan.Zero, TimeSpan.Zero, "三天出差", new DateTime(2026, 12, 3));
        var mi = le.ToItems("x").Where(i => i.Id == mdAll.Id).OrderBy(i => i.Date).ToList();
        Check("mdAll-day local all-day: one item per day, Start only on the first day, End shared", mi.Count == 3 && mi[0].Date == new DateTime(2026, 12, 1) && mi[2].Date == new DateTime(2026, 12, 3) &&
            mi[0].Start == new DateTime(2026, 12, 1) && mi[1].Start == null && mi[2].Start == null && mi.All(i => i.End == new DateTime(2026, 12, 4)) && mi[0].IsEditable && !mi[1].IsEditable && !mi[2].IsEditable);
        var multiTimed = le.Add(new DateTime(2026, 12, 10), false, new TimeSpan(22, 0, 0), new TimeSpan(6, 0, 0), "夜間值班", new DateTime(2026, 12, 11));
        var mt = le.ToItems("x").Where(i => i.Id == multiTimed.Id).ToList();
        Check("mdAll-day local timed: one item on the start day, End on the end date", mt.Count == 1 && mt[0].Start == new DateTime(2026, 12, 10, 22, 0, 0) && mt[0].End == new DateTime(2026, 12, 11, 6, 0, 0) && mt[0].IsEditable);
        var multiReload = LocalEventStore.Load().ToItems("x").Where(i => i.Id == mdAll.Id).ToList();
        Check("mdAll-day local: survives a restart", multiReload.Count == 3 && LocalEventStore.Load().ToItems("x").Any(i => i.Id == multiTimed.Id && i.End == new DateTime(2026, 12, 11, 6, 0, 0)));
        le.Add(new DateTime(2026, 12, 20), true, TimeSpan.Zero, TimeSpan.Zero, "壞資料", new DateTime(2026, 12, 18));
        Check("mdAll-day local: an end date earlier than the start is treated as single-day", le.ToItems("x").Count(i => i.Title == "壞資料") == 1);
        Check("mdAll-day local: update can stretch a single day to several and back",
            le.Update(mdAll.Id, new DateTime(2026, 12, 1), true, TimeSpan.Zero, TimeSpan.Zero, "三天出差", new DateTime(2026, 12, 5)) &&
            LocalEventStore.Load().ToItems("x").Count(i => i.Id == mdAll.Id) == 5 &&
            le.Update(mdAll.Id, new DateTime(2026, 12, 1), true, TimeSpan.Zero, TimeSpan.Zero, "三天出差") &&
            LocalEventStore.Load().ToItems("x").Count(i => i.Id == mdAll.Id) == 1);
        // 舊格式（6 欄、沒有結束日期）的檔案還是讀得進來，視為單日
        string eventsFile = Path.Combine(dir, "local-events.txt");
        File.AppendAllText(eventsFile, "oldid\t2026-11-20\t0\t10:00\t11:00\t舊格式的行程\n");
        var oldFmt = LocalEventStore.Load().ToItems("x").FirstOrDefault(i => i.Id == "oldid");
        Check("local events: old 6-column lines still load (single day, title intact)", oldFmt != null && oldFmt.Title == "舊格式的行程" && oldFmt.End == new DateTime(2026, 11, 20, 11, 0, 0), oldFmt == null ? "missing" : oldFmt.Title);
        // 清掉這幾筆，避免影響後面的檢查
        foreach (string rid in new[] { mdAll.Id, multiTimed.Id, "oldid" }) le.Remove(rid);
        foreach (string rmId in LocalEventStore.Load().ToItems("x").Where(i => i.Title == "壞資料").Select(i => i.Id).Distinct().ToList()) le.Remove(rmId);

        // 驗證規則（含日期範圍）
        TimeSpan rs, re2;
        Check("range: end date before start date is rejected", AddEventLogic.ValidateRange("x", true, new DateTime(2026, 12, 5), new DateTime(2026, 12, 4), "", "", out rs, out re2).Contains("結束日期"));
        Check("range: longer than the maximum is rejected, exactly the maximum is fine",
            AddEventLogic.ValidateRange("x", true, new DateTime(2026, 12, 1), new DateTime(2026, 12, 1).AddDays(AddEventLogic.MaxDays), "", "", out rs, out re2).Contains("最長") &&
            AddEventLogic.ValidateRange("x", true, new DateTime(2026, 12, 1), new DateTime(2026, 12, 1).AddDays(AddEventLogic.MaxDays - 1), "", "", out rs, out re2) == null);
        Check("range: timed same day needs end after start, but across days the end time can be earlier",
            AddEventLogic.ValidateRange("x", false, new DateTime(2026, 12, 1), new DateTime(2026, 12, 1), "10:00", "09:00", out rs, out re2) != null &&
            AddEventLogic.ValidateRange("x", false, new DateTime(2026, 12, 1), new DateTime(2026, 12, 2), "22:00", "06:00", out rs, out re2) == null && rs == new TimeSpan(22, 0, 0) && re2 == new TimeSpan(6, 0, 0));

        // ===== 拖曳排序（RowReorder）=====
        var rc = new List<double> { 10, 30, 50, 70 };
        Check("reorder: dragging the first row, the pointer decides how many other rows are above it", RowReorder.TargetIndex(rc, 0, 5) == 0 && RowReorder.TargetIndex(rc, 0, 40) == 1 && RowReorder.TargetIndex(rc, 0, 60) == 2 && RowReorder.TargetIndex(rc, 0, 100) == 3);
        Check("reorder: dragging the last row up", RowReorder.TargetIndex(rc, 3, 100) == 3 && RowReorder.TargetIndex(rc, 3, 20) == 1 && RowReorder.TargetIndex(rc, 3, 0) == 0);
        var mv = new List<string> { "a", "b", "c", "d" };
        RowReorder.Move(mv, 0, 2);
        var afterFirst = string.Join("", mv);
        RowReorder.Move(mv, 3, 0);
        var afterSecond = string.Join("", mv);
        RowReorder.Move(mv, 1, 1); RowReorder.Move(mv, -1, 2); RowReorder.Move(mv, 1, 9);
        Check("reorder: Move puts the item in the new place and ignores no-ops / out of range", afterFirst == "bcad" && afterSecond == "dbca" && string.Join("", mv) == "dbca", afterFirst + " " + afterSecond + " " + string.Join("", mv));
        // 穩定：搬完以後用同一個指標位置再算一次，答案還是搬到的位置（不會來回跳）
        bool stable = true;
        for (int cur = 0; cur < 5 && stable; cur++)
        {
            for (double py = -5; py < 215 && stable; py += 7)
            {
                var order = Enumerable.Range(0, 5).ToList();
                var centers = order.Select(i => i * 40 + 20.0).ToList();
                int tgt = RowReorder.TargetIndex(centers, cur, py);
                RowReorder.Move(order, cur, tgt);
                var centers2 = order.Select((o, i) => i * 40 + 20.0).ToList();
                if (RowReorder.TargetIndex(centers2, tgt, py) != tgt) stable = false;
            }
        }
        Check("reorder: after moving, the same pointer position gives the same index (no flip-flopping)", stable);

        long leSize = new FileInfo(Path.Combine(dir, "local-events.txt")).Length;
        Settings.ReadOnly = true;
        LocalEventStore.Load().Add(new DateTime(2026, 11, 1), false, TimeSpan.FromHours(9), TimeSpan.FromHours(10), "唯讀");
        Settings.ReadOnly = false;
        Check("local events: readonly does not write", new FileInfo(Path.Combine(dir, "local-events.txt")).Length == leSize);

        // 本機行程會被行程提醒抓到：預設 10 分鐘前
        var le2 = new LocalEventStore();
        var soonLocal = le2.Add(DateTime.Today, false, DateTime.Now.TimeOfDay.Add(TimeSpan.FromMinutes(6)), DateTime.Now.TimeOfDay.Add(TimeSpan.FromMinutes(66)), "本機馬上開始");
        var storeLocal = new ItemStore();
        storeLocal.SetSource("le", le2.ToItems("#4ECDC4"));
        var localDues = EventReminderLogic.Compute(storeLocal, Settings.EventReminderPerEvent, 10, DateTime.Now, 8);
        Check("local events: picked up by the event reminder with the default 10 minutes", localDues.Count == 1 && localDues[0].Minutes == 10 && localDues[0].Item.Title == "本機馬上開始");

        // ===== 依各行程自己的設定 =====
        var timed = Ev("會議", new DateTime(2026, 10, 26, 14, 0, 0), 10, 60);
        var noRem = Ev("沒設提醒", new DateTime(2026, 10, 26, 15, 0, 0));
        var holiday = Ev("節日", new DateTime(2026, 10, 27, 0, 0, 0), 10); holiday.Kind = ItemKind.Holiday;
        var task = Ev("待辦", new DateTime(2026, 10, 26, 16, 0, 0), 10); task.Kind = ItemKind.GoogleTask;
        var store = Store(timed, noRem, holiday, task);

        var dues = EventReminderLogic.Compute(store, Settings.EventReminderPerEvent, 10, Now, 8);
        Check("per-event: only events with reminders, one entry per reminder", dues.Count == 2 && dues.All(d => d.Item.Title == "會議"), dues.Count + "");
        Check("per-event: due = start - minutes", dues.Any(d => d.Minutes == 10 && d.Due == new DateTime(2026, 10, 26, 13, 50, 0)) && dues.Any(d => d.Minutes == 60 && d.Due == new DateTime(2026, 10, 26, 13, 0, 0)));
        Check("per-event: expires at start for timed events", dues.All(d => d.Expires == new DateTime(2026, 10, 26, 14, 0, 0)));
        Check("per-event: holidays and tasks are never listed", !dues.Any(d => d.Item.Kind != ItemKind.Event));

        // 整天行程：提醒從當天 00:00 往前算
        var allday = AllDay("明天出差", new DateTime(2026, 10, 27), 900, 1440);
        var ad = EventReminderLogic.Compute(Store(allday), Settings.EventReminderPerEvent, 10, Now, 8);
        Check("all-day: 900 min = 9:00 the day before", ad.Any(d => d.Minutes == 900 && d.Due == new DateTime(2026, 10, 26, 9, 0, 0)));
        Check("all-day: 1440 min = midnight the day before", ad.Any(d => d.Minutes == 1440 && d.Due == new DateTime(2026, 10, 26, 0, 0, 0)));
        Check("all-day: stays valid through the whole day", ad.All(d => d.Expires == new DateTime(2026, 10, 28, 0, 0, 0)));
        var multi = AllDay("連休", new DateTime(2026, 10, 28), 900); var cont = AllDay("連休", new DateTime(2026, 10, 29)); cont.Start = null;
        Check("all-day multi-day: only the first day carries the reminder", EventReminderLogic.Compute(Store(multi, cont), Settings.EventReminderPerEvent, 10, Now, 8).Count == 1);

        // ===== 自訂 =====
        var custom = EventReminderLogic.Compute(Store(timed, noRem, allday), Settings.EventReminderCustom, 15, Now, 8);
        Check("custom: every timed event gets start - N, own reminders ignored", custom.Count == 2 && custom.All(d => d.Minutes == 15) &&
            custom.Any(d => d.Item.Title == "會議" && d.Due == new DateTime(2026, 10, 26, 13, 45, 0)) && custom.Any(d => d.Item.Title == "沒設提醒" && d.Due == new DateTime(2026, 10, 26, 14, 45, 0)), custom.Count + "");
        Check("custom: all-day events are not reminded", !custom.Any(d => d.Item.AllDay));

        // ===== 不提醒 =====
        Check("off: nothing", EventReminderLogic.Compute(Store(timed, noRem, allday), Settings.EventReminderOff, 10, Now, 8).Count == 0);

        // ===== 範圍 =====
        var far = Ev("很遠的行程", new DateTime(2026, 11, 20, 9, 0, 0), 10);
        var yesterday = Ev("昨天的行程", new DateTime(2026, 10, 25, 23, 50, 0), 10);
        var rangeDues = EventReminderLogic.Compute(Store(far, yesterday), Settings.EventReminderPerEvent, 10, Now, 8);
        Check("range: far future skipped, yesterday kept (for late-night events)", rangeDues.Count == 1 && rangeDues[0].Item.Title == "昨天的行程");

        // 同一個行程重複出現（同一個日曆）不會變成兩筆
        var dup = Ev("會議", new DateTime(2026, 10, 26, 14, 0, 0), 10);
        Check("duplicates collapse to one", EventReminderLogic.Compute(Store(timed, dup), Settings.EventReminderPerEvent, 10, Now, 8).Count(d => d.Minutes == 10) == 1);

        // ===== 現在該顯示哪些 =====
        Func<string, bool> notAcked = k => false;
        var all = EventReminderLogic.Compute(store, Settings.EventReminderPerEvent, 10, Now, 8);
        var p = EventReminderLogic.Pending(all, Now, notAcked, NoSnooze);
        Check("pending: 10-minute reminder is due now, 60-minute one too (already past, event not started)", p.Count == 2, p.Count + "");
        Check("pending: before the due time nothing shows", EventReminderLogic.Pending(all, new DateTime(2026, 10, 26, 12, 59, 0), notAcked, NoSnooze).Count == 0);
        Check("pending: only the 60-min reminder at 13:30", EventReminderLogic.Pending(all, new DateTime(2026, 10, 26, 13, 30, 0), notAcked, NoSnooze).Count == 1);
        Check("pending: nothing after the event started", EventReminderLogic.Pending(all, new DateTime(2026, 10, 26, 14, 0, 0), notAcked, NoSnooze).Count == 0);
        var ackedKey = all[0].Key;
        Check("pending: acknowledged ones are excluded", EventReminderLogic.Pending(all, Now, k => k == ackedKey, NoSnooze).Count == 1);
        var snoozed = new Dictionary<string, DateTime> { { all[0].Key, Now.AddMinutes(5) } };
        Check("pending: snoozed ones are excluded until the snooze ends", EventReminderLogic.Pending(all, Now, notAcked, snoozed).Count == 1 &&
            EventReminderLogic.Pending(all, Now.AddMinutes(5), notAcked, snoozed).Count == 2 - 0 - (all[0].Expires <= Now.AddMinutes(5) ? 2 : 0));
        var ad2 = EventReminderLogic.Compute(Store(allday), Settings.EventReminderPerEvent, 10, new DateTime(2026, 10, 26, 9, 30, 0), 8);
        Check("pending: all-day reminder shows at 9:30 the day before", EventReminderLogic.Pending(ad2, new DateTime(2026, 10, 26, 9, 30, 0), notAcked, NoSnooze).Count == 2);
        Check("pending: all-day reminder still shown on the day itself", EventReminderLogic.Pending(ad2, new DateTime(2026, 10, 27, 8, 0, 0), notAcked, NoSnooze).Count == 2);
        Check("pending: all-day reminder gone the next day", EventReminderLogic.Pending(ad2, new DateTime(2026, 10, 28, 0, 0, 1), notAcked, NoSnooze).Count == 0);
        Check("pending: sorted by start time", EventReminderLogic.Pending(EventReminderLogic.Compute(Store(Ev("晚的", new DateTime(2026, 10, 26, 14, 30, 0), 60), Ev("早的", new DateTime(2026, 10, 26, 14, 5, 0), 60)), Settings.EventReminderPerEvent, 10, Now, 8), Now, notAcked, NoSnooze).First().Item.Title == "早的");

        // 「開始時提醒」（0 分鐘）：提醒時間就是開始時間，要在開始的那一刻跳出來，不能一到點就過期
        var atStart = EventReminderLogic.Compute(Store(Ev("準時", new DateTime(2026, 10, 26, 14, 0, 0), 0)), Settings.EventReminderPerEvent, 10, Now, 8);
        Check("pending: a 0-minute (at start) reminder shows when the event starts and a few minutes after",
            atStart.Count == 1 && EventReminderLogic.Pending(atStart, new DateTime(2026, 10, 26, 13, 59, 59), notAcked, NoSnooze).Count == 0 &&
            EventReminderLogic.Pending(atStart, new DateTime(2026, 10, 26, 14, 0, 0), notAcked, NoSnooze).Count == 1 &&
            EventReminderLogic.Pending(atStart, new DateTime(2026, 10, 26, 14, 4, 59), notAcked, NoSnooze).Count == 1 &&
            EventReminderLogic.Pending(atStart, new DateTime(2026, 10, 26, 14, 5, 0), notAcked, NoSnooze).Count == 0);
        var atStartCustom = EventReminderLogic.Compute(Store(Ev("自訂零", new DateTime(2026, 10, 26, 14, 0, 0))), Settings.EventReminderCustom, 0, Now, 8);
        Check("pending: custom mode with 0 minutes also shows at the start", EventReminderLogic.Pending(atStartCustom, new DateTime(2026, 10, 26, 14, 0, 30), notAcked, NoSnooze).Count == 1);
        Check("next wake: a 0-minute reminder wakes us at the start time", Fmt(EventReminderLogic.NextWake(atStart, new DateTime(2026, 10, 26, 13, 0, 0), notAcked, NoSnooze).Value) == "10-26 14:00");

        // 長的提醒（自訂最多 28 天）要掃得夠遠，不然會拖到只剩 8 天才跳出
        Check("scan range: long reminders widen it, short ones keep 8 days",
            EventReminderLogic.DaysToScan(Settings.EventReminderCustom, 14 * 1440) == 15 && EventReminderLogic.DaysToScan(Settings.EventReminderCustom, 10) == 8 &&
            EventReminderLogic.DaysToScan(Settings.EventReminderPerEvent, 10) == 29 && EventReminderLogic.DaysToScan(Settings.EventReminderCustom, Settings.MaxEventReminderMinutes) == 29);
        var farEvent = Ev("兩週後", Now.Date.AddDays(13).AddHours(10));
        var farDues = EventReminderLogic.Compute(Store(farEvent), Settings.EventReminderCustom, 14 * 1440, Now, EventReminderLogic.DaysToScan(Settings.EventReminderCustom, 14 * 1440));
        Check("a 14-day reminder is found for an event 13 days away; with the old 8-day scan it was not",
            farDues.Count == 1 && EventReminderLogic.Compute(Store(farEvent), Settings.EventReminderCustom, 14 * 1440, Now, 8).Count == 0);

        // ===== 下一次要醒來的時間 =====
        var future = EventReminderLogic.Compute(Store(Ev("下午", new DateTime(2026, 10, 26, 16, 0, 0), 10, 30)), Settings.EventReminderPerEvent, 10, Now, 8);
        Check("next wake: earliest future due", Fmt(EventReminderLogic.NextWake(future, Now, notAcked, NoSnooze).Value) == "10-26 15:30");
        Check("next wake: nothing to do -> null", EventReminderLogic.NextWake(new List<DueReminder>(), Now, notAcked, NoSnooze) == null);
        var sn = new Dictionary<string, DateTime> { { all[0].Key, Now.AddMinutes(3) } };
        Check("next wake: includes snooze expiry", Fmt(EventReminderLogic.NextWake(new List<DueReminder> { all[0] }, Now, notAcked, sn).Value) == Fmt(Now.AddMinutes(3)));
        Check("next wake: expired and acknowledged ones are ignored", EventReminderLogic.NextWake(new List<DueReminder> { all[0] }, new DateTime(2026, 10, 26, 14, 1, 0), notAcked, NoSnooze) == null &&
            EventReminderLogic.NextWake(future, Now, k => true, NoSnooze) == null);

        // ===== 文字 =====
        var line = EventReminderLogic.LineText(all.First(d => d.Minutes == 10), Now);
        Check("line text: time, title and minutes left", line == "14:00  會議（10 分鐘後開始）", line);
        Check("line text: hours", EventReminderLogic.LineText(all.First(d => d.Minutes == 60), new DateTime(2026, 10, 26, 12, 30, 0)).EndsWith("（1 小時 30 分鐘後開始）"));
        Check("line text: all-day tomorrow", EventReminderLogic.LineText(ad2[0], new DateTime(2026, 10, 26, 9, 30, 0)) == "全天  明天出差（明天）");
        Check("keys differ by reminder minutes", all[0].Key != all[1].Key);

        // 同一個行程有兩個提醒同時到時間：只列一行，不是兩行
        var onePer = EventReminderLogic.OnePerEvent(p);
        Check("one line per event even when two reminders are due together", p.Count == 2 && onePer.Count == 1 && onePer[0].Item.Title == "會議", p.Count + "/" + onePer.Count);
        Check("one line per event keeps different events separate", EventReminderLogic.OnePerEvent(EventReminderLogic.Pending(
            EventReminderLogic.Compute(Store(Ev("甲", new DateTime(2026, 10, 26, 14, 0, 0), 10, 60), Ev("乙", new DateTime(2026, 10, 26, 14, 5, 0), 60)), Settings.EventReminderPerEvent, 10, Now, 8),
            Now, notAcked, NoSnooze)).Count == 2);

        // ===== 確認紀錄 =====
        var acks = EventAckStore.Load(Now);
        acks.Ack(new[] { all[0] });
        Check("ack store: remembered across restart", EventAckStore.Load(Now).IsAcked(all[0].Key) && !EventAckStore.Load(Now).IsAcked(all[1].Key));
        var oldItem = new DueReminder { Item = Ev("很舊", new DateTime(2026, 10, 1, 9, 0, 0), 10), Due = new DateTime(2026, 10, 1, 8, 50, 0), Key = "old-key" };
        acks.Ack(new[] { oldItem });
        Check("ack store: records older than 3 days are dropped on load", !EventAckStore.Load(Now).IsAcked("old-key") && EventAckStore.Load(Now).IsAcked(all[0].Key));
        long size = new FileInfo(Path.Combine(dir, "acked-events.txt")).Length;
        Settings.ReadOnly = true;
        EventAckStore.Load(Now).Ack(new[] { all[1] });
        Settings.ReadOnly = false;
        Check("ack store: readonly does not write", new FileInfo(Path.Combine(dir, "acked-events.txt")).Length == size);
        File.Delete(Path.Combine(dir, "acked-events.txt"));

        // ===== 控制器（用替身視窗）=====
        var settings = new Settings();
        var live = new ItemStore();
        DateTime nowReal = DateTime.Now;
        var soon = Ev("馬上開始", nowReal.AddMinutes(6), 10);
        live.SetSource("x", new[] { soon });
        var rem = new EventReminder(live, settings, EventAckStore.Load(nowReal));
        ReminderWindow.Created = 0;
        rem.CheckNow();
        var win = ReminderWindow.Last;
        Check("controller: shows one window with the event", ReminderWindow.Created == 1 && win.LastLines.Count == 1 && win.LastLines[0].Text.Contains("馬上開始") && win.LastAck == "知道了" && win.LastSnooze == "5 分鐘後再提醒", win.LastHeading);
        rem.CheckNow();
        Check("controller: second check reuses the same window", ReminderWindow.Created == 1 && win.ShowCount == 2);
        win.ClickSnooze();
        Check("controller: snooze closes the window", win.Closed);
        rem.CheckNow();
        Check("controller: while snoozed it does not show again", ReminderWindow.Created == 1);

        var rem2 = new EventReminder(live, settings, EventAckStore.Load(nowReal));
        ReminderWindow.Created = 0;
        rem2.CheckNow();
        var win2 = ReminderWindow.Last;
        win2.ClickAck();
        Check("controller: ack closes the window", win2.Closed);
        rem2.CheckNow();
        Check("controller: acknowledged event never shows again", ReminderWindow.Created == 1);
        var rem3 = new EventReminder(live, settings, EventAckStore.Load(nowReal));
        ReminderWindow.Created = 0;
        rem3.CheckNow();
        Check("controller: ack survives restart", ReminderWindow.Created == 0);

        // 一個行程兩個提醒同時到：提醒框只有一行；按「知道了」兩個都算確認，重開也不會再跳
        var twice = Ev("雙提醒", nowReal.AddMinutes(8), 10, 60);
        var liveTwo = new ItemStore();
        liveTwo.SetSource("x", new[] { twice });
        var remTwo = new EventReminder(liveTwo, new Settings(), new EventAckStore());
        ReminderWindow.Created = 0;
        remTwo.CheckNow();
        var winTwo = ReminderWindow.Last;
        Check("controller: two reminders of one event show as a single line", winTwo.LastLines.Count == 1 && winTwo.LastHeading == "行程提醒", winTwo.LastLines.Count + " " + winTwo.LastHeading);
        winTwo.ClickAck();
        var ackStoreTwo = EventAckStore.Load(DateTime.Now);
        Check("controller: ack covers both reminders of that event", ackStoreTwo.IsAcked(EventReminderLogic.KeyOf(twice, 10)) && ackStoreTwo.IsAcked(EventReminderLogic.KeyOf(twice, 60)));

        settings.EventReminderMode = Settings.EventReminderOff;
        var rem4 = new EventReminder(live, settings, new EventAckStore());
        ReminderWindow.Created = 0;
        rem4.CheckNow();
        Check("controller: off means no window", ReminderWindow.Created == 0);
        settings.EventReminderMode = Settings.EventReminderCustom;
        settings.EventReminderMinutes = 30;
        var rem5 = new EventReminder(live, settings, new EventAckStore());
        ReminderWindow.Created = 0;
        rem5.CheckNow();
        Check("controller: custom 30 min shows the event that starts in 6 min", ReminderWindow.Created == 1);

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

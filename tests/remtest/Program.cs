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
        public bool Hidden;
        public string LastHeading;
        public string LastSnoozeText;
        public List<ReminderLine> LastLines = new List<ReminderLine>();

        internal event Action Acknowledged;
        internal event Action Snoozed;

        public ReminderWindow() { Created++; Last = this; }

        internal void ShowOrUpdate(string heading, string subHeading, IList<ReminderLine> lines, string ackText, string snoozeText)
        {
            LastHeading = heading;
            LastLines = lines.ToList();
            LastSnoozeText = snoozeText;
            ShowCount++;
            Hidden = false;
        }

        public void Hide() { Hidden = true; }
        public void Close() { Closed = true; }
        public void ClickAck() { Acknowledged(); }
        public void ClickSnooze() { Snoozed(); }
    }
}

static class P
{
    static int fails;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail)); if (!ok) fails++; }

    static CalendarItem H(DateTime d, string t) { return new CalendarItem { Date = d, Title = t, Kind = ItemKind.Holiday, Color = "#FF6B7A" }; }
    static CalendarItem E(DateTime d, string t) { return new CalendarItem { Date = d, Title = t, Kind = ItemKind.Event, Color = "#6CB4FF" }; }

    static int Main()
    {
        string dir = Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA");
        if (string.IsNullOrEmpty(dir)) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        DateTime today = DateTime.Today;
        DateTime tomorrow = today.AddDays(1);

        // 下一次檢查的時間
        Func<int, int, int, int, int, DateTime> T = (d, h, m, s, ms) => new DateTime(2026, 10, 26).AddDays(d).AddHours(h).AddMinutes(m).AddSeconds(s).AddMilliseconds(ms);
        Check("next: hourly all-day 14:20 -> 15:00", HolidayReminder.NextCheck(T(0, 14, 20, 0, 0), 60, 0, 24) == T(0, 15, 0, 0, 0));
        Check("next: hourly at 14:00:00.5 -> 15:00 (not the same instant)", HolidayReminder.NextCheck(T(0, 14, 0, 0, 500), 60, 0, 24) == T(0, 15, 0, 0, 0));
        Check("next: 30 min 14:20 -> 14:30", HolidayReminder.NextCheck(T(0, 14, 20, 0, 0), 30, 0, 24) == T(0, 14, 30, 0, 0));
        Check("next: 3h all-day 22:00 -> next day 00:00", HolidayReminder.NextCheck(T(0, 22, 0, 0, 0), 180, 0, 24) == T(1, 0, 0, 0, 0));
        Check("next: window 9-18 every 2h at 08:00 -> 09:00", HolidayReminder.NextCheck(T(0, 8, 0, 0, 0), 120, 9, 18) == T(0, 9, 0, 0, 0));
        Check("next: window 9-18 every 2h at 09:00:01 -> 11:00", HolidayReminder.NextCheck(T(0, 9, 0, 1, 0), 120, 9, 18) == T(0, 11, 0, 0, 0));
        Check("next: window 9-18 every 2h at 17:30 -> tomorrow 09:00", HolidayReminder.NextCheck(T(0, 17, 30, 0, 0), 120, 9, 18) == T(1, 9, 0, 0, 0));
        Check("next: window 9-18 hourly at 17:30 -> tomorrow 09:00 (end exclusive)", HolidayReminder.NextCheck(T(0, 17, 30, 0, 0), 60, 9, 18) == T(1, 9, 0, 0, 0));
        Check("next: window 9-18 hourly at 17:00:01 -> tomorrow 09:00", HolidayReminder.NextCheck(T(0, 17, 0, 1, 0), 60, 9, 18) == T(1, 9, 0, 0, 0));
        Check("next: after midnight in window 9-18 -> today 09:00", HolidayReminder.NextCheck(T(1, 0, 30, 0, 0), 60, 9, 18) == T(1, 9, 0, 0, 0));
        Check("next: invalid window falls back to all-day", HolidayReminder.NextCheck(T(0, 14, 20, 0, 0), 60, 18, 9) == T(0, 15, 0, 0, 0));
        Check("next: window 8-24 at 23:30 hourly -> tomorrow 08:00", HolidayReminder.NextCheck(T(0, 23, 30, 0, 0), 60, 8, 24) == T(1, 8, 0, 0, 0));

        // 按鈕文字
        Check("snooze text <60 min", HolidayReminder.SnoozeText(T(0, 14, 20, 0, 0), T(0, 15, 0, 0, 0)) == "40 分鐘後再問我");
        Check("snooze text 1 min", HolidayReminder.SnoozeText(T(0, 14, 59, 0, 0), T(0, 15, 0, 0, 0)) == "1 分鐘後再問我");
        Check("snooze text exactly 1 hour", HolidayReminder.SnoozeText(T(0, 15, 0, 1, 0), T(0, 16, 0, 0, 0)) == "1 小時後再問我", HolidayReminder.SnoozeText(T(0, 15, 0, 1, 0), T(0, 16, 0, 0, 0)));
        Check("snooze text 2 hours", HolidayReminder.SnoozeText(T(0, 9, 0, 1, 0), T(0, 11, 0, 0, 0)) == "2 小時後再問我");
        Check("snooze text hours+minutes", HolidayReminder.SnoozeText(T(0, 9, 30, 0, 0), T(0, 11, 0, 0, 0)) == "1 小時 30 分鐘後再問我");
        Check("snooze text next day", HolidayReminder.SnoozeText(T(0, 17, 0, 1, 0), T(1, 9, 0, 0, 0)) == "明天 09:00 再問我");

        // 篩選
        var store = new ItemStore();
        store.SetSource("a", new[]
        {
            H(tomorrow, "光復節"), H(tomorrow, "光復節"),      // 同名只算一個
            H(tomorrow, "補假"),
            E(tomorrow, "一般行程"),                            // 不是節日
            H(today, "今天的節日"),                             // 不是明天
            H(today.AddDays(2), "後天的節日")
        });
        var acks = AckStore.Load(today);
        var pending = HolidayReminder.Pending(store, acks, today);
        Check("pending only tomorrow holidays, deduped", pending.Count == 2 && pending.Any(i => i.Title == "光復節") && pending.Any(i => i.Title == "補假"), string.Join(",", pending.Select(i => i.Title)));

        // 按「好」之後不再提醒，而且重開程式（重新載入）也記得
        acks.Ack(new[] { pending.First(i => i.Title == "光復節") });
        var after = HolidayReminder.Pending(store, acks, today);
        Check("acked one is gone", after.Count == 1 && after[0].Title == "補假");
        var reloaded = AckStore.Load(today);
        Check("ack persisted across restart", HolidayReminder.Pending(store, reloaded, today).Count == 1);

        // 太舊的紀錄會被清掉
        File.AppendAllText(Path.Combine(dir, "acked.txt"), today.AddDays(-90).ToString("yyyy-MM-dd") + "\t很舊的節日\n");
        var pruned = AckStore.Load(today);
        Check("old ack entries dropped on load", !pruned.IsAcked(H(today.AddDays(-90), "很舊的節日")) && pruned.IsAcked(H(tomorrow, "光復節")));

        // 唯讀（示範模式）不寫檔
        long before = new FileInfo(Path.Combine(dir, "acked.txt")).Length;
        Settings.ReadOnly = true;
        var ro = AckStore.Load(today);
        ro.Ack(new[] { H(tomorrow, "唯讀不應寫入") });
        Settings.ReadOnly = false;
        Check("readonly does not write", new FileInfo(Path.Combine(dir, "acked.txt")).Length == before);

        // 控制器：重用同一個框、確認後關閉、停用時關閉
        var settings = new Settings();
        var store2 = new ItemStore();
        store2.SetSource("a", new[] { H(tomorrow, "測試節日A"), H(tomorrow, "測試節日B") });
        var acks2 = AckStore.Load(today);
        var rem = new HolidayReminder(store2, settings, acks2);
        ReminderWindow.Created = 0;

        rem.CheckNow();
        var w = ReminderWindow.Last;
        Check("first check creates one window", ReminderWindow.Created == 1 && w.ShowCount == 1 && w.LastLines.Count == 2);

        rem.CheckNow();   // 下一個整點，使用者沒回應
        Check("no response: same window reused, not a second one", ReminderWindow.Created == 1 && w.ShowCount == 2 && !w.Closed);

        w.ClickSnooze();
        Check("snooze hides window", w.Hidden && !w.Closed);
        rem.CheckNow();
        Check("after snooze next check shows the same window again", ReminderWindow.Created == 1 && w.ShowCount == 3 && !w.Hidden);

        // 提醒框還開著時，節日內容改變（例如來源更新）→ 更新內容
        store2.SetSource("a", new[] { H(tomorrow, "測試節日A"), H(tomorrow, "測試節日B"), H(tomorrow, "測試節日C") });
        rem.CheckNow();
        Check("content updated in same window", ReminderWindow.Created == 1 && w.LastLines.Count == 3);

        w.ClickAck();
        Check("ack closes window and records all listed", w.Closed && acks2.IsAcked(H(tomorrow, "測試節日A")) && acks2.IsAcked(H(tomorrow, "測試節日C")));
        rem.CheckNow();
        Check("after ack no more reminders", ReminderWindow.Created == 1);

        // 新出現的節日（沒確認過）會再提醒，而且是新視窗
        store2.SetSource("a", new[] { H(tomorrow, "測試節日A"), H(tomorrow, "新的節日") });
        rem.CheckNow();
        Check("new unacked holiday triggers reminder again", ReminderWindow.Created == 2 && ReminderWindow.Last.LastLines.Count == 1 && ReminderWindow.Last.LastLines[0].Text == "新的節日");

        // 設定關閉
        settings.HolidayReminder = false;
        var last = ReminderWindow.Last;
        rem.CheckNow();
        Check("disabled setting closes the window and shows nothing", last.Closed);

        // 沒有明天的節日 → 已開的框會被收掉
        settings.HolidayReminder = true;
        var store3 = new ItemStore();
        store3.SetSource("a", new[] { H(tomorrow, "只有一個") });
        var rem3 = new HolidayReminder(store3, settings, AckStore.Load(today));
        rem3.CheckNow();
        var w3 = ReminderWindow.Last;
        store3.SetSource("a", new CalendarItem[0]);
        rem3.CheckNow();
        Check("holiday disappears: window closed", w3.Closed);

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

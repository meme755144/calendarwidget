using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CalendarWidget;

static class P
{
    static int fails;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail)); if (!ok) fails++; }

    static string Wrap(string body) { return "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + body + "END:VCALENDAR\r\n"; }
    static List<CalendarItem> Run(string body, string from = "2026-01-01", string to = "2027-12-31")
    {
        return IcsParser.Parse(Wrap(body), DateTime.Parse(from), DateTime.Parse(to), ItemKind.Event, "#fff");
    }
    static string D(CalendarItem i) { return i.Date.ToString("yyyy-MM-dd"); }
    static string Dates(IEnumerable<CalendarItem> items) { return string.Join(",", items.OrderBy(i => i.Date).Select(D)); }

    static int Main()
    {
        TimeZoneInfo local = TimeZoneInfo.Local;
        Console.WriteLine("local zone: " + local.Id);

        var r = Run("BEGIN:VEVENT\r\nUID:1\r\nDTSTART;VALUE=DATE:20261010\r\nDTEND;VALUE=DATE:20261011\r\nSUMMARY:國慶日\r\nEND:VEVENT\r\n");
        Check("all-day single", r.Count == 1 && D(r[0]) == "2026-10-10" && r[0].Title == "國慶日" && r[0].TimeText == null, Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:2\r\nDTSTART;VALUE=DATE:20260928\r\nDTEND;VALUE=DATE:20261001\r\nSUMMARY:連假\r\nEND:VEVENT\r\n");
        Check("multi-day all-day", Dates(r) == "2026-09-28,2026-09-29,2026-09-30", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:3\r\nDTSTART:20261026T060000Z\r\nDTEND:20261026T070000Z\r\nSUMMARY:UTC會議\r\nEND:VEVENT\r\n");
        DateTime expect = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc), local);
        Check("utc to local", r.Count == 1 && D(r[0]) == expect.ToString("yyyy-MM-dd") && r[0].TimeText == expect.ToString("HH:mm"), r.Count > 0 ? r[0].TimeText : "none");

        r = Run("BEGIN:VEVENT\r\nUID:4\r\nDTSTART;TZID=Asia/Taipei:20261026T140000\r\nSUMMARY:客戶來訪\r\nEND:VEVENT\r\n");
        DateTime tp = TimeZoneInfo.ConvertTime(new DateTime(2026, 10, 26, 14, 0, 0), TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time"), local);
        Check("tzid Asia/Taipei", r.Count == 1 && r[0].TimeText == tp.ToString("HH:mm"), r.Count > 0 ? r[0].TimeText : "none");

        r = Run("BEGIN:VEVENT\r\nUID:5\r\nDTSTART;TZID=America/New_York:20261215T200000\r\nSUMMARY:跨日\r\nEND:VEVENT\r\n");
        DateTime ny = TimeZoneInfo.ConvertTime(new DateTime(2026, 12, 15, 20, 0, 0), TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"), local);
        Check("tz crossing date", r.Count == 1 && D(r[0]) == ny.ToString("yyyy-MM-dd") && r[0].TimeText == ny.ToString("HH:mm"), r.Count > 0 ? D(r[0]) + " " + r[0].TimeText : "none");

        r = Run("BEGIN:VEVENT\r\nUID:6\r\nDTSTART;VALUE=DATE:20261101\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:每日\r\nEND:VEVENT\r\n");
        Check("daily count", Dates(r) == "2026-11-01,2026-11-02,2026-11-03", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:7\r\nDTSTART;VALUE=DATE:20261102\r\nRRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=4\r\nSUMMARY:隔週\r\nEND:VEVENT\r\n");
        Check("weekly interval byday", Dates(r) == "2026-11-02,2026-11-04,2026-11-16,2026-11-18", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:8\r\nDTSTART;VALUE=DATE:20260131\r\nRRULE:FREQ=MONTHLY;BYMONTHDAY=31;COUNT=5\r\nSUMMARY:月底\r\nEND:VEVENT\r\n");
        Check("monthly 31st skips short months", Dates(r) == "2026-01-31,2026-03-31,2026-05-31,2026-07-31,2026-08-31", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:9\r\nDTSTART;VALUE=DATE:20261013\r\nRRULE:FREQ=MONTHLY;BYDAY=2TU;COUNT=3\r\nSUMMARY:第二個週二\r\nEND:VEVENT\r\n");
        Check("monthly 2nd tuesday", Dates(r) == "2026-10-13,2026-11-10,2026-12-08", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:10\r\nDTSTART;VALUE=DATE:20261030\r\nRRULE:FREQ=MONTHLY;BYDAY=-1FR;COUNT=3\r\nSUMMARY:最後週五\r\nEND:VEVENT\r\n");
        Check("monthly last friday", Dates(r) == "2026-10-30,2026-11-27,2026-12-25", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:11\r\nDTSTART;VALUE=DATE:20240229\r\nRRULE:FREQ=YEARLY\r\nSUMMARY:閏日\r\nEND:VEVENT\r\n", "2024-01-01", "2033-01-01");
        Check("yearly feb 29 only leap years", Dates(r) == "2024-02-29,2028-02-29,2032-02-29", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:12\r\nDTSTART;VALUE=DATE:20261101\r\nRRULE:FREQ=DAILY;UNTIL=20261103\r\nSUMMARY:到期\r\nEND:VEVENT\r\n");
        Check("until inclusive", Dates(r) == "2026-11-01,2026-11-02,2026-11-03", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:13\r\nDTSTART;VALUE=DATE:20261101\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEXDATE;VALUE=DATE:20261102\r\nSUMMARY:例外\r\nEND:VEVENT\r\n");
        Check("exdate removes one", Dates(r) == "2026-11-01,2026-11-03,2026-11-04", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:14\r\nDTSTART;VALUE=DATE:20261101\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:原本\r\nEND:VEVENT\r\n" +
                "BEGIN:VEVENT\r\nUID:14\r\nRECURRENCE-ID;VALUE=DATE:20261103\r\nDTSTART;VALUE=DATE:20261105\r\nSUMMARY:改期\r\nEND:VEVENT\r\n");
        Check("recurrence-id moved", Dates(r) == "2026-11-01,2026-11-02,2026-11-05" && r.Count(i => i.Title == "改期") == 1, Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:15\r\nDTSTART;VALUE=DATE:20261101\r\nSTATUS:CANCELLED\r\nSUMMARY:取消\r\nEND:VEVENT\r\n");
        Check("cancelled skipped", r.Count == 0);

        r = Run("BEGIN:VEVENT\r\nUID:16\r\nDTSTART;VALUE=DATE:20261101\r\nSUMMARY:A\\, B\\;C\\nD 很長的\r\n  標題接續\r\nEND:VEVENT\r\n");
        Check("escape and folding", r.Count == 1 && r[0].Title == "A, B;C D 很長的 標題接續", r.Count > 0 ? "[" + r[0].Title + "]" : "none");

        r = Run("BEGIN:VEVENT\r\nUID:17\r\nDTSTART;VALUE=DATE:20261101\r\nSUMMARY:有提醒\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nSUMMARY:鬧鐘\r\nTRIGGER:-PT10M\r\nEND:VALARM\r\nEND:VEVENT\r\n");
        Check("valarm ignored", r.Count == 1 && r[0].Title == "有提醒", r.Count > 0 ? r[0].Title : "none");

        r = Run("BEGIN:VEVENT\r\nUID:18\r\nDTSTART;VALUE=DATE:20261101\r\nRRULE:FREQ=DAILY\r\nSUMMARY:每天\r\nEND:VEVENT\r\n", "2026-11-10", "2026-11-12");
        Check("range filter", Dates(r) == "2026-11-10,2026-11-11,2026-11-12", Dates(r));

        r = Run("BEGIN:VEVENT\r\nUID:19\r\nDTSTART;VALUE=DATE:20261030\r\nRRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=3\r\nSUMMARY:月底工作日\r\nEND:VEVENT\r\n");
        Check("bysetpos last weekday", Dates(r) == "2026-10-30,2026-11-30,2026-12-31", Dates(r));

        // ===== VALARM（行程自己帶的提醒）=====
        Func<string, string> alarm = body => "BEGIN:VALARM\r\n" + body + "END:VALARM\r\n";
        Func<string, string> evWith = alarms => "BEGIN:VEVENT\r\nUID:al\r\nDTSTART;TZID=Asia/Taipei:20261105T140000\r\nSUMMARY:有提醒的會議\r\n" + alarms + "END:VEVENT\r\n";

        r = Run(evWith(alarm("ACTION:DISPLAY\r\nTRIGGER:-PT15M\r\n")));
        Check("alarm -PT15M -> [15]", r.Count == 1 && r[0].Reminders != null && r[0].Reminders.SequenceEqual(new[] { 15 }), r.Count > 0 && r[0].Reminders != null ? string.Join(",", r[0].Reminders) : "null");
        Check("alarm: Start is the local start time", r[0].Start.HasValue && !r[0].AllDay && r[0].Start.Value.ToString("HH:mm") == r[0].TimeText);

        r = Run(evWith(alarm("ACTION:DISPLAY\r\nTRIGGER:-PT10M\r\n") + alarm("ACTION:AUDIO\r\nTRIGGER:-P1D\r\n") + alarm("ACTION:DISPLAY\r\nTRIGGER:-PT10M\r\n")));
        Check("alarm: several alarms sorted and deduplicated", r[0].Reminders.SequenceEqual(new[] { 10, 1440 }), string.Join(",", r[0].Reminders));

        r = Run(evWith(alarm("ACTION:EMAIL\r\nTRIGGER:-PT30M\r\n")));
        Check("alarm: EMAIL action ignored", r[0].Reminders == null);
        r = Run(evWith(alarm("ACTION:DISPLAY\r\nTRIGGER:PT5M\r\n")));
        Check("alarm: trigger after the start ignored", r[0].Reminders == null);
        r = Run(evWith(alarm("ACTION:DISPLAY\r\nTRIGGER;RELATED=END:-PT5M\r\n")));
        Check("alarm: relative to END ignored", r[0].Reminders == null);
        r = Run(evWith(alarm("ACTION:DISPLAY\r\nTRIGGER;VALUE=DATE-TIME:20261105T130000Z\r\n")));
        Check("alarm: absolute trigger ignored", r[0].Reminders == null);
        r = Run(evWith(alarm("TRIGGER:-PT20M\r\n")));
        Check("alarm: missing ACTION treated as display", r[0].Reminders != null && r[0].Reminders.SequenceEqual(new[] { 20 }));
        r = Run(evWith(""));
        Check("no alarm -> null reminders", r[0].Reminders == null);

        // 整天事件：Start 是那天的 00:00，提醒從那裡往前算；跨日只有第一天有
        r = Run("BEGIN:VEVENT\r\nUID:ad\r\nDTSTART;VALUE=DATE:20261110\r\nDTEND;VALUE=DATE:20261113\r\nSUMMARY:出差\r\n" + alarm("ACTION:DISPLAY\r\nTRIGGER:-P1D\r\n") + "END:VEVENT\r\n");
        var first = r.OrderBy(i => i.Date).First();
        Check("all-day alarm: first day has Start/AllDay/Reminders", first.AllDay && first.Start == new DateTime(2026, 11, 10) && first.Reminders.SequenceEqual(new[] { 1440 }));
        Check("all-day alarm: other days carry no reminder", r.Where(i => i.Date > first.Date).All(i => i.Start == null && i.Reminders == null) && r.Count == 3);

        // 重複行程：每一次都帶著提醒
        r = Run("BEGIN:VEVENT\r\nUID:rec\r\nDTSTART;TZID=Asia/Taipei:20261112T090000\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:每日站會\r\n" + alarm("ACTION:DISPLAY\r\nTRIGGER:-PT5M\r\n") + "END:VEVENT\r\n");
        Check("recurring: every occurrence has its own start and the reminder", r.Count == 3 && r.All(i => i.Reminders != null && i.Reminders[0] == 5 && i.Start.HasValue) && r.Select(i => i.Start.Value.Date).Distinct().Count() == 3);

        // VALARM 內的 SUMMARY／DESCRIPTION 不能蓋掉行程標題
        r = Run(evWith(alarm("ACTION:DISPLAY\r\nSUMMARY:鬧鐘標題\r\nDESCRIPTION:鬧鐘內容\r\nTRIGGER:-PT1H\r\n")));
        Check("alarm text does not replace the event title", r[0].Title == "有提醒的會議" && r[0].Reminders.SequenceEqual(new[] { 60 }));

        // 直接測 TRIGGER 的換算
        Check("trigger parse -P1DT2H = 1560", IcsParser.ParseTriggerMinutes("-P1DT2H") == 1560);
        Check("trigger parse -P1W = 10080", IcsParser.ParseTriggerMinutes("-P1W") == 10080);
        Check("trigger parse PT0S = 0", IcsParser.ParseTriggerMinutes("PT0S") == 0);
        Check("trigger parse -PT1H30M = 90", IcsParser.ParseTriggerMinutes("-PT1H30M") == 90);
        Check("trigger parse garbage = null", IcsParser.ParseTriggerMinutes("hello") == null && IcsParser.ParseTriggerMinutes("-P") == null && IcsParser.ParseTriggerMinutes("") == null);

        // ===== 壞資料不能讓程式出事（ICS 可能來自任何網址）=====
        Check("trigger: absurd sizes, full-width digits and huge numbers are ignored (no exception)",
            IcsParser.ParseTriggerMinutes("-P1000000D") == null && IcsParser.ParseTriggerMinutes("-PT１５M") == null && IcsParser.ParseTriggerMinutes("-P99999999999D") == null &&
            IcsParser.ParseTriggerMinutes("-P" + new string('9', 400) + "D") == null);
        Check("trigger: 28 days is the largest accepted", IcsParser.ParseTriggerMinutes("-P28D") == 40320 && IcsParser.ParseTriggerMinutes("-P29D") == null);

        r = Run("BEGIN:VEVENT\r\nUID:bad1\r\nDTSTART;VALUE=DATE:20260105\r\nRRULE:FREQ=YEARLY;INTERVAL=9000\r\nSUMMARY:怪規則\r\nEND:VEVENT\r\n" +
                "BEGIN:VEVENT\r\nUID:bad2\r\nDTSTART;VALUE=DATE:20260107\r\nRRULE:FREQ=DAILY;INTERVAL=2000000000;COUNT=3\r\nSUMMARY:怪間隔\r\nEND:VEVENT\r\n" +
                "BEGIN:VEVENT\r\nUID:bad3\r\nDTSTART;VALUE=DATE:20260108\r\nRRULE:FREQ=WEEKLY;UNTIL=99991231\r\nSUMMARY:無限期\r\nEND:VEVENT\r\n" +
                "BEGIN:VEVENT\r\nUID:bad4\r\nDTSTART:20260109T090000Z\r\nSUMMARY:壞提醒\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-P1000000D\r\nEND:VALARM\r\nEND:VEVENT\r\n" +
                "BEGIN:VEVENT\r\nUID:ok1\r\nDTSTART;VALUE=DATE:20260106\r\nSUMMARY:正常\r\nEND:VEVENT\r\n");
        Check("odd rules / huge intervals / absurd alarms: the normal event is still there and nothing throws",
            r.Any(i => i.Title == "正常") && r.Any(i => i.Title == "怪規則") && r.Count(i => i.Title == "無限期") > 50 && r.Any(i => i.Title == "壞提醒" && i.Reminders == null), string.Join(",", r.Select(i => i.Title).Distinct()));

        // 一個很長的屬性被折成幾萬行（例如內嵌的附件）：不能越接越慢
        var fold = new System.Text.StringBuilder("BEGIN:VEVENT\r\nUID:fold\r\nDTSTART;VALUE=DATE:20260110\r\nSUMMARY:折行\r\nDESCRIPTION:");
        for (int k = 0; k < 20000; k++) fold.Append("\r\n ").Append('x', 70);
        fold.Append("\r\nEND:VEVENT\r\n");
        var foldClock = System.Diagnostics.Stopwatch.StartNew();
        r = Run(fold.ToString());
        Check("a property folded into 20000 lines parses quickly", r.Count == 1 && r[0].Title == "折行" && foldClock.ElapsedMilliseconds < 3000, foldClock.ElapsedMilliseconds + " ms");

        // 跨好幾十年的整天事件：只產生落在範圍內的那幾天，而且很快
        var spanClock = System.Diagnostics.Stopwatch.StartNew();
        r = Run("BEGIN:VEVENT\r\nUID:span\r\nDTSTART;VALUE=DATE:20000101\r\nDTEND;VALUE=DATE:29990101\r\nSUMMARY:合約期間\r\nEND:VEVENT\r\n", "2026-03-01", "2026-03-31");
        Check("a multi-decade all-day event only yields the days inside the range", r.Count == 31 && D(r.OrderBy(i => i.Date).First()) == "2026-03-01" && spanClock.ElapsedMilliseconds < 1000, r.Count + " items, " + spanClock.ElapsedMilliseconds + " ms");

        // IANA 時區名稱對照：表裡每一個都要真的找得到 Windows 時區（打錯字會悄悄變成電腦的時區）
        var missingZones = IcsParser.KnownZoneIds.Where(z => IcsParser.FindZone(z) == null).ToList();
        Check("every mapped time zone exists on this Windows", missingZones.Count == 0, string.Join(",", missingZones));
        r = Run("BEGIN:VEVENT\r\nUID:tz\r\nDTSTART;TZID=America/Toronto:20261105T090000\r\nSUMMARY:多倫多\r\nEND:VEVENT\r\n");
        DateTime toronto = TimeZoneInfo.ConvertTime(new DateTime(2026, 11, 5, 9, 0, 0), TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"), local);
        Check("a zone that used to be unknown (America/Toronto) is now converted", r.Count == 1 && r[0].TimeText == toronto.ToString("HH:mm"), r.Count > 0 ? r[0].TimeText : "none");

        // 多個背景工作同時第一次查同一個時區：不能壞掉（字典不是執行緒安全的）
        var zoneTasks = Enumerable.Range(0, 16).Select(n => System.Threading.Tasks.Task.Run(() =>
        {
            foreach (string z in IcsParser.KnownZoneIds) IcsParser.FindZone(z + (n % 2 == 0 ? "" : "/x"));
        })).ToArray();
        bool zonesOk;
        try { zonesOk = System.Threading.Tasks.Task.WaitAll(zoneTasks, 20000); } catch (AggregateException) { zonesOk = false; }
        Check("looking up time zones from many threads at once works", zonesOk);

        bool noThrow = true;
        try { Run("garbage\r\nBEGIN:VEVENT\r\nDTSTART:not-a-date\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nSUMMARY:no start\r\nEND:VEVENT\r\n"); } catch (Exception) { noThrow = false; }
        Check("malformed input does not throw", noThrow);

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CalendarWidget;

static class P
{
    static int fails;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail)); if (!ok) fails++; }

    static string Dir;

    static List<HolidayEntry> Load(int year)
    {
        string error;
        var entries = HolidayCalendar.Parse(File.ReadAllText(Path.Combine(Dir, year + ".json"), Encoding.UTF8), year, out error);
        if (entries == null) throw new Exception(year + ": " + error);
        return entries;
    }

    static int Main(string[] args)
    {
        Dir = args[0];   // 放 2025.json / 2026.json / 2027.json 的資料夾
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA"))) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }

        // ===== 可以選的年度 =====
        var before = HolidayCalendar.AvailableYears(new DateTime(2026, 5, 31));
        var after = HolidayCalendar.AvailableYears(new DateTime(2026, 6, 1));
        var nextYear = HolidayCalendar.AvailableYears(new DateTime(2027, 1, 15));
        Check("years: before June the newest choice is this year", before.First() == 2026 && !before.Contains(2027));
        Check("years: from June on, next year becomes available", after.First() == 2027 && after.Contains(2026));
        Check("years: newest first, down to the first year with data, no gaps", after.Last() == HolidayCalendar.FirstYear && after.SequenceEqual(after.OrderByDescending(y => y)) && after.Count == 2027 - HolidayCalendar.FirstYear + 1);
        Check("years: in January the newest is still this year", nextYear.First() == 2027 && !nextYear.Contains(2028));

        // ===== 解析 =====
        var e26 = Load(2026);
        var e25 = Load(2025);
        Check("parse: a full year has all its days in date order", e26.Count == 365 && e26.First().Date == new DateTime(2026, 1, 1) && e26.Last().Date == new DateTime(2026, 12, 31) &&
            e26.Zip(e26.Skip(1), (a, b) => a.Date < b.Date).All(x => x));
        string err;
        Check("parse: data of another year is rejected", HolidayCalendar.Parse(File.ReadAllText(Path.Combine(Dir, "2026.json"), Encoding.UTF8), 2027, out err) == null && err != null && err.Contains("不完整"), err);
        Check("parse: a truncated list is rejected", HolidayCalendar.Parse("[{\"date\":\"20260101\",\"week\":\"四\",\"isHoliday\":true,\"description\":\"開國紀念日\"}]", 2026, out err) == null && err.Contains("不完整"), err);
        Check("parse: not JSON / not a list is rejected", HolidayCalendar.Parse("<html>nope</html>", 2026, out err) == null && HolidayCalendar.Parse("{\"a\":1}", 2026, out err) == null && HolidayCalendar.Parse("", 2026, out err) == null);

        // ===== 事件：預設（國定假日＋補班日，連續合併）=====
        var def26 = HolidayCalendar.BuildEvents(e26, new HolidayOptions());
        var cny = def26.FirstOrDefault(e => e.Title == "春節");
        Check("events 2026: consecutive same-name holidays merge (春節 17~19 Feb = 3 days)", cny != null && cny.Start == new DateTime(2026, 2, 17) && cny.EndExclusive == new DateTime(2026, 2, 20), cny == null ? "missing" : cny.Start + " " + cny.EndExclusive);
        Check("events 2026: 22 named holiday days become 20 events (春節 3 days -> 1; 補假 are separate), no make-up days that year",
            def26.Count == 20 && def26.All(e => e.Kind == HolidayKind.Holiday), def26.Count + "");
        Check("events 2026: the day count adds up (merged events cover the same days)", def26.Sum(e => (e.EndExclusive - e.Start).Days) == e26.Count(x => x.IsHoliday && x.Description.Length > 0));
        var noMerge = HolidayCalendar.BuildEvents(e26, new HolidayOptions { Merge = false });
        Check("events 2026: without merging every named holiday is its own event", noMerge.Count == 22 && noMerge.All(e => (e.EndExclusive - e.Start).Days == 1));
        var withWeekends = HolidayCalendar.BuildEvents(e26, new HolidayOptions { Weekends = true });
        int weekendCount = e26.Count(x => x.IsHoliday && x.Description.Length == 0 && x.IsWeekend);
        Check("events 2026: weekends are added only when asked, one event per day, titled 週六 / 週日",
            withWeekends.Count(e => e.Kind == HolidayKind.Weekend) == weekendCount && weekendCount > 90 &&
            withWeekends.Where(e => e.Kind == HolidayKind.Weekend).All(e => (e.Title == "週六" && e.Start.DayOfWeek == DayOfWeek.Saturday) || (e.Title == "週日" && e.Start.DayOfWeek == DayOfWeek.Sunday)) &&
            def26.All(e => e.Kind != HolidayKind.Weekend));
        var onlyWeekends = HolidayCalendar.BuildEvents(e26, new HolidayOptions { Holidays = false, MakeUpDays = false, Weekends = true });
        Check("events 2026: only-weekends option excludes holidays", onlyWeekends.All(e => e.Kind == HolidayKind.Weekend) && onlyWeekends.Count == weekendCount);
        Check("events: nothing selected gives nothing", HolidayCalendar.BuildEvents(e26, new HolidayOptions { Holidays = false, MakeUpDays = false, Weekends = false }).Count == 0);

        // ===== 補班日（2025/2/8 週六補行上班）=====
        var def25 = HolidayCalendar.BuildEvents(e25, new HolidayOptions());
        var makeUp = def25.Where(e => e.Kind == HolidayKind.MakeUp).ToList();
        Check("events 2025: the make-up Saturday is found, titled 補班日 with the official note", makeUp.Count == 1 && makeUp[0].Start == new DateTime(2025, 2, 8) && makeUp[0].Title == "補班日" && makeUp[0].Note == "補行上班", makeUp.Count + "");
        Check("events 2025: make-up days can be switched off", HolidayCalendar.BuildEvents(e25, new HolidayOptions { MakeUpDays = false }).All(e => e.Kind != HolidayKind.MakeUp));
        Check("events 2025: 春節 28 Jan (除夕) to 31 Jan stays split by name (小年夜 / 農曆除夕 / 春節 x3)",
            def25.Any(e => e.Title == "春節" && e.Start == new DateTime(2025, 1, 29) && e.EndExclusive == new DateTime(2025, 2, 1)) && def25.Any(e => e.Title == "農曆除夕") && def25.Any(e => e.Title == "小年夜"));

        // ===== ICS：用月曆自己的解析器讀回來 =====
        string ics = HolidayCalendar.BuildIcs(def25, "台灣行事曆 2025", false, new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc));
        Check("ics: CRLF line endings, proper envelope, calendar name, all-day dates", ics.StartsWith("BEGIN:VCALENDAR\r\n") && ics.EndsWith("END:VCALENDAR\r\n") && ics.Contains("X-WR-CALNAME:台灣行事曆 2025") &&
            ics.Contains("DTSTART;VALUE=DATE:20250129") && ics.Contains("DTEND;VALUE=DATE:20250201") && !ics.Replace("\r\n", "").Contains("\n"));
        Check("ics: events are marked as free time (do not block your calendar)", ics.Split(new[] { "BEGIN:VEVENT" }, StringSplitOptions.None).Skip(1).All(b => b.Contains("TRANSP:TRANSPARENT")));
        var physical = ics.Split(new[] { "\r\n" }, StringSplitOptions.None);
        Check("ics: no physical line is longer than 75 bytes", physical.All(l => Encoding.UTF8.GetByteCount(l) <= 75), physical.OrderByDescending(l => Encoding.UTF8.GetByteCount(l)).First());
        var parsed = IcsParser.Parse(ics, new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), ItemKind.Holiday, "#FF6B7A");
        int expectedDays = def25.Sum(e => (e.EndExclusive - e.Start).Days);
        Check("ics: our own parser reads back exactly the same days", parsed.Count == expectedDays && parsed.All(i => i.AllDay && i.Kind == ItemKind.Holiday), parsed.Count + " vs " + expectedDays);
        Check("ics: titles and dates survive (春節 over 3 days, 補班日 on 8 Feb)",
            parsed.Count(i => i.Title == "春節") == 3 && parsed.Any(i => i.Title == "補班日" && i.Date == new DateTime(2025, 2, 8)) && parsed.Any(i => i.Title == "國慶日" && i.Date == new DateTime(2025, 10, 10)));
        Check("ics: no alarm unless asked", parsed.All(i => i.Reminders == null));

        string icsAlarm = HolidayCalendar.BuildIcs(HolidayCalendar.BuildEvents(e25, new HolidayOptions { Weekends = true, Alarm = true }), "x", true, DateTime.UtcNow);
        var parsedAlarm = IcsParser.Parse(icsAlarm, new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), ItemKind.Event, "#fff");
        var newYear = parsedAlarm.First(i => i.Title == "開國紀念日");
        var aSaturday = parsedAlarm.First(i => i.Title == "週六");
        Check("ics: alarm = the day before at 09:00 (900 minutes from the start of the day) on holidays and make-up days, not on weekends",
            newYear.Reminders != null && newYear.Reminders.SequenceEqual(new[] { 900 }) && aSaturday.Reminders == null &&
            parsedAlarm.First(i => i.Title == "補班日").Reminders != null, newYear.Reminders == null ? "null" : string.Join(",", newYear.Reminders));
        var multiAlarm = parsedAlarm.Where(i => i.Title == "春節").OrderBy(i => i.Date).ToList();
        Check("ics: for a merged multi-day holiday only the first day carries the alarm", multiAlarm.Count == 3 && multiAlarm[0].Reminders != null && multiAlarm[1].Reminders == null && multiAlarm[2].Reminders == null);

        var tricky = new List<HolidayEvent> { new HolidayEvent { Start = new DateTime(2026, 3, 1), EndExclusive = new DateTime(2026, 3, 2), Kind = HolidayKind.Holiday, Title = "A,B;C\\D 一個很長很長很長很長很長很長很長很長很長很長很長很長很長很長很長很長很長很長很長很長的名稱", Note = "說明" } };
        var trickyParsed = IcsParser.Parse(HolidayCalendar.BuildIcs(tricky, "t", false, DateTime.UtcNow), new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ItemKind.Event, "#fff");
        Check("ics: special characters are escaped and long lines are folded without breaking the text", trickyParsed.Count == 1 && trickyParsed[0].Title == tricky[0].Title, trickyParsed.Count == 0 ? "none" : trickyParsed[0].Title);

        // ===== 下載（用假伺服器）=====
        Environment.SetEnvironmentVariable("CALENDARWIDGET_HOLIDAY_URL", "http://127.0.0.1:18767/{year}.json");
        Check("url: test override replaces {year}", HolidayCalendar.DataUrl(2026) == "http://127.0.0.1:18767/2026.json", HolidayCalendar.DataUrl(2026));
        var downloaded = HolidayCalendar.Download(2027);
        Check("download: fetches, validates and parses a year", downloaded.Count == 365 && downloaded.Any(e => e.Description == "春節"));
        string notYet = null;
        try { HolidayCalendar.Download(2028); } catch (InvalidDataException ex) { notYet = ex.Message; }
        Check("download: a year that is not published yet gives a clear message", notYet != null && notYet.Contains("2028") && notYet.Contains("6 月"), notYet);
        Environment.SetEnvironmentVariable("CALENDARWIDGET_HOLIDAY_URL", "http://127.0.0.1:1/{year}.json");
        string offline = null;
        try { HolidayCalendar.Download(2026); } catch (Exception ex) { offline = ex.GetType().Name; }
        Check("download: an unreachable server surfaces as a network error", offline == "WebException", offline);
        Environment.SetEnvironmentVariable("CALENDARWIDGET_HOLIDAY_URL", null);
        Check("url: default is the jsDelivr copy of ruyut/TaiwanCalendar", HolidayCalendar.DataUrl(2026) == "https://cdn.jsdelivr.net/gh/ruyut/TaiwanCalendar/data/2026.json");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

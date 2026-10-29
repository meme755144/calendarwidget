using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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

        // ===== 放假日標示（OffDayStore）：背景下載、快取、退回週六日 =====
        string cacheDir = Path.Combine(DataPaths.LocalDir, "holidays");
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        Environment.SetEnvironmentVariable("CALENDARWIDGET_HOLIDAY_URL", "http://127.0.0.1:18767/{year}.json");
        var today = new DateTime(2026, 10, 28);
        var oct24 = new DateTime(2026, 10, 24);   // 週六
        var oct25 = new DateTime(2026, 10, 25);   // 週日，光復節
        var oct26 = new DateTime(2026, 10, 26);   // 週一，補假
        var oct27 = new DateTime(2026, 10, 27);   // 週二，一般上班日

        var off = new OffDayStore();
        var arrived = new ManualResetEvent(false);
        off.Changed += delegate { arrived.Set(); };
        Check("offdays: with no data yet only Saturday and Sunday count", !off.IsOff(oct26) && !off.IsOff(oct27) && off.IsOff(oct24) && off.IsOff(oct25) && off.Describe(oct26) == null);
        off.Prefetch(new DateTime(2026, 10, 1), new DateTime(2026, 11, 11), today);
        Check("offdays: the year arrives in the background and raises Changed", arrived.WaitOne(10000));
        Check("offdays: a weekday make-up holiday (補假) is off and says why", off.IsOff(oct26) && off.Describe(oct26) == "補假", off.Describe(oct26));
        Check("offdays: a normal weekday is not off; a plain Saturday is off without a note", !off.IsOff(oct27) && off.Describe(oct27) == null && off.IsOff(oct24) && off.Describe(oct24) == null);
        Check("offdays: a named holiday on a Sunday keeps its name", off.IsOff(oct25) && off.Describe(oct25) != null && off.Describe(oct25).Contains("光復"), off.Describe(oct25));
        string cached = Path.Combine(cacheDir, "2026.json");
        string cacheErr;
        Check("offdays: the downloaded year is cached as valid JSON", File.Exists(cached) && HolidayCalendar.Parse(File.ReadAllText(cached, Encoding.UTF8), 2026, out cacheErr) != null);

        // 快取有而且很新：就算伺服器連不上也有資料，也不會再下載
        Environment.SetEnvironmentVariable("CALENDARWIDGET_HOLIDAY_URL", "http://127.0.0.1:1/{year}.json");
        var fromCache = new OffDayStore();
        bool fromCacheChanged = false;
        fromCache.Changed += delegate { fromCacheChanged = true; };
        fromCache.Prefetch(new DateTime(2026, 10, 1), new DateTime(2026, 11, 11), today);
        Thread.Sleep(500);
        Check("offdays: a fresh cache is used right away, without any download", fromCache.IsOff(oct26) && fromCache.Describe(oct26) == "補假" && !fromCacheChanged);

        // 補班的週六：不放假、不標紅，標題會註明
        Environment.SetEnvironmentVariable("CALENDARWIDGET_HOLIDAY_URL", "http://127.0.0.1:18767/{year}.json");
        var off25 = new OffDayStore();
        var arrived25 = new ManualResetEvent(false);
        off25.Changed += delegate { arrived25.Set(); };
        off25.Prefetch(new DateTime(2025, 2, 1), new DateTime(2025, 2, 28), today);
        Check("offdays: a past year is downloaded when there is no cache", arrived25.WaitOne(10000));
        var makeUpSat = new DateTime(2025, 2, 8);   // 週六補班
        Check("offdays: a make-up Saturday is not off and is labelled 補班日", !off25.IsOff(makeUpSat) && off25.Describe(makeUpSat) == "補班日", off25.Describe(makeUpSat));
        Check("offdays: the next ordinary Saturday is still off", off25.IsOff(new DateTime(2025, 2, 15)));

        // 過去的年度不會再改：快取再舊也不重新下載
        string cached25 = Path.Combine(cacheDir, "2025.json");
        File.SetLastWriteTimeUtc(cached25, DateTime.UtcNow.AddDays(-100));
        var pastStore = new OffDayStore();
        bool pastChanged = false;
        pastStore.Changed += delegate { pastChanged = true; };
        pastStore.Prefetch(new DateTime(2025, 2, 1), new DateTime(2025, 2, 28), today);
        Thread.Sleep(700);
        Check("offdays: an old cache of a past year is kept as is", !pastChanged && pastStore.Describe(makeUpSat) == "補班日" && File.GetLastWriteTimeUtc(cached25) < DateTime.UtcNow.AddDays(-99));

        // 今年的快取超過 7 天：先用舊的，背景更新
        File.SetLastWriteTimeUtc(cached, DateTime.UtcNow.AddDays(-10));
        var stale = new OffDayStore();
        var refreshed = new ManualResetEvent(false);
        stale.Changed += delegate { refreshed.Set(); };
        stale.Prefetch(new DateTime(2026, 10, 1), new DateTime(2026, 11, 11), today);
        Check("offdays: a stale cache of this year is used immediately", stale.IsOff(oct26));
        Check("offdays: ...and refreshed in the background", refreshed.WaitOne(10000) && File.GetLastWriteTimeUtc(cached) > DateTime.UtcNow.AddDays(-1));

        // 快取壞掉：當作沒有，重新下載
        File.WriteAllText(cached, "not json", Encoding.UTF8);
        var broken = new OffDayStore();
        var healed = new ManualResetEvent(false);
        broken.Changed += delegate { healed.Set(); };
        broken.Prefetch(new DateTime(2026, 10, 1), new DateTime(2026, 11, 11), today);
        Check("offdays: a broken cache file is ignored", !broken.IsOff(oct26));
        Check("offdays: ...and the data is downloaded again", healed.WaitOne(10000) && broken.IsOff(oct26));

        // 關掉、唯讀、官方還沒公布的年度：不連網、不存檔
        string cached27 = Path.Combine(cacheDir, "2027.json");
        var disabled = new OffDayStore { Enabled = false };
        disabled.Prefetch(new DateTime(2027, 1, 1), new DateTime(2027, 1, 31), today);
        Settings.ReadOnly = true;
        var readOnly = new OffDayStore();
        readOnly.Prefetch(new DateTime(2027, 1, 1), new DateTime(2027, 1, 31), today);
        Settings.ReadOnly = false;
        var tooEarly = new OffDayStore();
        tooEarly.Prefetch(new DateTime(2027, 1, 1), new DateTime(2027, 1, 31), new DateTime(2026, 5, 31));   // 6 月才開放下一年
        Thread.Sleep(700);
        Check("offdays: turned off, read-only and not-yet-published years do not download anything", !File.Exists(cached27));
        Check("offdays: turned off means plain weekend marking even with data cached", !new OffDayStore { Enabled = false }.IsOff(oct26) && new OffDayStore { Enabled = false }.IsOff(oct24));

        // 連不上：退回週六日，不會當掉
        Environment.SetEnvironmentVariable("CALENDARWIDGET_HOLIDAY_URL", "http://127.0.0.1:1/{year}.json");
        var unreachable = new OffDayStore();
        bool unreachableChanged = false;
        unreachable.Changed += delegate { unreachableChanged = true; };
        unreachable.Prefetch(new DateTime(2027, 1, 1), new DateTime(2027, 1, 31), today);
        Thread.Sleep(1500);
        Check("offdays: an unreachable server leaves the weekend fallback in place", !unreachableChanged && !unreachable.IsOff(new DateTime(2027, 1, 4)) && unreachable.IsOff(new DateTime(2027, 1, 2)));
        Environment.SetEnvironmentVariable("CALENDARWIDGET_HOLIDAY_URL", null);

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

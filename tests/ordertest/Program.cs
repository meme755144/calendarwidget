using System;
using System.IO;
using System.Linq;
using CalendarWidget;

static class P
{
    static int fails;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail)); if (!ok) fails++; }

    static CalendarItem I(string cal, ItemKind kind, string title, string time, string color)
    {
        return new CalendarItem { Date = new DateTime(2026, 10, 26), CalendarId = cal, Kind = kind, Title = title, TimeText = time, Color = color };
    }

    static string Titles(ItemStore s) { return string.Join(",", s.For(new DateTime(2026, 10, 26)).Select(i => i.Title)); }
    static string Colors(ItemStore s) { return string.Join(",", s.For(new DateTime(2026, 10, 26)).Select(i => i.Color).Distinct()); }

    static int Main()
    {
        string dir = Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA");
        if (string.IsNullOrEmpty(dir)) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        var store = new ItemStore();
        store.SetSource("ics", new[]
        {
            I("ics:a", ItemKind.Event, "ICS 下午", "14:00", "#111111"),
            I("ics:a", ItemKind.Event, "ICS 上午", "09:00", "#111111"),
            I("ics:a", ItemKind.Event, "ICS 整天", null, "#111111")
        });
        store.SetSource("holiday", new[] { I("g:holiday", ItemKind.Holiday, "節日", null, "#FF0000") });
        store.SetSource("tasks", new[] { I(CalendarIds.GoogleTasks, ItemKind.GoogleTask, "Google 待辦", null, "#FFFF00") });
        store.SetSource("local", new[] { I(CalendarIds.Local, ItemKind.LocalTodo, "本機待辦", null, "#00FF00") });

        // 沒設定過順序：依種類 節日 → 行程 → Google 待辦 → 本機待辦；同一個日曆內整天事件在前，再依時間
        Check("default order by kind, then all-day, then time", Titles(store) == "節日,ICS 整天,ICS 上午,ICS 下午,Google 待辦,本機待辦", Titles(store));
        Check("default dot color order", Colors(store) == "#FF0000,#111111,#FFFF00,#00FF00", Colors(store));

        // 使用者調整順序：本機待辦最前面，其次 Google 待辦，ICS，最後節日
        store.SetOrder(new[] { CalendarIds.Local, CalendarIds.GoogleTasks, "ics:a", "g:holiday" });
        Check("custom order applied", Titles(store) == "本機待辦,Google 待辦,ICS 整天,ICS 上午,ICS 下午,節日", Titles(store));
        Check("custom dot color order follows calendar order", Colors(store) == "#00FF00,#FFFF00,#111111,#FF0000", Colors(store));

        // 順序裡有已經不存在的日曆、或少了某個日曆：不影響，沒列到的排在後面
        store.SetOrder(new[] { "ics:gone", "g:holiday", "ics:a" });
        Check("unknown ids ignored, unlisted calendars go last by kind", Titles(store) == "節日,ICS 整天,ICS 上午,ICS 下午,Google 待辦,本機待辦", Titles(store));

        // 重複的 id 不會出錯
        store.SetOrder(new[] { "ics:a", "ics:a", CalendarIds.Local });
        Check("duplicate ids tolerated", Titles(store).StartsWith("ICS 整天,ICS 上午,ICS 下午,本機待辦"), Titles(store));

        // 順序檔的存取
        CalendarOrder.Save(new[] { "local", "ics:a", "g:x" });
        var back = CalendarOrder.Load();
        Check("order file roundtrip", back.SequenceEqual(new[] { "local", "ics:a", "g:x" }), string.Join("|", back));
        CalendarOrder.Save(new[] { "a", "b", "a" });
        Check("order file dedupes", CalendarOrder.Load().SequenceEqual(new[] { "a", "b" }));
        Settings.ReadOnly = true;
        CalendarOrder.Save(new[] { "should-not-write" });
        Settings.ReadOnly = false;
        Check("readonly does not write order", CalendarOrder.Load().SequenceEqual(new[] { "a", "b" }));

        // 設定檔多了「摺起來」的狀態
        var s = new Settings { DetailCollapsed = true };
        s.Save();
        Check("DetailCollapsed persisted", Settings.Load().DetailCollapsed);

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

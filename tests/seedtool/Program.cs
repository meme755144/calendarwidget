using System;
using CalendarWidget;

static class P
{
    static int Main()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA"))) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }
        var s = new GoogleSecrets { ClientId = "test-client", ClientSecret = "test-secret", RefreshToken = "RT-1", Email = "me@example.com",
            Scopes = "https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/tasks.readonly https://www.googleapis.com/auth/calendar.events" };
        s.Save();
        var c = new GoogleConfig();
        c.Calendars.Add(new GoogleCalendarEntry { Id = "me@example.com", Name = "me@example.com", Enabled = true, CanWrite = 1, Color = "#9fe1e7" });
        c.Calendars.Add(new GoogleCalendarEntry { Id = "zh-tw.taiwan#holiday@group.v.calendar.google.com", Name = "台灣的節日", Enabled = true, CanWrite = 0, Kind = ItemKind.Holiday, Color = "#16a765" });
        c.Save();
        Console.WriteLine("seeded");
        return 0;
    }
}

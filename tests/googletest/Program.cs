using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CalendarWidget;

static class P
{
    const string Fake = "http://127.0.0.1:18766";
    static int fails;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail)); if (!ok) fails++; }

    static Dictionary<string, string> Query(string url)
    {
        var d = new Dictionary<string, string>();
        foreach (string pair in new Uri(url).Query.TrimStart('?').Split('&'))
        {
            int eq = pair.IndexOf('=');
            d[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
        }
        return d;
    }

    static void Get(string url)
    {
        try { using (var r = WebRequest.Create(url).GetResponse()) { } }
        catch (WebException) { }
    }

    static void Post(string url) { var req = (HttpWebRequest)WebRequest.Create(url); req.Method = "POST"; req.ContentLength = 0; using (req.GetResponse()) { } }

    static string DownloadUtf8(string url)
    {
        using (var wc = new System.Net.WebClient { Encoding = System.Text.Encoding.UTF8 }) return wc.DownloadString(url);
    }

    static GoogleException Expect(Action a)
    {
        try { a(); }
        catch (GoogleException ex) { return ex; }
        return null;
    }

    static int Main() { try { return Main2(); } catch (Exception e) { Console.WriteLine("CRASH " + e.GetType().FullName + ": " + e.Message); var i = e.InnerException; while (i != null) { Console.WriteLine("  inner " + i.GetType().FullName + ": " + i.Message); i = i.InnerException; } Console.WriteLine(e.StackTrace); return 9; } }
    static int Main2()
    {
        string dir = Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA");
        if (string.IsNullOrEmpty(dir)) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        var secrets = new GoogleSecrets { ClientId = "test-client", ClientSecret = "test-secret" };
        var auth = new GoogleAuth(secrets);
        var client = new GoogleClient(auth);

        // 1. 完整的登入流程（用假瀏覽器：照網址上的參數，把授權碼送回本機接收的位址）
        string seenUrl = null;
        auth.OpenBrowser = url =>
        {
            seenUrl = url;
            var q = Query(url);
            Post(Fake + "/register?code=CODE123&challenge=" + Uri.EscapeDataString(q["code_challenge"]));
            Task.Run(() => { Thread.Sleep(300); Get(q["redirect_uri"] + "/favicon.ico"); Get(q["redirect_uri"] + "/?code=CODE123&state=" + Uri.EscapeDataString(q["state"])); });
        };
        auth.Authorize(TimeSpan.FromSeconds(30));
        var qs = Query(seenUrl);
        Check("auth url has PKCE S256, offline access, consent, readonly scopes",
            qs["code_challenge_method"] == "S256" && qs["access_type"] == "offline" && qs["prompt"] == "consent" &&
            qs["scope"].Contains("calendar.readonly") && qs["scope"].Contains("tasks.readonly") && qs["redirect_uri"].StartsWith("http://127.0.0.1:"), seenUrl);
        Check("refresh token stored after authorize", secrets.RefreshToken == "RT-1", secrets.RefreshToken);
        Check("secrets persisted and encrypted on disk",
            GoogleSecrets.Load().RefreshToken == "RT-1" && !File.ReadAllText(Path.Combine(DataPaths.LocalDir, "google.dat")).Contains("RT-1")
            && !File.ReadAllText(Path.Combine(DataPaths.LocalDir, "google.dat")).Contains("test-secret"));

        // 2. state 不符
        auth.OpenBrowser = url =>
        {
            var q = Query(url);
            Post(Fake + "/register?code=CODE123&challenge=" + Uri.EscapeDataString(q["code_challenge"]));
            Task.Run(() => { Thread.Sleep(200); Get(q["redirect_uri"] + "/?code=CODE123&state=WRONG"); });
        };
        var ex = Expect(() => auth.Authorize(TimeSpan.FromSeconds(20)));
        Check("state mismatch aborts", ex != null && ex.Message.Contains("不符"), ex == null ? "no exception" : ex.Message);

        // 3. 使用者拒絕授權
        auth.OpenBrowser = url =>
        {
            var q = Query(url);
            Task.Run(() => { Thread.Sleep(200); Get(q["redirect_uri"] + "/?error=access_denied&state=" + Uri.EscapeDataString(q["state"])); });
        };
        ex = Expect(() => auth.Authorize(TimeSpan.FromSeconds(20)));
        Check("access_denied reported", ex != null && ex.Message.Contains("拒絕"), ex == null ? "no exception" : ex.Message);

        // 4. 取消登入
        auth.OpenBrowser = url => Task.Run(() => { Thread.Sleep(600); auth.Cancel(); });
        ex = Expect(() => auth.Authorize(TimeSpan.FromSeconds(20)));
        Check("cancel stops waiting", ex != null && ex.Message.Contains("取消"), ex == null ? "no exception" : ex.Message);

        // 5. 逾時
        auth.OpenBrowser = url => { };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ex = Expect(() => auth.Authorize(TimeSpan.FromSeconds(2)));
        Check("timeout", ex != null && ex.Message.Contains("逾時") && sw.Elapsed < TimeSpan.FromSeconds(6), ex == null ? "no exception" : ex.Message + " " + sw.Elapsed);

        // 5b. 使用者登入很慢（3.5 秒後才送回結果）：真實情況下人需要幾十秒，不能因為等太久就收不到
        auth.OpenBrowser = url =>
        {
            var q = Query(url);
            Post(Fake + "/register?code=CODE123&challenge=" + Uri.EscapeDataString(q["code_challenge"]));
            Task.Run(() => { Thread.Sleep(3500); Get(q["redirect_uri"] + "/?code=CODE123&state=" + Uri.EscapeDataString(q["state"])); });
        };
        secrets.RefreshToken = "";
        ex = Expect(() => auth.Authorize(TimeSpan.FromSeconds(10)));
        Check("late redirect (3.5s after opening browser) is still received", ex == null && secrets.RefreshToken == "RT-1", ex == null ? "token=" + secrets.RefreshToken : ex.Message);

        // 5c. 更久（12 秒）
        auth.OpenBrowser = url =>
        {
            var q = Query(url);
            Post(Fake + "/register?code=CODE123&challenge=" + Uri.EscapeDataString(q["code_challenge"]));
            Task.Run(() => { Thread.Sleep(12000); Get(q["redirect_uri"] + "/?code=CODE123&state=" + Uri.EscapeDataString(q["state"])); });
        };
        secrets.RefreshToken = "";
        ex = Expect(() => auth.Authorize(TimeSpan.FromSeconds(30)));
        Check("very late redirect (12s) is still received", ex == null && secrets.RefreshToken == "RT-1", ex == null ? "token=" + secrets.RefreshToken : ex.Message);

        // 6. 存取權杖：同一個權杖會重複使用，不會每次都重新換
        string t1 = auth.AccessToken(), t2 = auth.AccessToken();
        Check("access token cached", t1 == t2 && t1.StartsWith("AT-"), t1 + " " + t2);

        // 7. 日曆清單
        string email;
        var cals = client.ListCalendars(out email);
        Check("calendar list", cals.Count == 4 && email == "me@example.com", cals.Count + " " + email);
        var holiday = cals.First(c => c.Id.StartsWith("zh-tw.taiwan"));
        Check("holiday calendar defaults to Holiday kind", holiday.Kind == ItemKind.Holiday && holiday.Name == "台灣的節日");
        Check("summaryOverride used as name", cals.First(c => c.Id.StartsWith("team@")).Name == "我的團隊");
        Check("default enabled = selected or primary", cals.Count(c => c.Enabled) == 2 && cals.First(c => c.Id == "me@example.com").Enabled && holiday.Enabled);
        Check("color from google, fallback when missing", cals.First(c => c.Id == "me@example.com").Color == "#9fe1e7" && cals.First(c => c.Id.StartsWith("forbidden")).Color == "#6CB4FF");

        // 8. 行程：分頁、時間、整天、跨日、取消、無標題、UTC
        DateTime today = DateTime.Today;
        var events = client.FetchEvents("me@example.com", "#123456", ItemKind.Event, today.AddDays(-62), today.AddDays(400));
        string D(int off) { return today.AddDays(off).ToString("yyyy-MM-dd"); }
        Check("paging fetched both pages", events.Any(e => e.Title == "晨會") && events.Any(e => e.Title == "連休三天"), string.Join("|", events.Select(e => e.Title)));
        var visit = events.First(e => e.Title == "軟體園區主任來訪");
        DateTime expectVisit = new DateTimeOffset(today.Year, today.Month, today.Day, 14, 0, 0, TimeSpan.FromHours(8)).ToLocalTime().DateTime;
        Check("timed event converted to local time", visit.TimeText == expectVisit.ToString("HH:mm") && visit.Date == expectVisit.Date && visit.Color == "#123456" && visit.Kind == ItemKind.Event, visit.TimeText);
        Check("multi-day all-day expanded to 3 days", events.Count(e => e.Title == "連休三天") == 3 &&
            events.Where(e => e.Title == "連休三天").Select(e => e.Date.ToString("yyyy-MM-dd")).OrderBy(x => x).SequenceEqual(new[] { D(3), D(4), D(5) }));
        Check("cancelled event skipped", !events.Any(e => e.Title == "已取消的會議"));
        Check("missing title gets placeholder", events.Any(e => e.Title == "（無標題）" && e.Date == today.AddDays(1) && e.TimeText == null));
        var utcEv = events.First(e => e.Title == "UTC 時間的行程");
        DateTime expectUtc = DateTime.SpecifyKind(today.AddDays(2).AddHours(2), DateTimeKind.Utc).ToLocalTime();
        Check("UTC event converted", utcEv.TimeText == expectUtc.ToString("HH:mm") && utcEv.Date == expectUtc.Date, utcEv.TimeText + " " + utcEv.Date);

        // 提醒：useDefault 用日曆預設（只留 popup）、自己的設定只留 popup、整天事件、沒有 reminders 欄位就沒有提醒
        Check("reminders: useDefault uses the calendar's popup defaults (email dropped)", visit.Reminders != null && visit.Reminders.SequenceEqual(new[] { 10 }), visit.Reminders == null ? "null" : string.Join(",", visit.Reminders));
        var morning = events.First(e => e.Title == "晨會");
        Check("reminders: own overrides keep only popup, sorted", morning.Reminders != null && morning.Reminders.SequenceEqual(new[] { 5, 1440 }), morning.Reminders == null ? "null" : string.Join(",", morning.Reminders));
        Check("reminders: Start is the local start time", visit.Start.HasValue && visit.Start.Value == expectVisit && !visit.AllDay);
        var holiday3 = events.Where(e => e.Title == "連休三天").OrderBy(e => e.Date).ToList();
        Check("reminders: all-day event carries them on the first day only", holiday3[0].AllDay && holiday3[0].Start == today.AddDays(3) && holiday3[0].Reminders.SequenceEqual(new[] { 900 }) && holiday3.Skip(1).All(e => e.Start == null && e.Reminders == null));
        var untitled = events.First(e => e.Title == "（無標題）");
        Check("reminders: event without a reminders field has none", untitled.Reminders == null && events.First(e => e.Title == "UTC 時間的行程").Reminders == null);

        var holidays = client.FetchEvents(holiday.Id, "#ff0000", ItemKind.Holiday, today.AddDays(-62), today.AddDays(400));
        Check("holiday calendar events carry Holiday kind", holidays.Count == 1 && holidays[0].Kind == ItemKind.Holiday && holidays[0].Date == today.AddDays(1) && holidays[0].Title == "光復節");

        // 範圍外的不收
        var narrow = client.FetchEvents("me@example.com", "#123456", ItemKind.Event, today, today);
        Check("range filter on client side", narrow.All(e => e.Date == today), string.Join(",", narrow.Select(e => e.Date.ToString("MM-dd"))));

        // 9. 待辦
        var tasks = client.FetchTasks("#ffd166", today.AddDays(-62), today.AddDays(400));
        Check("tasks: only those with due date and title", tasks.Count == 2 && tasks.Any(t => t.Title == "報帳") && tasks.Any(t => t.Title == "已完成的待辦"), string.Join("|", tasks.Select(t => t.Title)));
        Check("tasks: kind, color, done flag", tasks.All(t => t.Kind == ItemKind.GoogleTask && t.Color == "#ffd166") && tasks.First(t => t.Title == "已完成的待辦").Done && !tasks.First(t => t.Title == "報帳").Done);
        Check("tasks: due date is the calendar date", tasks.First(t => t.Title == "報帳").Date == today.AddDays(2));

        // 10. API 錯誤
        ex = Expect(() => client.FetchEvents("forbidden@group.calendar.google.com", "#000000", ItemKind.Event, today, today));
        Check("403 gives readable error, not sign-in", ex != null && ex.Message.Contains("403") && !ex.NeedsSignIn, ex == null ? "no exception" : ex.Message);

        // ===== 新增行程：第一次才請求「寫入」授權 =====
        Check("write scope: not granted by the normal sign-in", !auth.HasWriteScope && secrets.Scopes.Contains("calendar.readonly") && !secrets.Scopes.Contains("calendar.events"), secrets.Scopes);
        var calsForWrite = client.ListCalendars(out email);
        Check("write scope: accessRole -> CanWrite (owner/writer = 1, reader = 0)",
            calsForWrite.First(c => c.Id == "me@example.com").CanWrite == 1 && calsForWrite.First(c => c.Id.StartsWith("team@")).CanWrite == 1 &&
            calsForWrite.First(c => c.Id.StartsWith("zh-tw.taiwan")).CanWrite == 0 && calsForWrite.First(c => c.Id.StartsWith("forbidden")).CanWrite == 0);

        Post(Fake + "/reset-scopes");
        ex = Expect(() => client.CreateEvent("me@example.com", "不該成功", false, today.AddHours(14), today.AddHours(15)));
        Check("write scope: creating without the scope says authorization is needed", ex != null && ex.NeedsWriteScope && !ex.NeedsSignIn, ex == null ? "no exception" : ex.Message);

        ex = Expect(() => client.UpdateEvent("me@example.com", "ev-visit", "不該成功", false, today.AddHours(9), today.AddHours(10)));
        Check("update: without the write scope says authorization is needed", ex != null && ex.NeedsWriteScope && !ex.NeedsSignIn, ex == null ? "no exception" : ex.Message);
        ex = Expect(() => client.DeleteEvent("me@example.com", "ev-visit"));
        Check("delete: without the write scope says authorization is needed", ex != null && ex.NeedsWriteScope && !ex.NeedsSignIn, ex == null ? "no exception" : ex.Message);

        // 抓回來的行程帶有 id；重複行程的某一次有 recurring 標記
        Check("fetch: events carry their id, recurring instances are flagged",
            visit.Id == "ev-visit" && !visit.Recurring && morning.Id == "ev-standup_20261027T013000Z" && morning.Recurring);

        string writeUrl = null;
        auth.OpenBrowser = url =>
        {
            writeUrl = url;
            var q = Query(url);
            Post(Fake + "/register?code=CODEW&challenge=" + Uri.EscapeDataString(q["code_challenge"]) + "&scope=" + Uri.EscapeDataString(q["scope"]) +
                 "&include=" + (q.ContainsKey("include_granted_scopes") ? q["include_granted_scopes"] : "false"));
            Task.Run(() => { Thread.Sleep(300); Get(q["redirect_uri"] + "/?code=CODEW&state=" + Uri.EscapeDataString(q["state"])); });
        };
        secrets.Email = "me@example.com";
        auth.AuthorizeWrite(TimeSpan.FromSeconds(30));
        var wq = Query(writeUrl);
        Check("write scope: auth url asks only for calendar.events, keeps earlier grants, hints the account",
            wq["scope"] == GoogleAuth.WriteScope && wq["include_granted_scopes"] == "true" && wq["login_hint"] == "me@example.com" && wq["access_type"] == "offline", writeUrl);
        Check("write scope: now granted, and the read scopes are still there", auth.HasWriteScope && secrets.Scopes.Contains("calendar.readonly") && secrets.Scopes.Contains("tasks.readonly"), secrets.Scopes);
        Check("write scope: persisted (encrypted) across restart", GoogleSecrets.Load().Scopes.Contains("calendar.events") && new GoogleAuth(GoogleSecrets.Load()).HasWriteScope);

        string newId = client.CreateEvent("me@example.com", "新增的會議", false, today.AddHours(14), today.AddHours(15));
        Check("create: returns the new event id", newId == "new-1", newId);
        var sent = Json.ParseObject(DownloadUtf8(Fake + "/last-created"));
        var sentBody = Json.Obj(sent, "body");
        string offsetText = TimeZoneInfo.Local.GetUtcOffset(today.AddHours(14)).ToString(@"hh\:mm");
        string sign = TimeZoneInfo.Local.GetUtcOffset(today.AddHours(14)) < TimeSpan.Zero ? "-" : "+";
        Check("create: summary, start/end with the local offset, default reminders",
            Json.Str(sentBody, "summary") == "新增的會議" &&
            Json.Str(Json.Obj(sentBody, "start"), "dateTime") == today.AddHours(14).ToString("yyyy-MM-dd'T'HH:mm:ss") + sign + offsetText &&
            Json.Str(Json.Obj(sentBody, "end"), "dateTime") == today.AddHours(15).ToString("yyyy-MM-dd'T'HH:mm:ss") + sign + offsetText &&
            Json.Bool(Json.Obj(sentBody, "reminders"), "useDefault") && Json.Str(sent, "calendarId") == "me@example.com", DownloadUtf8(Fake + "/last-created"));

        client.CreateEvent("team@group.calendar.google.com", "全天的事", true, today.AddDays(5).AddHours(14), today.AddDays(5).AddHours(15));
        var sentAll = Json.Obj(Json.ParseObject(DownloadUtf8(Fake + "/last-created")), "body");
        Check("create: all-day uses date and the next day as the (exclusive) end, no time fields",
            Json.Str(Json.Obj(sentAll, "start"), "date") == today.AddDays(5).ToString("yyyy-MM-dd") && Json.Str(Json.Obj(sentAll, "end"), "date") == today.AddDays(6).ToString("yyyy-MM-dd") &&
            Json.Str(Json.Obj(sentAll, "start"), "dateTime") == null);

        ex = Expect(() => client.CreateEvent("forbidden@group.calendar.google.com", "寫到唯讀日曆", false, today.AddHours(9), today.AddHours(10)));
        Check("create: read-only calendar gives a readable error (not a sign-in / scope problem)", ex != null && !ex.NeedsWriteScope && !ex.NeedsSignIn && ex.Message.Contains("編輯權限"), ex == null ? "no exception" : ex.Message);

        // 刪除行程
        client.DeleteEvent("me@example.com", "ev-standup_20261027T013000Z");
        var del1 = Json.ParseObject(DownloadUtf8(Fake + "/last-deleted"));
        Check("delete: sends DELETE for that calendar and that (instance) id", Json.Str(del1, "calendarId") == "me@example.com" && Json.Str(del1, "eventId") == "ev-standup_20261027T013000Z", DownloadUtf8(Fake + "/last-deleted"));
        client.DeleteEvent("team@group.calendar.google.com", "a/b c");
        var del2 = Json.ParseObject(DownloadUtf8(Fake + "/last-deleted"));
        Check("delete: calendar and event ids are URL-escaped", Json.Str(del2, "calendarId") == "team@group.calendar.google.com" && Json.Str(del2, "eventId") == "a/b c", DownloadUtf8(Fake + "/last-deleted"));
        client.DeleteEvent("me@example.com", "gone-1");
        Check("delete: already gone (410) counts as success", true);
        ex = Expect(() => client.DeleteEvent("forbidden@group.calendar.google.com", "x1"));
        Check("delete: read-only calendar gives a readable error", ex != null && !ex.NeedsWriteScope && !ex.NeedsSignIn && ex.Message.Contains("編輯權限"), ex == null ? "no exception" : ex.Message);

        // 修改行程（PATCH）
        client.UpdateEvent("me@example.com", "ev-visit", "改過的標題", false, today.AddHours(16), today.AddHours(17).AddMinutes(30));
        var pat = Json.ParseObject(DownloadUtf8(Fake + "/last-patched"));
        var patBody = Json.Obj(pat, "body");
        Check("update: PATCH to that calendar/event with summary and local-offset times, clearing the all-day field",
            Json.Str(pat, "calendarId") == "me@example.com" && Json.Str(pat, "eventId") == "ev-visit" && Json.Str(patBody, "summary") == "改過的標題" &&
            Json.Str(Json.Obj(patBody, "start"), "dateTime") == today.AddHours(16).ToString("yyyy-MM-dd'T'HH:mm:ss") + sign + offsetText &&
            Json.Str(Json.Obj(patBody, "end"), "dateTime") == today.AddHours(17).AddMinutes(30).ToString("yyyy-MM-dd'T'HH:mm:ss") + sign + offsetText &&
            Json.Obj(patBody, "start").ContainsKey("date") && Json.Str(Json.Obj(patBody, "start"), "date") == null && !patBody.ContainsKey("reminders"), DownloadUtf8(Fake + "/last-patched"));
        client.UpdateEvent("team@group.calendar.google.com", "ev-x", "改成全天", true, today.AddDays(3).AddHours(10), today.AddDays(3).AddHours(11));
        var patAll = Json.Obj(Json.ParseObject(DownloadUtf8(Fake + "/last-patched")), "body");
        Check("update: switching to all-day sends date (+1 day exclusive end) and clears dateTime",
            Json.Str(Json.Obj(patAll, "start"), "date") == today.AddDays(3).ToString("yyyy-MM-dd") && Json.Str(Json.Obj(patAll, "end"), "date") == today.AddDays(4).ToString("yyyy-MM-dd") &&
            Json.Obj(patAll, "start").ContainsKey("dateTime") && Json.Str(Json.Obj(patAll, "start"), "dateTime") == null);
        // 多日：整天行程的 end 日期是最後一天（含），送出去的結束日再加一天；有時間的行程結束時間可以在後幾天
        client.UpdateEvent("me@example.com", "ev-visit", "三天", true, today.AddDays(7), today.AddDays(9));
        var patMulti = Json.Obj(Json.ParseObject(DownloadUtf8(Fake + "/last-patched")), "body");
        Check("update: multi-day all-day sends start date and (last day + 1) as the exclusive end", Json.Str(Json.Obj(patMulti, "start"), "date") == today.AddDays(7).ToString("yyyy-MM-dd") && Json.Str(Json.Obj(patMulti, "end"), "date") == today.AddDays(10).ToString("yyyy-MM-dd"));
        client.CreateEvent("me@example.com", "跨夜", false, today.AddDays(2).AddHours(22), today.AddDays(3).AddHours(6));
        var crMulti = Json.Obj(Json.ParseObject(DownloadUtf8(Fake + "/last-created")), "body");
        Check("create: timed event can end on a later day", Json.Str(Json.Obj(crMulti, "start"), "dateTime") == today.AddDays(2).AddHours(22).ToString("yyyy-MM-dd'T'HH:mm:ss") + sign + offsetText &&
            Json.Str(Json.Obj(crMulti, "end"), "dateTime") == today.AddDays(3).AddHours(6).ToString("yyyy-MM-dd'T'HH:mm:ss") + sign + offsetText);
        client.CreateEvent("me@example.com", "出差三天", true, today.AddDays(4), today.AddDays(6));
        var crAll = Json.Obj(Json.ParseObject(DownloadUtf8(Fake + "/last-created")), "body");
        Check("create: multi-day all-day sends (last day + 1) as the exclusive end", Json.Str(Json.Obj(crAll, "start"), "date") == today.AddDays(4).ToString("yyyy-MM-dd") && Json.Str(Json.Obj(crAll, "end"), "date") == today.AddDays(7).ToString("yyyy-MM-dd"));
        ex = Expect(() => client.UpdateEvent("me@example.com", "gone-1", "x", false, today.AddHours(9), today.AddHours(10)));
        Check("update: event already gone gives a readable error", ex != null && !ex.NeedsWriteScope && ex.Message.Contains("找不到"), ex == null ? "no exception" : ex.Message);
        ex = Expect(() => client.UpdateEvent("forbidden@group.calendar.google.com", "x1", "x", false, today.AddHours(9), today.AddHours(10)));
        Check("update: read-only calendar gives a readable error", ex != null && !ex.NeedsWriteScope && ex.Message.Contains("編輯權限"), ex == null ? "no exception" : ex.Message);

        // 抓回來的行程帶有結束時間（編輯用）：有時間的是結束的時刻，整天的是最後一天的隔天
        Check("fetch: End is the local end time (timed) / exclusive end date (all-day)",
            visit.End.HasValue && visit.End.Value == expectVisit.AddHours(1) && visit.IsEditable &&
            holiday3[0].End == today.AddDays(6) && holiday3[0].IsEditable && !holiday3[1].IsEditable && !holiday3[2].IsEditable && holiday3[0].LastDay == today.AddDays(5) &&
            untitled.AllDay && untitled.End == today.AddDays(2) && untitled.IsEditable, visit.End + " / " + holiday3[0].End);

        // 設定檔：編輯權限欄位來回，舊格式（沒有這一欄）讀出來是「不知道」
        var cfgW = new GoogleConfig();
        cfgW.Calendars.Add(new GoogleCalendarEntry { Id = "a@b.c", Name = "可寫", CanWrite = 1 });
        cfgW.Calendars.Add(new GoogleCalendarEntry { Id = "d@e.f", Name = "唯讀", CanWrite = 0 });
        cfgW.Calendars.Add(new GoogleCalendarEntry { Id = "g@h.i", Name = "未知", CanWrite = -1 });
        cfgW.Save();
        var backW = GoogleConfig.Load();
        Check("config: CanWrite roundtrip (1 / 0 / unknown)", backW.Calendars.Select(c => c.CanWrite).SequenceEqual(new[] { 1, 0, -1 }));
        File.WriteAllLines(Path.Combine(DataPaths.Dir, "google-calendars.txt"), new[] { "old@x.y\t1\tE\t#112233\t舊格式" });
        Check("config: old 5-column file loads with unknown write access", GoogleConfig.Load().Calendars.Count == 1 && GoogleConfig.Load().Calendars[0].CanWrite == -1);

        // 11. 登入被撤銷 → 需要重新連接
        var revoked = new GoogleSecrets { ClientId = "test-client", ClientSecret = "test-secret", RefreshToken = "REVOKED" };
        ex = Expect(() => new GoogleAuth(revoked).AccessToken());
        Check("invalid_grant means sign in again", ex != null && ex.NeedsSignIn, ex == null ? "no exception" : ex.Message);

        var none = new GoogleSecrets { ClientId = "test-client", ClientSecret = "test-secret" };
        ex = Expect(() => new GoogleAuth(none).AccessToken());
        Check("no token means sign in", ex != null && ex.NeedsSignIn);

        // 12. 登出會清掉憑證
        auth.SignOut();
        Check("sign out clears token on disk", GoogleSecrets.Load().RefreshToken == "" && secrets.RefreshToken == "");

        // 13. 設定檔來回
        var cfg = new GoogleConfig { ShowTasks = true, TasksColor = "#abcdef", IntervalMinutes = 60 };
        cfg.Calendars.Add(new GoogleCalendarEntry { Id = "a@b.c", Name = "中文 名稱", Color = "#112233", Kind = ItemKind.Holiday, Enabled = false });
        cfg.Save();
        var back = GoogleConfig.Load();
        Check("google config roundtrip", back.ShowTasks && back.TasksColor == "#abcdef" && back.IntervalMinutes == 60 && back.Calendars.Count == 1 &&
            back.Calendars[0].Name == "中文 名稱" && back.Calendars[0].Kind == ItemKind.Holiday && !back.Calendars[0].Enabled && back.Calendars[0].Color == "#112233");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

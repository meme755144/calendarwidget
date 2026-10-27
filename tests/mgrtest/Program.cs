using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CalendarWidget;

static class P
{
    static int fails;
    static string logFile;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail)); if (!ok) fails++; }

    static void Pump(Func<bool> cond, int timeoutMs = 15000)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        var sw = Stopwatch.StartNew();
        timer.Tick += (s, e) => { if (cond() || sw.ElapsedMilliseconds > timeoutMs) { timer.Stop(); frame.Continue = false; } };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void Settle(int ms = 500) { Pump(() => false, ms); }

    static int Requests(string contains)
    {
        return File.ReadAllLines(logFile).Count(l => l.Contains(contains));
    }

    static void HttpPost(string url)
    {
        var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
        req.Method = "POST";
        req.ContentLength = 0;
        using (req.GetResponse()) { }
    }

    static void HttpGetQuiet(string url)
    {
        try { using (System.Net.WebRequest.Create(url).GetResponse()) { } }
        catch (System.Net.WebException) { }
    }

    static Dictionary<string, string> QueryOf(string url)
    {
        var d = new Dictionary<string, string>();
        foreach (string pair in new Uri(url).Query.TrimStart('?').Split('&'))
        {
            int eq = pair.IndexOf('=');
            d[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
        }
        return d;
    }

    static void Seed(string refresh)
    {
        var s = new GoogleSecrets { ClientId = "test-client", ClientSecret = "test-secret", RefreshToken = refresh, Email = "me@example.com" };
        s.Save();
    }

    static int Main(string[] args)
    {
        string dir = Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA");
        logFile = args[0];
        if (string.IsNullOrEmpty(dir)) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        var ui = Dispatcher.CurrentDispatcher;
        DateTime today = DateTime.Today;

        // ===== A. 已登入、第一次載入日曆清單，並自動抓取 =====
        Seed("RT-1");
        var store = new ItemStore();
        var mgr = new GoogleManager(store, ui);
        int calendarsChanged = 0, changed = 0;
        mgr.CalendarsChanged += () => calendarsChanged++;
        mgr.Changed += () => changed++;
        mgr.Start();
        Check("A0 start with no calendars/no cache does nothing visible", store.For(today).Count == 0);

        mgr.ReloadCalendars();
        Pump(() => store.For(today).Any(i => i.Title == "晨會") && store.For(today.AddDays(1)).Any(i => i.Title == "光復節"));

        var cfg = mgr.Config;
        Check("A1 calendar list merged (4 calendars, 2 enabled)", cfg.Calendars.Count == 4 && cfg.Calendars.Count(c => c.Enabled) == 2, cfg.Calendars.Count + "/" + cfg.Calendars.Count(c => c.Enabled));
        Check("A2 CalendarsChanged raised", calendarsChanged >= 1);
        Check("A3 primary events shown", store.For(today).Any(i => i.Title == "軟體園區主任來訪" && i.TimeText != null));
        var holidayItem = store.For(today.AddDays(1)).First(i => i.Title == "光復節");
        Check("A4 holiday calendar items are Holiday kind with google color", holidayItem.Kind == ItemKind.Holiday && holidayItem.Color == "#16a765", holidayItem.Kind + " " + holidayItem.Color);
        Check("A5 disabled calendar not fetched", Requests("team%40group") == 0 && !store.For(today.AddDays(4)).Any(i => i.Title == "團隊站會"));
        Check("A6 status shows connected + count", mgr.StatusText().Contains("已連接：me@example.com") && mgr.StatusText().Contains("上次更新"), mgr.StatusText());
        Check("A7 config persisted", GoogleConfig.Load().Calendars.Count == 4);
        string cacheDir = Path.Combine(DataPaths.LocalDir, "cache");
        Check("A8 cache files written", Directory.Exists(cacheDir) && Directory.GetFiles(cacheDir, "google-*.tsv").Length == 2 && File.Exists(Path.Combine(cacheDir, "google.meta")), Directory.Exists(cacheDir) ? string.Join(",", Directory.GetFiles(cacheDir).Select(Path.GetFileName)) : "no dir");

        // ===== B. 套用設定：啟用團隊日曆 + 顯示待辦；停用節日日曆 =====
        var entries = mgr.Config.Calendars;
        entries.First(c => c.Id.StartsWith("team@")).Enabled = true;
        entries.First(c => c.Id.StartsWith("zh-tw.taiwan")).Enabled = false;
        mgr.ApplyConfig(entries, true, "#ffd166", 30);
        Check("B1 disabled calendar removed immediately", !store.For(today.AddDays(1)).Any(i => i.Title == "光復節"));
        Pump(() => store.For(today.AddDays(4)).Any(i => i.Title == "團隊站會") && store.For(today.AddDays(2)).Any(i => i.Title == "報帳"));
        Check("B2 newly enabled calendar fetched", store.For(today.AddDays(4)).Any(i => i.Title == "團隊站會"));
        var task = store.For(today.AddDays(2)).First(i => i.Title == "報帳");
        Check("B3 tasks shown with kind/color", task.Kind == ItemKind.GoogleTask && task.Color == "#ffd166");
        Check("B4 completed task marked done", store.For(today).Any(i => i.Title == "已完成的待辦" && i.Done));

        // 只改顏色與類型：不應該再打網路，記憶體裡的項目直接換色
        Settle(300);
        int eventCallsBefore = Requests("/events");
        entries = mgr.Config.Calendars;
        entries.First(c => c.Id == "me@example.com").Color = "#abcdef";
        entries.First(c => c.Id.StartsWith("team@")).Kind = ItemKind.Holiday;
        mgr.ApplyConfig(entries, true, "#00ff00", 30);
        Settle(800);
        Check("B5 recolor without network", Requests("/events") == eventCallsBefore, eventCallsBefore + " -> " + Requests("/events"));
        Check("B6 recolored items", store.For(today).First(i => i.Title == "晨會").Color == "#abcdef" && store.For(today.AddDays(4)).First(i => i.Title == "團隊站會").Kind == ItemKind.Holiday
            && store.For(today.AddDays(2)).First(i => i.Title == "報帳").Color == "#00ff00");

        // ===== C. 重新啟動：先顯示快取，再背景更新 =====
        var store2 = new ItemStore();
        var mgr2 = new GoogleManager(store2, ui);
        mgr2.Start();
        Check("C1 cache shown immediately on start (before any network)", store2.For(today).Any(i => i.Title == "晨會") && store2.For(today.AddDays(2)).Any(i => i.Title == "報帳"));
        var cachedVisit = store2.For(today).FirstOrDefault(i => i.Title == "軟體園區主任來訪");
        var cachedMorning = store2.For(today).FirstOrDefault(i => i.Title == "晨會");
        Check("C1b cache keeps start time and reminders", cachedVisit != null && cachedVisit.Start.HasValue && cachedVisit.Reminders != null && cachedVisit.Reminders.SequenceEqual(new[] { 10 }) &&
            cachedMorning != null && cachedMorning.Reminders.SequenceEqual(new[] { 5, 1440 }));
        var cachedAllDay = store2.For(today.AddDays(3)).FirstOrDefault(i => i.Title == "連休三天");
        Check("C1c cache keeps all-day flag", cachedAllDay != null && cachedAllDay.AllDay && cachedAllDay.Start == today.AddDays(3) && cachedAllDay.Reminders.SequenceEqual(new[] { 900 }));
        Check("C2 cached items keep latest colors", store2.For(today).First(i => i.Title == "晨會").Color == "#abcdef" && store2.For(today.AddDays(4)).First(i => i.Title == "團隊站會").Kind == ItemKind.Holiday);
        int eventCallsC = Requests("/events");
        Settle(800);
        Check("C3 fresh cache is not refetched right away", Requests("/events") == eventCallsC, eventCallsC + " -> " + Requests("/events"));

        // 手動更新
        mgr2.RefreshNow();
        Pump(() => Requests("/events") > eventCallsC);
        Settle(500);
        Check("C4 manual refresh refetches", Requests("/events") > eventCallsC);

        // ===== D. 中斷連接 =====
        int revokeBefore = Requests("/revoke");
        mgr.Disconnect();
        Check("D1 items removed", store.For(today).All(i => i.Kind != ItemKind.Event && i.Kind != ItemKind.GoogleTask) && store.For(today.AddDays(2)).Count == 0);
        Check("D2 token cleared on disk", GoogleSecrets.Load().RefreshToken == "" && !mgr.IsSignedIn);
        Check("D3 client credentials kept for reconnecting", GoogleSecrets.Load().ClientId == "test-client");
        Check("D4 calendar selections cleared, caches deleted", GoogleConfig.Load().Calendars.Count == 0 && Directory.GetFiles(cacheDir, "google-*.tsv").Length == 0);
        Check("D5 status says not connected", mgr.StatusText() == "尚未連接", mgr.StatusText());
        Settle(500);
        Check("D6 revoke requested exactly once", Requests("/revoke") == revokeBefore + 1, revokeBefore + " -> " + Requests("/revoke"));

        // ===== E. 登入失效 =====
        Seed("REVOKED");
        var store3 = new ItemStore();
        var mgr3 = new GoogleManager(store3, ui);
        var cfg3 = new GoogleConfig();
        cfg3.Calendars.Add(new GoogleCalendarEntry { Id = "me@example.com", Name = "me", Enabled = true });
        cfg3.Save();
        var mgr3b = new GoogleManager(store3, ui);
        mgr3b.Start();
        Pump(() => mgr3b.NeedsSignIn);
        Check("E1 revoked token detected", mgr3b.NeedsSignIn && mgr3b.StatusText().Contains("登入已失效"), mgr3b.StatusText());
        int tokenCalls = Requests("/token");
        mgr3b.RefreshDue();
        Settle(800);
        Check("E2 no retry storm while sign-in needed", Requests("/token") == tokenCalls, tokenCalls + " -> " + Requests("/token"));

        // ===== F. 舊版快取（沒有提醒資料）：視為過期，馬上重新抓一次 =====
        Directory.Delete(DataPaths.Dir, true);
        Directory.CreateDirectory(DataPaths.Dir);
        Seed("RT-1");
        var cfgF = new GoogleConfig();
        cfgF.Calendars.Add(new GoogleCalendarEntry { Id = "me@example.com", Name = "me", Enabled = true });
        cfgF.Save();
        string cacheDirF = Path.Combine(DataPaths.LocalDir, "cache");
        Directory.CreateDirectory(cacheDirF);
        string hashF;
        using (var sha = System.Security.Cryptography.SHA1.Create())
            hashF = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("cal:me@example.com"))).Replace("-", "").ToLowerInvariant();
        File.WriteAllLines(Path.Combine(cacheDirF, "google-" + hashF + ".tsv"), new[]
        {
            today.ToString("yyyy-MM-dd") + "\t09:30\t1\t#6CB4FF\t0\t舊格式的行程"   // 6 欄：舊版寫的
        });
        File.WriteAllText(Path.Combine(cacheDirF, "google.meta"), DateTime.Now.Ticks.ToString());   // 看起來剛更新過
        int eventCallsF = Requests("/events");
        var storeF = new ItemStore();
        var mgrF = new GoogleManager(storeF, ui);
        mgrF.Start();
        Check("F1 old-format cache is not shown", !storeF.For(today).Any(i => i.Title == "舊格式的行程"));
        Pump(() => storeF.For(today).Any(i => i.Title == "晨會"));
        Check("F2 it refetches right away even though the cache looked fresh", Requests("/events") > eventCallsF && storeF.For(today).Any(i => i.Title == "晨會" && i.Reminders != null));

        // ===== G. 新增行程（第一次才請求寫入授權）=====
        Directory.Delete(DataPaths.Dir, true);
        Directory.CreateDirectory(DataPaths.Dir);
        HttpPost("http://127.0.0.1:18766/reset-scopes");
        Seed("RT-1");   // 只有讀取授權
        var cfgG = new GoogleConfig();
        cfgG.Calendars.Add(new GoogleCalendarEntry { Id = "me@example.com", Name = "me", Enabled = true, CanWrite = 1 });
        cfgG.Calendars.Add(new GoogleCalendarEntry { Id = "team@group.calendar.google.com", Name = "我的團隊", Enabled = true, CanWrite = 1 });
        cfgG.Calendars.Add(new GoogleCalendarEntry { Id = "zh-tw.taiwan#holiday@group.v.calendar.google.com", Name = "台灣的節日", Enabled = true, CanWrite = 0, Kind = ItemKind.Holiday });
        cfgG.Calendars.Add(new GoogleCalendarEntry { Id = "hidden@group.calendar.google.com", Name = "沒顯示", Enabled = false, CanWrite = 1 });
        cfgG.Calendars.Add(new GoogleCalendarEntry { Id = "unknown@group.calendar.google.com", Name = "還不知道", Enabled = true, CanWrite = -1 });
        cfgG.Save();

        var storeG = new ItemStore();
        var mgrG = new GoogleManager(storeG, ui);
        mgrG.BrowserOpener = url =>
        {
            var q = QueryOf(url);
            HttpPost("http://127.0.0.1:18766/register?code=CODEG&challenge=" + Uri.EscapeDataString(q["code_challenge"]) + "&scope=" + Uri.EscapeDataString(q["scope"]) +
                     "&include=" + (q.ContainsKey("include_granted_scopes") ? q["include_granted_scopes"] : "false"));
            System.Threading.Tasks.Task.Run(() => { System.Threading.Thread.Sleep(300); HttpGetQuiet(q["redirect_uri"] + "/?code=CODEG&state=" + Uri.EscapeDataString(q["state"])); });
        };
        // 這裡不呼叫 Start()，免得背景的更新和日曆清單補抓干擾（Start 的補抓行為在後面單獨測）
        var writable = mgrG.WritableCalendars().Select(c => c.Id).ToList();
        Check("G1 writable calendars: shown + not read-only (unknown stays selectable), hidden and read-only excluded",
            writable.Count == 3 && writable.Contains("me@example.com") && writable.Contains("team@group.calendar.google.com") && writable.Contains("unknown@group.calendar.google.com"), string.Join(",", writable));
        Check("G2 no write scope yet", !mgrG.HasWriteScope);

        var progressG = new List<string>();
        bool doneG = false; string errorG = "(not called)";
        int getsBefore = Requests("GET /calendar/v3/calendars");
        mgrG.AddEvent("me@example.com", "手動新增的行程", false, today.AddHours(15), today.AddHours(16), m => progressG.Add(m), e => { errorG = e; doneG = true; });
        Check("G3 busy flag is set while adding", mgrG.Adding);
        Pump(() => doneG);
        Check("G4 first add asks for the extra authorization, then succeeds", doneG && errorG == null && progressG.Count == 1 && progressG[0].Contains("授權") && mgrG.HasWriteScope, errorG + " / " + progressG.Count);
        Check("G5 the event was really sent to the right calendar", Requests("POST /calendar/v3/calendars/me%40example.com/events") >= 1);
        Check("G6 busy flag cleared afterwards", !mgrG.Adding);
        Pump(() => Requests("GET /calendar/v3/calendars") > getsBefore);
        Check("G7 after adding, it refetches so the new event shows up", Requests("GET /calendar/v3/calendars") > getsBefore);

        progressG.Clear();
        doneG = false; errorG = "(not called)";
        int postsBefore = Requests("POST /calendar/v3/calendars/team");
        mgrG.AddEvent("team@group.calendar.google.com", "第二次新增", true, today.AddDays(2), today.AddDays(2), m => progressG.Add(m), e => { errorG = e; doneG = true; });
        Pump(() => doneG);
        Check("G8 second add needs no authorization again", doneG && errorG == null && progressG.Count == 0 && Requests("POST /calendar/v3/calendars/team") == postsBefore + 1, errorG + " / " + progressG.Count);

        doneG = false; errorG = "(not called)";
        mgrG.AddEvent("forbidden@group.calendar.google.com", "寫到唯讀", false, today.AddHours(9), today.AddHours(10), null, e => { errorG = e; doneG = true; });
        Pump(() => doneG);
        Check("G9 adding to a calendar without edit access reports a readable error", doneG && errorG != null && errorG.Contains("編輯權限"), errorG);
        Check("G10 and the manager is usable again afterwards", !mgrG.Adding);

        // ===== 刪除 Google 行程 =====
        Pump(() => storeG.For(today).Any(i => i.Title == "軟體園區主任來訪"));
        var visitG = storeG.For(today).First(i => i.Title == "軟體園區主任來訪");
        Check("G13 fetched event has its id and belongs to a writable calendar", visitG.Id == "ev-visit" && mgrG.WritableCalendarIdOf(visitG) == "me@example.com", visitG.Id + " / " + mgrG.WritableCalendarIdOf(visitG));
        var noIdItem = new CalendarItem { CalendarId = visitG.CalendarId, Kind = ItemKind.Event, Title = "x" };
        var todoItem = new CalendarItem { Id = "t1", CalendarId = CalendarIds.Local, Kind = ItemKind.LocalTodo, Title = "t" };
        var holidayItemG = new CalendarItem { Id = "h1", CalendarId = CalendarIds.Google("zh-tw.taiwan#holiday@group.v.calendar.google.com"), Kind = ItemKind.Holiday, Title = "h" };
        var icsItem = new CalendarItem { Id = "i1", CalendarId = "ics:abc", Kind = ItemKind.Event, Title = "i" };
        Check("G14 not deletable: no id, local todo, read-only calendar, ICS", mgrG.WritableCalendarIdOf(noIdItem) == null && mgrG.WritableCalendarIdOf(todoItem) == null &&
            mgrG.WritableCalendarIdOf(holidayItemG) == null && mgrG.WritableCalendarIdOf(icsItem) == null);

        progressG.Clear();
        doneG = false; errorG = "(not called)";
        int getsBeforeDel = Requests("GET /calendar/v3/calendars");
        mgrG.DeleteEvent("me@example.com", "ev-visit", m => progressG.Add(m), e => { errorG = e; doneG = true; });
        Check("G15 busy flag is set while deleting", mgrG.Adding);
        Pump(() => doneG);
        Check("G16 delete succeeds without asking for authorization again", doneG && errorG == null && progressG.Count == 0, errorG + " / " + progressG.Count);
        Check("G17 the DELETE went to the right calendar and event", Requests("DELETE /calendar/v3/calendars/me%40example.com/events/ev-visit") == 1);
        Pump(() => Requests("GET /calendar/v3/calendars") > getsBeforeDel);
        Check("G18 after deleting, it refetches", Requests("GET /calendar/v3/calendars") > getsBeforeDel && !mgrG.Adding);

        doneG = false; errorG = "(not called)";
        mgrG.DeleteEvent("forbidden@group.calendar.google.com", "x1", null, e => { errorG = e; doneG = true; });
        Pump(() => doneG);
        Check("G19 deleting on a read-only calendar reports a readable error", doneG && errorG != null && errorG.Contains("編輯權限"), errorG);

        // 修改 Google 行程
        progressG.Clear();
        doneG = false; errorG = "(not called)";
        int getsBeforeUpd = Requests("GET /calendar/v3/calendars");
        mgrG.UpdateEvent("me@example.com", "ev-visit", "改過的會議", false, today.AddHours(16), today.AddHours(17), m => progressG.Add(m), e => { errorG = e; doneG = true; });
        Check("G15b busy flag is set while updating", mgrG.Adding);
        Pump(() => doneG);
        Check("G15c update succeeds, no extra authorization, PATCH sent to the right event", doneG && errorG == null && progressG.Count == 0 &&
            Requests("PATCH /calendar/v3/calendars/me%40example.com/events/ev-visit") == 1, errorG + " / " + progressG.Count);
        Pump(() => Requests("GET /calendar/v3/calendars") > getsBeforeUpd);
        Check("G15d after updating, it refetches", Requests("GET /calendar/v3/calendars") > getsBeforeUpd && !mgrG.Adding);

        // 刪除要先有寫入授權：只有讀取授權的帳號，第一次刪除也會請你授權
        HttpPost("http://127.0.0.1:18766/reset-scopes");
        Seed("RT-1");
        var mgrD = new GoogleManager(new ItemStore(), ui);
        mgrD.BrowserOpener = url =>
        {
            var q = QueryOf(url);
            HttpPost("http://127.0.0.1:18766/register?code=CODED&challenge=" + Uri.EscapeDataString(q["code_challenge"]) + "&scope=" + Uri.EscapeDataString(q["scope"]) +
                     "&include=" + (q.ContainsKey("include_granted_scopes") ? q["include_granted_scopes"] : "false"));
            System.Threading.Tasks.Task.Run(() => { System.Threading.Thread.Sleep(300); HttpGetQuiet(q["redirect_uri"] + "/?code=CODED&state=" + Uri.EscapeDataString(q["state"])); });
        };
        var progressD = new List<string>();
        doneG = false; errorG = "(not called)";
        mgrD.DeleteEvent("me@example.com", "ev-standup_20261027T013000Z", m => progressD.Add(m), e => { errorG = e; doneG = true; });
        Pump(() => doneG);
        Check("G20 first delete asks for the extra authorization, then succeeds", doneG && errorG == null && progressD.Count == 1 && progressD[0].Contains("授權") && mgrD.HasWriteScope, errorG + " / " + progressD.Count);

        // 快取要保留行程 id 與重複行程標記（重開程式後還是可以刪）
        var storeC = new ItemStore();
        var mgrC = new GoogleManager(storeC, ui);
        mgrC.Start();
        var cachedStandup = storeC.For(today).FirstOrDefault(i => i.Title == "晨會");
        Check("G21 cache keeps event id and recurring flag", cachedStandup != null && cachedStandup.Id == "ev-standup_20261027T013000Z" && cachedStandup.Recurring && cachedStandup.End.HasValue && cachedStandup.End.Value > cachedStandup.Start.Value, cachedStandup == null ? "no item" : cachedStandup.Id + " / " + cachedStandup.Recurring);

        // ===== 共用的「帳號」介面（AccountRegistry / ICalendarAccount）=====
        var registry = new AccountRegistry();
        registry.Add(mgrG);
        int registryChanged = 0;
        registry.Changed += () => registryChanged++;
        var targets = registry.WritableTargets(new List<string>());
        Check("H1 registry lists the signed-in account's writable calendars with store ids and labels",
            targets.Count == 3 && targets[0].Calendar.StoreId == "g:me@example.com" && targets[0].Label == "Google｜me" && targets.Any(x => x.Label == "Google｜我的團隊") && targets.All(x => x.Account.Name == "Google"),
            string.Join(",", targets.Select(x => x.Label)));
        var ordered = registry.WritableTargets(new List<string> { "g:team@group.calendar.google.com" });
        Check("H2 registry orders targets by the calendar order setting, the rest keep their order", ordered[0].Calendar.RemoteId == "team@group.calendar.google.com" && ordered[1].Calendar.RemoteId == "me@example.com");
        var standupG = storeG.For(today).FirstOrDefault(i => i.Title == "晨會");
        var resolved1 = standupG == null ? null : registry.Resolve(standupG);
        Check("H3 resolve finds the account and calendar of a Google event", resolved1 != null && resolved1.Account == mgrG && resolved1.Calendar.RemoteId == "me@example.com" && resolved1.Calendar.Name == "me");
        Check("H4 resolve gives null for items that are not editable online events (ICS, local, holiday calendar, no id)",
            registry.Resolve(icsItem) == null && registry.Resolve(todoItem) == null && registry.Resolve(holidayItemG) == null && registry.Resolve(noIdItem) == null && registry.Resolve(null) == null);
        var iface = (ICalendarAccount)mgrG;
        Check("H5 account facts: name, signed in, write access, auth notice differs for add and edit, default reminder note",
            iface.Name == "Google" && iface.IsSignedIn && iface.HasWriteAccess && iface.WriteAuthNotice(false).Contains("新增") && iface.WriteAuthNotice(true).Contains("修改") && iface.DefaultReminderNote.Contains("預設提醒"));

        int postsH = Requests("POST /calendar/v3/calendars/me%40example.com/events");
        doneG = false; errorG = "(not called)";
        iface.AddEvent("me@example.com", new EventDraft { Title = "經由介面新增", AllDay = false, Start = today.AddHours(18), End = today.AddHours(19) }, null, e => { errorG = e; doneG = true; });
        Pump(() => doneG);
        Check("H6 adding through the interface reaches the server and the registry reports the change", doneG && errorG == null && Requests("POST /calendar/v3/calendars/me%40example.com/events") == postsH + 1, errorG);
        Pump(() => registryChanged > 0);
        Check("H7 the registry forwards the account's Changed event", registryChanged > 0);

        doneG = false; errorG = "(not called)";
        iface.UpdateEvent("me@example.com", "ev-x1", new EventDraft { Title = "改", AllDay = true, Start = today.AddDays(1), End = today.AddDays(2) }, null, e => { errorG = e; doneG = true; });
        Pump(() => doneG);
        Check("H8 updating through the interface sends a PATCH", doneG && errorG == null && Requests("PATCH /calendar/v3/calendars/me%40example.com/events/ev-x1") == 1, errorG);
        doneG = false; errorG = "(not called)";
        iface.DeleteEvent("me@example.com", "ev-x2", null, e => { errorG = e; doneG = true; });
        Pump(() => doneG);
        Check("H9 deleting through the interface sends a DELETE", doneG && errorG == null && Requests("DELETE /calendar/v3/calendars/me%40example.com/events/ev-x2") == 1, errorG);

        // 沒有登入的帳號不會出現在可選的日曆裡
        var signedOut = new AccountRegistry();
        Directory.Delete(DataPaths.Dir, true);
        Directory.CreateDirectory(DataPaths.Dir);
        var mgrOut = new GoogleManager(new ItemStore(), ui);
        signedOut.Add(mgrOut);
        Check("H10 a signed-out account contributes no targets and cannot resolve items", signedOut.WritableTargets(null).Count == 0 && signedOut.Resolve(standupG) == null && !((ICalendarAccount)mgrOut).IsSignedIn);

        // 舊版存的清單沒有「編輯權限」：啟動時背景默默補上
        Directory.Delete(DataPaths.Dir, true);
        Directory.CreateDirectory(DataPaths.Dir);
        Seed("RT-1");
        var cfgQ = new GoogleConfig();
        cfgQ.Calendars.Add(new GoogleCalendarEntry { Id = "me@example.com", Name = "me", Enabled = true, CanWrite = -1 });
        cfgQ.Calendars.Add(new GoogleCalendarEntry { Id = "zh-tw.taiwan#holiday@group.v.calendar.google.com", Name = "台灣的節日", Enabled = true, CanWrite = -1, Kind = ItemKind.Holiday });
        cfgQ.Save();
        var mgrQ = new GoogleManager(new ItemStore(), ui);
        mgrQ.Start();
        Pump(() => mgrQ.Config.Calendars.All(c => c.CanWrite >= 0));
        var resolved = mgrQ.Config.Calendars.ToDictionary(c => c.Id);
        Check("G11 start fills in unknown write access quietly (owner = 1, reader = 0)", resolved["me@example.com"].CanWrite == 1 && resolved["zh-tw.taiwan#holiday@group.v.calendar.google.com"].CanWrite == 0);
        Check("G12 and it kept the user's own choices (name/kind) while doing so", resolved["zh-tw.taiwan#holiday@group.v.calendar.google.com"].Kind == ItemKind.Holiday);

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

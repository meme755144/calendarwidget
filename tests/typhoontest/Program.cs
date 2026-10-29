using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Threading;
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
    const string Fake = "http://127.0.0.1:18768";
    static int fails;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail)); if (!ok) fails++; }

    static void Pump(Func<bool> cond, int timeoutMs = 15000)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        var sw = Stopwatch.StartNew();
        timer.Tick += (s, e) => { if (cond() || sw.ElapsedMilliseconds > timeoutMs) { timer.Stop(); frame.Continue = false; } };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void Wait(int ms) { var sw = Stopwatch.StartNew(); Pump(() => sw.ElapsedMilliseconds >= ms, ms + 1000); }

    static string Http(string path) { using (var c = new WebClient()) return c.DownloadString(Fake + path); }
    static void Mode(string name, int agoHours = 0, int bump = 0, string raw = null, int status = 200)
    {
        string q = "/_mode?name=" + name + "&ago=" + agoHours + "&bump=" + bump + "&status=" + status;
        if (raw != null) q += "&raw=" + Uri.EscapeDataString(raw);
        Http(q);
    }
    static Dictionary<string, object> Hits() { return Json.ParseObject(Http("/_hits")); }
    static int HitCount() { return Convert.ToInt32(Hits()["total"]); }

    static TyphoonData Parse(string json) { string p; var d = TyphoonData.Parse(json, out p); if (d == null) throw new Exception(p); return d; }

    static TyphoonItem Item(int offset, string scope = "", string kind = "city", bool work = true, bool school = true, DateTime? baseDay = null)
    {
        DateTime d = (baseDay ?? new DateTime(2026, 10, 29)).AddDays(offset);
        return new TyphoonItem { Date = d, Scope = scope, ScopeKind = kind, Work = work, School = school, Text = d.Month + "/" + d.Day + "停止上班、停止上課。" };
    }

    static TyphoonData Data(params KeyValuePair<string, TyphoonItem>[] items)
    {
        var data = new TyphoonData { State = "announced", CheckedAtUtc = DateTime.UtcNow };
        foreach (var kv in items)
        {
            List<TyphoonItem> list;
            if (!data.Cities.TryGetValue(kv.Key, out list)) data.Cities[kv.Key] = list = new List<TyphoonItem>();
            list.Add(kv.Value);
        }
        return data;
    }

    static KeyValuePair<string, TyphoonItem> At(string city, TyphoonItem item) { return new KeyValuePair<string, TyphoonItem>(city, item); }

    static TyphoonWatch Watch(string text) { return TyphoonWatch.Parse(text); }

    static int Main(string[] args)
    {
        string dir = Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA");
        if (string.IsNullOrEmpty(dir) || args.Length < 1) { Console.WriteLine("need CALENDARWIDGET_DATA and the tests\\data folder as the first argument"); return 2; }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string sampleText = File.ReadAllText(Path.Combine(args[0], "typhoon-latest-sample.json"), Encoding.UTF8);
        var today0 = new DateTime(2026, 10, 29);

        // ===== 解析：爬蟲實際輸出的樣本 =====
        var sample = Parse(sampleText);
        Check("parse: schema, state, page date and checkedAt", sample.Schema == 2 && sample.State == "announced" && sample.PageDate == today0 && sample.CheckedAtUtc == new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc));
        Check("parse: cities come with their items (city and area level)", sample.Cities.Count == 3 && sample.Cities["高雄市"].Count == 2 && sample.Cities["臺北市"][0].Date == new DateTime(2026, 10, 30));
        var kh = sample.Cities["高雄市"];
        Check("parse: area item keeps scope and kind; half-day item keeps period and start time",
            kh[0].ScopeKind == "area" && kh[0].Scope == "那瑪夏區" && kh[1].ScopeKind == "city" && kh[1].Period == "afternoon" && kh[1].From == "13:00" && kh[1].Work && kh[1].School);
        Check("parse: an unknown sentence becomes an Unknown item with its text", sample.Cities["花蓮縣"].Count == 1 && sample.Cities["花蓮縣"][0].Unknown && sample.Cities["花蓮縣"][0].Text.Contains("全縣停班停課"));
        Check("parse: schools are listed separately and not mixed into cities", sample.Schools["高雄市"].Count == 1 && sample.Schools["高雄市"][0].ScopeKind == "school" && !sample.Cities["高雄市"].Any(i => i.ScopeKind == "school"));
        Check("parse: no error block means Error is null", sample.Error == null);

        string problem;
        Check("parse: garbage / array / missing state / bad cities are rejected with a reason",
            TyphoonData.Parse("<html>", out problem) == null && problem != null &&
            TyphoonData.Parse("[1,2]", out problem) == null &&
            TyphoonData.Parse("{\"cities\":{}}", out problem) == null && problem.Contains("state") &&
            TyphoonData.Parse("{\"state\":\"none\",\"cities\":[1]}", out problem) == null && problem.Contains("cities"));
        var v1 = Parse("{\"schema\":1,\"state\":\"none\",\"cities\":{},\"schools\":{\"高雄市\":3},\"checkedAt\":\"2026-10-29T12:00:00.000Z\"}");
        Check("parse: the old format (schools as a number) still loads, with no school items", v1.Schema == 1 && v1.Schools.Count == 0 && v1.State == "none");
        var odd = Parse("{\"state\":\"announced\",\"cities\":{\"高雄市\":[{\"scope\":\"x\",\"work\":true},{\"date\":\"2026-10-30\",\"work\":true,\"school\":false},\"junk\",5],\"臺北市\":\"nope\"}}");
        Check("parse: items without a date, non-objects and non-list groups are skipped", odd.Cities.Count == 1 && odd.Cities["高雄市"].Count == 1 && odd.Cities["高雄市"][0].Date == new DateTime(2026, 10, 30) && !odd.Cities["高雄市"][0].School);
        var withError = Parse("{\"state\":\"none\",\"cities\":{},\"error\":{\"status\":403,\"message\":\"被擋\",\"backoffUntil\":\"2026-10-29T14:30:00.000Z\"}}");
        Check("parse: the error block is read", withError.Error != null && withError.Error.Status == 403 && withError.Error.Message == "被擋" && withError.Error.BackoffUntil.HasValue);
        string heartbeat = sampleText.Replace("2026-10-29T12:00:00.000Z", "2026-10-29T18:00:00.000Z");
        Check("signature: a new checkedAt alone does not change it", Parse(sampleText).Signature() == Parse(heartbeat).Signature());
        Check("signature: a different announcement does", Parse(sampleText).Signature() != Parse(sampleText.Replace("\"scope\": \"那瑪夏區\"", "\"scope\": \"桃源區\"")).Signature());
        Check("signature: schools do not matter", Parse(sampleText).Signature() == Parse(sampleText.Replace("高雄市某某國民中學", "別的學校")).Signature());

        // ===== 關注的縣市與鄉鎮區 =====
        var w = Watch("高雄市:前鎮區、小港區|台北市:|新北市:三重區 板橋區|火星市:x");
        Check("watch: cities keep their order, 台 equals 臺, unknown cities are dropped", w.Cities.SequenceEqual(new[] { "高雄市", "臺北市", "新北市" }));
        Check("watch: areas are split on 、 and spaces; empty means none", w.AreasOf("高雄市").SequenceEqual(new[] { "前鎮區", "小港區" }) && w.AreasOf("臺北市").Count == 0 && w.AreasOf("新北市").SequenceEqual(new[] { "三重區", "板橋區" }));
        Check("watch: Contains ignores 台/臺", w.Contains("台北市") && w.Contains("臺北市") && !w.Contains("臺中市"));
        Check("watch: serialize then parse gives the same thing", Watch(w.Serialize()).Serialize() == w.Serialize() && w.Serialize() == "高雄市:前鎮區、小港區|臺北市:|新北市:三重區、板橋區", w.Serialize());
        Check("watch: split handles commas, semicolons, full-width, duplicates", TyphoonWatch.SplitAreas("前鎮區,小港區；前鎮區，台中 中區\n").SequenceEqual(new[] { "前鎮區", "小港區", "臺中", "中區" }));
        Check("watch: empty input is an empty watch", Watch("").Cities.Count == 0 && Watch(null).Cities.Count == 0);
        Check("cities: 22 of them, no duplicates", TyphoonCities.All.Length == 22 && TyphoonCities.All.Distinct().Count() == 22);

        // ===== 篩選要通知的公告 =====
        var today = today0;
        var khOnly = TyphoonMatcher.Select(sample, Watch("高雄市:"), today);
        Check("select: whole-city announcement for a watched city is included; the mountain area is not (no areas listed)", khOnly.Count == 1 && khOnly[0].ScopeKind == "city" && khOnly[0].Date == new DateTime(2026, 10, 30));
        var khArea = TyphoonMatcher.Select(sample, Watch("高雄市:那瑪夏區"), today);
        Check("select: an area is included when it is on the city's list", khArea.Count == 2 && khArea.Any(a => a.Scope == "那瑪夏區"));
        var khOther = TyphoonMatcher.Select(sample, Watch("高雄市:前鎮區"), today);
        Check("select: 前鎮區 selected, 那瑪夏區 announcement is not notified (the reported use case)", khOther.Count == 1 && khOther[0].ScopeKind == "city");
        Check("select: an unwatched city produces nothing", TyphoonMatcher.Select(sample, Watch("臺中市:"), today).Count == 0 && TyphoonMatcher.Select(sample, Watch(""), today).Count == 0 && TyphoonMatcher.Select(null, Watch("高雄市:"), today).Count == 0);
        Check("select: school-level announcements are never notified, even if the school name is on the list", TyphoonMatcher.Select(Data(At("高雄市", Item(0, "高雄市某某國民中學", "school"))), Watch("高雄市:某某國民中學"), today).Count == 0);
        var unknownAlerts = TyphoonMatcher.Select(sample, Watch("花蓮縣:"), today);
        Check("select: an announcement the scraper could not read is passed on as unknown", unknownAlerts.Count == 1 && unknownAlerts[0].Unknown && unknownAlerts[0].Line(today).Contains("請自行查看"));
        Check("select: ...but only for watched cities", !TyphoonMatcher.Select(sample, Watch("高雄市:"), today).Any(a => a.Unknown));

        // 同名的區在不同縣市：清單只對各自的縣市有效
        var twoCities = Data(At("臺北市", Item(0, "中山區", "area")), At("基隆市", Item(0, "中山區", "area")));
        var pickTaipei = TyphoonMatcher.Select(twoCities, Watch("臺北市:中山區|基隆市:"), today);
        Check("select: the same district name in another city does not leak (per-city lists)", pickTaipei.Count == 1 && pickTaipei[0].City == "臺北市");
        var villageData = Data(At("高雄市", Item(0, "桃源區拉芙蘭里", "area")), At("花蓮縣", Item(0, "花蓮縣光復鄉", "area")));
        Check("select: matching is by 'contains' (village inside a listed district, scope with the city prefix)",
            TyphoonMatcher.Select(villageData, Watch("高雄市:桃源區"), today).Count == 1 && TyphoonMatcher.Select(villageData, Watch("花蓮縣:光復鄉"), today).Count == 1 && TyphoonMatcher.Select(villageData, Watch("高雄市:六龜區"), today).Count == 0);

        // 日期範圍
        var dated = Data(At("高雄市", Item(-1)), At("高雄市", Item(0)), At("高雄市", Item(2)), At("高雄市", Item(3)));
        Check("select: yesterday is dropped; today..+2 days are kept; +3 is not shown yet", TyphoonMatcher.Select(dated, Watch("高雄市:"), today).Select(a => (a.Date - today).Days).SequenceEqual(new[] { 0, 2 }));
        var noStop = Data(At("高雄市", Item(0, work: false, school: false)));
        Check("select: an item where nothing is stopped is ignored", TyphoonMatcher.Select(noStop, Watch("高雄市:"), today).Count == 0);

        // 頁面有內容但連縣市都看不懂
        var unparsed = new TyphoonData { State = "unparsed", UnparsedText = "颱風來襲" };
        Check("select: unreadable page content alerts anyone who watches something", TyphoonMatcher.Select(unparsed, Watch("高雄市:"), today).Count == 1 && TyphoonMatcher.Select(unparsed, Watch("高雄市:"), today)[0].Unknown);
        Check("select: ...and nobody who watches nothing", TyphoonMatcher.Select(unparsed, Watch(""), today).Count == 0);
        Check("select: results are ordered by date, then city", TyphoonMatcher.Select(Data(At("高雄市", Item(1)), At("臺北市", Item(0)), At("高雄市", Item(0))), Watch("高雄市:|臺北市:"), today).Select(a => a.City + (a.Date - today).Days).SequenceEqual(new[] { "臺北市0", "高雄市0", "高雄市1" }));

        // 文字
        var half = TyphoonMatcher.Select(sample, Watch("高雄市:那瑪夏區"), today);
        string lineCity = half.First(a => a.ScopeKind == "city").Line(today);
        string lineArea = half.First(a => a.ScopeKind == "area").Line(today);
        Check("text: whole-city line with day label and the original wording", lineCity == "高雄市 明天（10/30）下午01:00起停止上班、停止上課", lineCity);
        Check("text: area line says it is a partial area", lineArea == "高雄市 那瑪夏區（部分地區） 今天（10/29）停止上班、停止上課", lineArea);
        Check("text: Short() for the date title", half.First(a => a.ScopeKind == "city").Short() == "高雄市 下午13:00起停班停課" && half.First(a => a.ScopeKind == "area").Short() == "高雄市那瑪夏區 停班停課");
        var schoolOnlyStop = new TyphoonAlert { City = "高雄市", Date = today, Work = false, School = true };
        Check("text: with no original text it is built from the flags", schoolOnlyStop.Line(today) == "高雄市 今天（10/29）停課" && schoolOnlyStop.Short() == "高雄市 停課");
        Check("text: AckTitle separates different announcements of the same city", half[0].AckTitle != half[1].AckTitle);

        // 日曆標紅與標題
        Check("off day: red only on the date of a notified announcement", TyphoonMatcher.IsOff(khOnly, new DateTime(2026, 10, 30)) && !TyphoonMatcher.IsOff(khOnly, new DateTime(2026, 10, 29)) && !TyphoonMatcher.IsOff(khOnly, new DateTime(2026, 10, 31)));
        Check("off day: an unknown announcement never turns a date red", !TyphoonMatcher.IsOff(unknownAlerts, today));
        Check("describe: title suffix lists what is closed", TyphoonMatcher.Describe(khArea, new DateTime(2026, 10, 30)) == "颱風假：高雄市 下午13:00起停班停課" && TyphoonMatcher.Describe(khArea, today) == "颱風假：高雄市那瑪夏區 停班停課" && TyphoonMatcher.Describe(khArea, new DateTime(2026, 11, 5)) == null);

        // 資料多久沒更新
        Check("stale: within 8 hours is fresh, beyond is stale, unknown time or no data is stale",
            !TyphoonMatcher.IsStale(new TyphoonData { CheckedAtUtc = DateTime.UtcNow.AddHours(-7) }, DateTime.UtcNow) && TyphoonMatcher.IsStale(new TyphoonData { CheckedAtUtc = DateTime.UtcNow.AddHours(-9) }, DateTime.UtcNow) &&
            TyphoonMatcher.IsStale(new TyphoonData(), DateTime.UtcNow) && TyphoonMatcher.IsStale(null, DateTime.UtcNow));

        // ===== 鄉鎮市區名稱 =====
        Check("districts: all 22 cities have a list, 368 districts in all (the official number)", TyphoonCities.All.All(c => TyphoonDistricts.Of(c).Count > 0) && TyphoonCities.All.Sum(c => TyphoonDistricts.Of(c).Count) == 368);
        Check("districts: no duplicates inside a city, every name ends in 區/鄉/鎮/市",
            TyphoonCities.All.All(c => TyphoonDistricts.Of(c).Distinct().Count() == TyphoonDistricts.Of(c).Count && TyphoonDistricts.Of(c).All(n => "區鄉鎮市".Contains(n[n.Length - 1]))));
        Check("districts: known examples and per-city counts",
            TyphoonDistricts.Of("高雄市").Contains("前鎮區") && TyphoonDistricts.Of("高雄市").Contains("那瑪夏區") && TyphoonDistricts.Of("花蓮縣").Contains("秀林鄉") && TyphoonDistricts.Of("屏東縣").Contains("滿州鄉") &&
            TyphoonDistricts.Of("高雄市").Count == 38 && TyphoonDistricts.Of("臺南市").Count == 37 && TyphoonDistricts.Of("連江縣").Count == 4 && TyphoonDistricts.Of("嘉義市").Count == 2);
        Check("districts: the islands that are not townships (釣魚臺, 東沙, 南沙) are not listed", !TyphoonDistricts.Of("宜蘭縣").Contains("釣魚臺") && !TyphoonDistricts.Of("高雄市").Contains("東沙群島") && !TyphoonDistricts.Of("高雄市").Contains("南沙群島"));
        Check("districts: 台 equals 臺 in the city name; an unknown city has none", TyphoonDistricts.Of("台南市").Count == 37 && TyphoonDistricts.Of("火星市").Count == 0);
        Check("districts: IsStandard", TyphoonDistricts.IsStandard("高雄市", "前鎮區") && !TyphoonDistricts.IsStandard("高雄市", "秀林鄉") && !TyphoonDistricts.IsStandard("高雄市", "桃源區拉芙蘭里"));

        // 公告的範圍指的是哪個區：精確到標準區名
        Func<string, string, string> M = (city, scope) => string.Join("、", TyphoonDistricts.Match(city, scope));
        Check("match: a part of a township maps to the township (秀林鄉大天祥地區 -> 秀林鄉)", M("花蓮縣", "秀林鄉大天祥地區(含富世村布洛灣及13至19鄰範圍)") == "秀林鄉", M("花蓮縣", "秀林鄉大天祥地區(含富世村布洛灣及13至19鄰範圍)"));
        Check("match: a village maps to its district; a city prefix is fine", M("高雄市", "桃源區拉芙蘭里") == "桃源區" && M("花蓮縣", "花蓮縣光復鄉") == "光復鄉");
        Check("match: 安南區 is not mistaken for 南區, but both are found when both are named", M("臺南市", "安南區") == "安南區" && M("臺南市", "南區") == "南區" && M("臺南市", "臺南市南區、安南區").Split('、').OrderBy(x => x).SequenceEqual(new[] { "南區", "安南區" }.OrderBy(x => x)));
        Check("match: several districts in one scope are all returned", M("高雄市", "那瑪夏區、桃源區").Split('、').OrderBy(x => x).SequenceEqual(new[] { "那瑪夏區", "桃源區" }.OrderBy(x => x)));
        Check("match: the announcement's variant spelling 滿洲鄉 is the standard 滿州鄉", M("屏東縣", "滿洲鄉長樂村") == "滿州鄉");
        Check("match: things that are not a district (a clinic, a power plant) match nothing", M("宜蘭縣", "南澳衛生所") == "" && M("花蓮縣", "立霧溪左岸台灣電力公司東部發電廠") == "");
        Check("match: a district of another city is not matched", M("高雄市", "秀林鄉") == "");

        // 篩選時用標準區名比對
        var xiulin = Data(At("花蓮縣", Item(0, "秀林鄉大天祥地區(含富世村布洛灣及13至19鄰範圍)", "area")));
        Check("select: 秀林鄉 selected -> the 大天祥地區 announcement is notified (precise to the township); 光復鄉 selected -> not",
            TyphoonMatcher.Select(xiulin, Watch("花蓮縣:秀林鄉"), today).Count == 1 && TyphoonMatcher.Select(xiulin, Watch("花蓮縣:光復鄉"), today).Count == 0);
        var annan = Data(At("臺南市", Item(0, "安南區", "area")));
        Check("select: 南區 selected does not pick up 安南區, 安南區 selected does", TyphoonMatcher.Select(annan, Watch("臺南市:南區"), today).Count == 0 && TyphoonMatcher.Select(annan, Watch("臺南市:安南區"), today).Count == 1);
        Check("select: the variant spelling still matches the standard name that was ticked", TyphoonMatcher.Select(Data(At("屏東縣", Item(0, "滿洲鄉長樂村", "area"))), Watch("屏東縣:滿州鄉"), today).Count == 1);
        Check("select: an entry that is not a standard name (typed by hand in an earlier version) still works by 'contains'",
            TyphoonMatcher.Select(villageData, Watch("高雄市:桃源區拉芙蘭里"), today).Count == 1 && TyphoonMatcher.Select(villageData, Watch("高雄市:拉芙蘭里"), today).Count == 1);

        // ===== 網址 =====
        Check("url: empty means the default; http(s) is accepted; anything else is rejected",
            TyphoonFeed.ResolveUrl("") == TyphoonFeed.DefaultUrl && TyphoonFeed.ResolveUrl("  ") == TyphoonFeed.DefaultUrl && TyphoonFeed.ResolveUrl(null) == TyphoonFeed.DefaultUrl &&
            TyphoonFeed.ResolveUrl("https://example.com/a.json") == "https://example.com/a.json" && TyphoonFeed.ResolveUrl("http://127.0.0.1:1/x") == "http://127.0.0.1:1/x" &&
            TyphoonFeed.ResolveUrl("ftp://x/y") == null && TyphoonFeed.ResolveUrl("file:///c:/a.json") == null && TyphoonFeed.ResolveUrl("not a url") == null);
        Check("url: the default points at the published data", TyphoonFeed.DefaultUrl == "https://raw.githubusercontent.com/meme755144/taiwan-typhoon-days/main/data/latest.json");

        // ===== 設定存檔 =====
        var s0 = Settings.Load();
        Check("settings: the feature is off by default, with the default url and no cities", !s0.TyphoonEnabled && s0.TyphoonUrl == "" && s0.TyphoonWatchList == "");
        s0.TyphoonEnabled = true; s0.TyphoonUrl = "http://127.0.0.1:18768/latest.json"; s0.TyphoonWatchList = Watch("高雄市:前鎮區|臺北市:").Serialize();
        s0.Save();
        var s1 = Settings.Load();
        Check("settings: switch, url and watch list (with Chinese area names) survive a restart", s1.TyphoonEnabled && s1.TyphoonUrl == s0.TyphoonUrl && Watch(s1.TyphoonWatchList).AreasOf("高雄市").SequenceEqual(new[] { "前鎮區" }) && Watch(s1.TyphoonWatchList).Contains("臺北市"));
        s1.TyphoonEnabled = false; s1.Save();
        Check("settings: turning it off is remembered", !Settings.Load().TyphoonEnabled);

        // ===== 提醒框（用替身視窗）=====
        var rs = new Settings { TyphoonEnabled = true, ReminderIntervalMinutes = 60, ReminderStartHour = 0, ReminderEndHour = 24 };
        string ackFile = "typhoon-acked-test.txt";
        var acks = AckStore.Load(DateTime.Today, ackFile);
        var live = new List<TyphoonAlert> { new TyphoonAlert { City = "高雄市", Date = DateTime.Today.AddDays(1), Work = true, School = true, Text = (DateTime.Today.AddDays(1).Month + "/" + DateTime.Today.AddDays(1).Day) + "停止上班、停止上課。" } };
        var reminder = new TyphoonReminder(delegate(DateTime d) { return live.ToList(); }, rs, acks);

        int created0 = ReminderWindow.Created;
        reminder.CheckNow();
        var win = ReminderWindow.Last;
        Check("reminder: a new announcement opens one box with the line and a snooze text", ReminderWindow.Created == created0 + 1 && win.ShowCount == 1 && win.LastHeading == "停班停課通知" && win.LastLines.Count == 1 && win.LastLines[0].Text.StartsWith("高雄市 明天（") && win.LastSnoozeText.Contains("再問我"), win.LastLines.Count == 0 ? "" : win.LastLines[0].Text);
        win.ClickSnooze();
        Check("reminder: 'ask me later' hides the box", win.Hidden);
        reminder.OnDataChanged();
        Check("reminder: re-reading the same data does not pop a snoozed box up again", win.Hidden && win.ShowCount == 1);
        reminder.CheckNow();
        Check("reminder: the scheduled check shows it again (same box, not a second one)", !win.Hidden && win.ShowCount == 2 && ReminderWindow.Created == created0 + 1);
        win.ClickSnooze();
        live.Add(new TyphoonAlert { City = "臺北市", Date = DateTime.Today, Work = true, School = true, Text = "停止上班、停止上課。" });
        reminder.OnDataChanged();
        Check("reminder: a new announcement pops up right away even though the box was snoozed", !win.Hidden && win.LastLines.Count == 2 && win.LastHeading.Contains("2"));
        win.ClickAck();
        Check("reminder: 'OK' closes the box", win.Closed);
        reminder.CheckNow();
        Check("reminder: acknowledged announcements do not come back", ReminderWindow.Created == created0 + 1);
        var reloaded = AckStore.Load(DateTime.Today, ackFile);
        Check("reminder: the acknowledgement is remembered across a restart, in its own file", live.All(a => reloaded.IsAcked(a.Date, a.AckTitle)) && File.Exists(Path.Combine(dir, ackFile)) && !File.Exists(Path.Combine(dir, "acked.txt")));
        live.Add(new TyphoonAlert { City = "高雄市", Scope = "前鎮區", ScopeKind = "area", Date = DateTime.Today.AddDays(1), Work = true, School = true, Text = "停止上班、停止上課。" });
        reminder.OnDataChanged();
        Check("reminder: a different announcement for an already-acknowledged city is new again", ReminderWindow.Created == created0 + 2 && ReminderWindow.Last.LastLines.Count == 1);

        // 提醒時段外：新公告不立刻跳出，排定的檢查才會（不管時段）
        ReminderWindow.Last.ClickAck();
        int hour = DateTime.Now.Hour;
        rs.ReminderStartHour = hour < 23 ? hour + 1 : 0;
        rs.ReminderEndHour = hour < 23 ? 24 : 23;
        Check("window: InWindow follows start (inclusive) and end (exclusive); nonsense means all day",
            TyphoonReminder.InWindow(new DateTime(2026, 10, 29, 9, 0, 0), 9, 18) && !TyphoonReminder.InWindow(new DateTime(2026, 10, 29, 18, 0, 0), 9, 18) && !TyphoonReminder.InWindow(new DateTime(2026, 10, 29, 8, 59, 0), 9, 18) &&
            TyphoonReminder.InWindow(new DateTime(2026, 10, 29, 3, 0, 0), 18, 9) && TyphoonReminder.InWindow(new DateTime(2026, 10, 29, 23, 0, 0), 0, 24));
        live.Add(new TyphoonAlert { City = "新北市", Date = DateTime.Today, Work = true, School = true, Text = "停止上班、停止上課。" });
        int created1 = ReminderWindow.Created;
        reminder.OnDataChanged();
        Check("reminder: outside the reminder hours a new announcement waits", ReminderWindow.Created == created1);
        reminder.CheckNow();
        Check("reminder: ...until the scheduled check (which ignores the hours because it is only scheduled inside them)", ReminderWindow.Created == created1 + 1 && ReminderWindow.Last.LastLines.Count == 1);
        rs.ReminderStartHour = 0; rs.ReminderEndHour = 24;

        // 關閉功能：框關掉、不再提醒
        var lastWin = ReminderWindow.Last;
        rs.TyphoonEnabled = false;
        reminder.ScheduleNext();
        Check("reminder: turning the feature off closes the open box", lastWin.Closed);
        int created2 = ReminderWindow.Created;
        live.Add(new TyphoonAlert { City = "宜蘭縣", Date = DateTime.Today, Work = true, School = true, Text = "停止上班、停止上課。" });
        reminder.OnDataChanged();
        reminder.CheckNow();
        Check("reminder: ...and nothing pops up while it is off", ReminderWindow.Created == created2);
        rs.TyphoonEnabled = true;

        // ===== 讀取（假伺服器）=====
        var ms = new Settings { TyphoonEnabled = false, TyphoonUrl = Fake + "/latest.json", TyphoonWatchList = Watch("高雄市:前鎮區").Serialize() };
        var ui = Dispatcher.CurrentDispatcher;
        var mgr = new TyphoonManager(ui, ms);
        int changed = 0, status = 0;
        mgr.Changed += delegate { changed++; };
        mgr.StatusChanged += delegate { status++; };

        Mode("none");
        int hits0 = HitCount();
        mgr.Apply();
        Wait(400);
        Check("feed: switched off means no connection at all, no alerts, nothing red", HitCount() == hits0 && mgr.Alerts(DateTime.Today).Count == 0 && !mgr.IsOff(DateTime.Today) && mgr.StatusText() == "功能目前關閉。");

        ms.TyphoonEnabled = true;
        mgr.Apply();
        Pump(() => mgr.StatusText().Contains("成功"));
        Check("feed: switched on reads once straight away; 'none' means no alerts and says so", HitCount() == hits0 + 1 && mgr.Alerts(DateTime.Today).Count == 0 && mgr.StatusText().Contains("目前沒有停班停課公告") && mgr.StatusText().Contains("最近一次讀取"), mgr.StatusText());
        string cachePath = Path.Combine(dir, "local", "typhoon-latest.json");
        Check("feed: the data that was read is cached on this computer", File.Exists(cachePath) && Parse(File.ReadAllText(cachePath, Encoding.UTF8)).State == "none");

        // 公告出現：只有高雄市整個縣市（前鎮區沒有、那瑪夏區不在清單裡）
        int changedBefore = changed;
        Mode("announced");
        mgr.RefreshNow();
        Pump(() => changed > changedBefore);
        var alerts = mgr.Alerts(DateTime.Today);
        Check("feed: an announcement raises Changed; only the whole-city one for 高雄市 is kept (前鎮區 listed, 那瑪夏區 and 臺北市 not)", alerts.Count == 1 && alerts[0].City == "高雄市" && alerts[0].ScopeKind == "city" && alerts[0].Date == DateTime.Today.AddDays(1), alerts.Count + "");
        Check("feed: tomorrow turns red, today does not; the title says why", mgr.IsOff(DateTime.Today.AddDays(1)) && !mgr.IsOff(DateTime.Today) && mgr.Describe(DateTime.Today.AddDays(1)).StartsWith("颱風假：高雄市"));
        Check("feed: status lists the cities with announcements", mgr.StatusText().Contains("目前有停班停課公告的縣市") && mgr.StatusText().Contains("高雄市"), mgr.StatusText());
        Check("feed: the cache now holds the announcement", Parse(File.ReadAllText(cachePath, Encoding.UTF8)).State == "announced");

        // 同樣的內容、只有 checkedAt 不同：不算變化
        changedBefore = changed;
        int statusBefore = status;
        Mode("announced", 0, 5);
        mgr.RefreshNow();
        Pump(() => status > statusBefore);
        Wait(200);
        Check("feed: only checkedAt changed -> Changed is not raised (no redraw, no re-notification)", changed == changedBefore);

        // 沒更新：If-None-Match 帶上去，回 304
        statusBefore = status;
        mgr.RefreshNow();
        Pump(() => status > statusBefore);
        var h = Hits();
        Check("feed: the ETag is sent back and the server answers 304", Convert.ToInt32(h["notModified"]) >= 1 && h["ifNoneMatch"].ToString().StartsWith("\"") && changed == changedBefore, h["ifNoneMatch"] + "/" + h["notModified"]);
        Check("feed: after a 304 the data is still there and the read counts as successful", mgr.Alerts(DateTime.Today).Count == 1 && mgr.StatusText().Contains("（成功）"));

        // 讀取失敗：保留舊資料
        statusBefore = status;
        Mode("announced", 0, 0, null, 500);
        mgr.RefreshNow();
        Pump(() => status > statusBefore);
        Check("feed: a server error keeps the last data and shows the reason", mgr.Alerts(DateTime.Today).Count == 1 && mgr.StatusText().Contains("讀取失敗"), mgr.StatusText());
        statusBefore = status;
        Mode("none", 0, 0, "<html>maintenance</html>");
        mgr.RefreshNow();
        Pump(() => status > statusBefore);
        Check("feed: a page that is not the expected JSON is refused, the last data is kept", mgr.Alerts(DateTime.Today).Count == 1 && mgr.StatusText().Contains("資料格式不正確"), mgr.StatusText());
        Check("feed: ...and a bad answer does not overwrite the cache", Parse(File.ReadAllText(cachePath, Encoding.UTF8)).State == "announced");

        // 資料太舊、爬蟲回報錯誤
        statusBefore = status;
        Mode("announced", 10);
        mgr.RefreshNow();
        Pump(() => status > statusBefore);
        Check("feed: data older than 8 hours is flagged as possibly outdated", mgr.StatusText().Contains("小時沒有更新") && mgr.Alerts(DateTime.Today).Count == 1, mgr.StatusText());
        statusBefore = status; changedBefore = changed;
        Mode("error");
        mgr.RefreshNow();
        Pump(() => status > statusBefore);
        Check("feed: an error reported by the scraper is shown and counts as a change", mgr.StatusText().Contains("資料來源回報錯誤") && mgr.StatusText().Contains("403") && changed > changedBefore, mgr.StatusText());

        // 離線啟動：用本機快取（最後一次成功存的是公告）
        Mode("announced");
        statusBefore = status;
        mgr.RefreshNow();
        Pump(() => status > statusBefore);
        var offline = new Settings { TyphoonEnabled = true, TyphoonUrl = "http://127.0.0.1:1/latest.json", TyphoonWatchList = ms.TyphoonWatchList };
        var offlineMgr = new TyphoonManager(ui, offline);
        offlineMgr.Apply();
        Check("feed: starting offline, the cached data is available immediately", offlineMgr.Alerts(DateTime.Today).Count == 1 && offlineMgr.IsOff(DateTime.Today.AddDays(1)));
        Pump(() => offlineMgr.StatusText().Contains("讀取失敗"));
        Check("feed: ...and the failed read is reported without losing it", offlineMgr.StatusText().Contains("讀取失敗") && offlineMgr.Alerts(DateTime.Today).Count == 1, offlineMgr.StatusText());

        // 網址不正確
        var bad = new Settings { TyphoonEnabled = true, TyphoonUrl = "ftp://nope", TyphoonWatchList = "高雄市:" };
        var badMgr = new TyphoonManager(ui, bad);
        int hitsBad = HitCount();
        badMgr.Apply();
        Wait(300);
        Check("feed: a bad url is reported and nothing is requested", badMgr.StatusText().Contains("網址不正確") && HitCount() == hitsBad, badMgr.StatusText());

        // 改關注的縣市、區：立刻依新設定重新篩選（不用重新讀取）
        Mode("area");
        statusBefore = status;
        mgr.RefreshNow();
        Pump(() => status > statusBefore);
        Check("settings change: 前鎮區 is on the list, so the 前鎮區 half-day announcement is notified and says it is partial",
            mgr.Alerts(DateTime.Today).Count == 1 && mgr.Alerts(DateTime.Today)[0].ScopeKind == "area" && mgr.Alerts(DateTime.Today)[0].Line(DateTime.Today).Contains("前鎮區（部分地區）"), mgr.Alerts(DateTime.Today).Count == 0 ? "" : mgr.Alerts(DateTime.Today)[0].Line(DateTime.Today));
        changedBefore = changed;
        ms.TyphoonWatchList = Watch("高雄市:小港區").Serialize();
        mgr.Apply();
        Check("settings change: not on the list any more -> no alert, and the calendar is told to redraw", mgr.Alerts(DateTime.Today).Count == 0 && changed > changedBefore);
        ms.TyphoonWatchList = Watch("高雄市:").Serialize();
        mgr.Apply();
        Check("settings change: empty area list means no areas at all", mgr.Alerts(DateTime.Today).Count == 0);

        // 關掉功能：沒有提醒、沒有紅字
        Mode("announced");
        ms.TyphoonWatchList = Watch("高雄市:").Serialize();
        mgr.Apply();
        Pump(() => mgr.Alerts(DateTime.Today).Count == 1);
        changedBefore = changed;
        ms.TyphoonEnabled = false;
        mgr.Apply();
        int hitsOff = HitCount();
        Wait(300);
        Check("settings change: turned off -> no alerts, nothing red, Changed raised, and no further reads", mgr.Alerts(DateTime.Today).Count == 0 && !mgr.IsOff(DateTime.Today.AddDays(1)) && mgr.Describe(DateTime.Today.AddDays(1)) == null && changed > changedBefore && HitCount() == hitsOff);

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

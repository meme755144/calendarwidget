using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using CalendarWidget;

static class P
{
    static int fails;
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

    static string Ics(params DateTime[] days)
    {
        var sb = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//t//EN\r\n");
        int i = 0;
        foreach (var d in days)
        {
            sb.Append("BEGIN:VEVENT\r\nUID:t" + (i++) + "@t\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;VALUE=DATE:" + d.ToString("yyyyMMdd") + "\r\nDTEND;VALUE=DATE:" + d.AddDays(1).ToString("yyyyMMdd") + "\r\nSUMMARY:事件" + d.ToString("MMdd") + "\r\nEND:VEVENT\r\n");
        }
        return sb.Append("END:VCALENDAR\r\n").ToString();
    }

    static int Main()
    {
        string dir = Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA");
        if (string.IsNullOrEmpty(dir)) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        DateTime today = DateTime.Today;
        var ui = Dispatcher.CurrentDispatcher;

        // ===== 設定：載入範圍與下方清單大小 =====
        var s0 = Settings.Load();
        Check("settings: defaults are 6 months back, 12 ahead, detail scale 100%", s0.LoadPastMonths == 6 && s0.LoadFutureMonths == 12 && Math.Abs(s0.DetailScale - 1.0) < 0.0001 && LoadRange.PastMonths == 6 && LoadRange.FutureMonths == 12);
        s0.LoadPastMonths = 24; s0.LoadFutureMonths = 36; s0.DetailScale = 1.4;
        s0.Save();
        var s1 = Settings.Load();
        Check("settings: range and detail scale survive a restart, and Load applies the range", s1.LoadPastMonths == 24 && s1.LoadFutureMonths == 36 && Math.Abs(s1.DetailScale - 1.4) < 0.0001 && LoadRange.PastMonths == 24 && LoadRange.FutureMonths == 36);
        File.AppendAllText(Path.Combine(dir, "settings.ini"), "LoadPastMonths=0\nLoadFutureMonths=999\nDetailScale=9\n");
        var s2 = Settings.Load();
        Check("settings: out-of-range values are ignored (range) or clamped (scale)", s2.LoadPastMonths == 24 && s2.LoadFutureMonths == 36 && Math.Abs(s2.DetailScale - 2.0) < 0.0001, s2.LoadPastMonths + "/" + s2.LoadFutureMonths + "/" + s2.DetailScale);
        Check("LoadRange: From / To follow the months", LoadRange.From == today.AddMonths(-24) && LoadRange.To == today.AddMonths(36));
        LoadRange.PastMonths = 6; LoadRange.FutureMonths = 12;

        // ===== 網路上的 ICS：受「載入範圍」限制 =====
        DateTime near = today.AddDays(10), old = today.AddMonths(-4), far = today.AddMonths(14);
        var storeHttp = new ItemStore();
        var mgrHttp = new SourceManager(storeHttp, ui);
        mgrHttp.Start();
        var listHttp = mgrHttp.Sources;
        var srcHttp = new CalendarSource { Name = "網路", Url = "http://127.0.0.1:18765/range", Kind = ItemKind.Event, IntervalMinutes = 0 };
        listHttp.Add(srcHttp);
        mgrHttp.Apply(listHttp);
        Pump(() => storeHttp.For(near).Count > 0);
        Check("http source: default range (6 months back, 12 ahead) includes 4 months ago and excludes 14 months ahead", storeHttp.For(near).Count == 1 && storeHttp.For(old).Count == 1 && storeHttp.For(far).Count == 0);
        LoadRange.PastMonths = 2;
        mgrHttp.Reparse();
        Pump(() => storeHttp.For(old).Count == 0);
        Check("range change: re-parsing the saved copy drops events older than the new range (no download again)", storeHttp.For(old).Count == 0 && storeHttp.For(near).Count == 1);
        LoadRange.PastMonths = 6; LoadRange.FutureMonths = 18;
        mgrHttp.Reparse();
        Pump(() => storeHttp.For(far).Count == 1);
        Check("range change: a wider range brings the far-away event back", storeHttp.For(far).Count == 1 && storeHttp.For(old).Count == 1);
        LoadRange.FutureMonths = 12;
        mgrHttp.Apply(new List<CalendarSource>());   // 來源清單存在同一個資料夾，先清掉，免得下面的管理員也載到這個網路來源

        // ===== 本機檔案當成 ICS 來源（file:///）：不受「載入範圍」限制 =====
        string icsPath = Path.Combine(dir, "gen", "holidays.ics");
        Directory.CreateDirectory(Path.GetDirectoryName(icsPath));
        DateTime veryFar = today.AddYears(3), veryOld = today.AddYears(-2);
        File.WriteAllText(icsPath, Ics(near, old, far, veryFar, veryOld), new UTF8Encoding(false));
        string url = new Uri(icsPath).AbsoluteUri;

        var store = new ItemStore();
        var mgr = new SourceManager(store, ui);
        mgr.Start();
        var list = mgr.Sources;
        var src = new CalendarSource { Name = "本機檔", Url = url, Kind = ItemKind.Holiday, IntervalMinutes = 0 };
        list.Add(src);
        mgr.Apply(list);
        Pump(() => store.For(near).Count > 0);
        Check("file source: events from a local file show up as holidays", store.For(near).Count == 1 && store.For(near)[0].Kind == ItemKind.Holiday && store.For(near)[0].Title == "事件" + near.ToString("MMdd"));
        Check("file source: not limited by the load range (14 months ahead, 3 years ahead, 2 years back all show)", store.For(far).Count == 1 && store.For(veryFar).Count == 1 && store.For(veryOld).Count == 1);
        Check("file source: status reports success", mgr.StatusText(src.Id).Contains("上次更新") && mgr.StatusOf(src.Id).Error == null, mgr.StatusText(src.Id));

        File.WriteAllText(icsPath, Ics(near.AddDays(1)), new UTF8Encoding(false));
        mgr.RefreshNow(src.Id);
        Pump(() => store.For(near.AddDays(1)).Count == 1);
        Check("file source: refreshing re-reads the file (new content replaces old)", store.For(near.AddDays(1)).Count == 1 && store.For(near).Count == 0);

        // 檔案不見了：保留舊資料，顯示錯誤
        File.Delete(icsPath);
        mgr.RefreshNow(src.Id);
        Pump(() => mgr.StatusOf(src.Id).Error != null);
        Check("file source: a missing file keeps the old events and reports a readable error", store.For(near.AddDays(1)).Count == 1 && mgr.StatusText(src.Id).Contains("找不到這個檔案"), mgr.StatusText(src.Id));

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}

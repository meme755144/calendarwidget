using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CalendarWidget;

static class P
{
    static int fails;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail)); if (!ok) fails++; }

    static readonly Version V090 = new Version(0, 9, 0);

    static UpdateResult Fetch(string path)
    {
        Environment.SetEnvironmentVariable("CALENDARWIDGET_UPDATE_URL", "http://127.0.0.1:18769" + path);
        return UpdateChecker.Check(V090);
    }

    static int Main(string[] args)
    {
        string root = args[0];   // 倉庫根目錄（要讀 CHANGELOG.md 與 csproj）

        // 第二種執行方式（--no-override，且沒有設 CALENDARWIDGET_DATA）：模擬平常使用，環境變數不能把檢查更新導去別的伺服器
        if (args.Length > 1 && args[1] == "--no-override")
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA"))) { Console.WriteLine("CALENDARWIDGET_DATA must be unset"); return 2; }
            Environment.SetEnvironmentVariable("CALENDARWIDGET_UPDATE_URL", "http://127.0.0.1:18769/new");
            Check("url: without a test data folder the env var is ignored", UpdateChecker.ApiUrl == "https://api.github.com/repos/meme755144/calendarwidget/releases/latest", UpdateChecker.ApiUrl);
            return fails == 0 ? 0 : 1;
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA"))) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }

        // ===== 版本號解析 =====
        Version v;
        Check("tag: v0.9.1", UpdateChecker.TryParseTag("v0.9.1", out v) && v == new Version(0, 9, 1));
        Check("tag: without v, two parts -> patch 0", UpdateChecker.TryParseTag("1.2", out v) && v == new Version(1, 2, 0));
        Check("tag: pre-release suffix is ignored", UpdateChecker.TryParseTag("v1.0.0-beta.2", out v) && v == new Version(1, 0, 0));
        Check("tag: four parts keep the first three", UpdateChecker.TryParseTag("2.3.4.5", out v) && v == new Version(2, 3, 4));
        Check("tag: 10 > 9 numerically (not as text)", UpdateChecker.TryParseTag("v0.10.0", out v) && v > new Version(0, 9, 5));
        Check("tag: garbage / empty / null rejected",
            !UpdateChecker.TryParseTag("latest", out v) && !UpdateChecker.TryParseTag("", out v) && !UpdateChecker.TryParseTag(null, out v) && !UpdateChecker.TryParseTag("v1", out v) && !UpdateChecker.TryParseTag("v99999999999.1.0", out v));

        // ===== 官方網址（伺服器回傳的網址不能直接信任）=====
        Check("url: release page ok", AppInfo.IsOfficialUrl("https://github.com/meme755144/calendarwidget/releases/tag/v0.9.1"));
        Check("url: repo root and Pages ok", AppInfo.IsOfficialUrl("https://github.com/meme755144/calendarwidget") && AppInfo.IsOfficialUrl("https://meme755144.github.io/calendarwidget/privacy.html"));
        Check("url: lookalike repo name rejected", !AppInfo.IsOfficialUrl("https://github.com/meme755144/calendarwidget-evil/releases"));
        Check("url: other user / other host rejected",
            !AppInfo.IsOfficialUrl("https://github.com/someone/calendarwidget") && !AppInfo.IsOfficialUrl("https://evil.example/meme755144/calendarwidget") && !AppInfo.IsOfficialUrl("https://github.com.evil.example/meme755144/calendarwidget"));
        Check("url: http, userinfo, odd port, file, non-url rejected",
            !AppInfo.IsOfficialUrl("http://github.com/meme755144/calendarwidget") && !AppInfo.IsOfficialUrl("https://meme755144@github.com/meme755144/calendarwidget") &&
            !AppInfo.IsOfficialUrl("https://github.com:8443/meme755144/calendarwidget") && !AppInfo.IsOfficialUrl("file:///C:/Windows/System32/calc.exe") &&
            !AppInfo.IsOfficialUrl("calc.exe") && !AppInfo.IsOfficialUrl("") && !AppInfo.IsOfficialUrl(null));
        Check("url: OpenUrl refuses a non-official url without starting anything", !AppInfo.OpenUrl("https://evil.example/x.exe"));

        // ===== 把 HTTP 結果轉成結果（不連線）=====
        string ok = "{\"tag_name\":\"v0.9.1\",\"html_url\":\"https://github.com/meme755144/calendarwidget/releases/tag/v0.9.1\",\"body\":\"- a\"}";
        var r = UpdateChecker.Interpret(V090, 200, ok);
        Check("interpret: newer tag -> NewVersion with url and notes", r.Status == UpdateStatus.NewVersion && r.Version == "0.9.1" && r.Url.EndsWith("/releases/tag/v0.9.1") && r.Notes == "- a");
        Check("interpret: same version -> UpToDate", UpdateChecker.Interpret(V090, 200, ok.Replace("v0.9.1", "v0.9.0")).Status == UpdateStatus.UpToDate);
        Check("interpret: older release -> UpToDate (never a downgrade)", UpdateChecker.Interpret(V090, 200, ok.Replace("v0.9.1", "v0.8.9")).Status == UpdateStatus.UpToDate);
        Check("interpret: 404 -> NoRelease", UpdateChecker.Interpret(V090, 404, "").Status == UpdateStatus.NoRelease);
        Check("interpret: 403/429 -> Failed with a rate-limit message", UpdateChecker.Interpret(V090, 403, "").Status == UpdateStatus.Failed && UpdateChecker.Interpret(V090, 429, "").Message.Contains("限制"));
        Check("interpret: 500 -> Failed", UpdateChecker.Interpret(V090, 500, "").Status == UpdateStatus.Failed);
        Check("interpret: not JSON -> Failed, no exception", UpdateChecker.Interpret(V090, 200, "<html>").Status == UpdateStatus.Failed && UpdateChecker.Interpret(V090, 200, "[1,2]").Status == UpdateStatus.Failed);
        Check("interpret: unreadable tag -> Failed", UpdateChecker.Interpret(V090, 200, "{\"tag_name\":\"nightly\"}").Status == UpdateStatus.Failed && UpdateChecker.Interpret(V090, 200, "{}").Status == UpdateStatus.Failed);
        r = UpdateChecker.Interpret(V090, 200, ok.Replace("https://github.com/meme755144/calendarwidget/releases/tag/v0.9.1", "https://evil.example/a.exe"));
        Check("interpret: non-official html_url is replaced by the fixed releases page", r.Status == UpdateStatus.NewVersion && r.Url == AppInfo.ReleasesUrl, r.Url);

        // ===== 發行說明預覽 =====
        var preview = UpdateChecker.NotesPreview("## 新功能\r\n- 修正月曆顯示\r\n\r\n" + new string('字', 100) + "\r\n- 三\r\n- 四", 3);
        Check("notes: markdown markers stripped, blanks skipped, capped, long lines cut",
            preview.Count == 3 && preview[0] == "新功能" && preview[1] == "修正月曆顯示" && preview[2].Length == 61 && preview[2].EndsWith("…"), string.Join("|", preview));
        Check("notes: bold and code markers removed", UpdateChecker.NotesPreview("**完整說明**請看 `CHANGELOG`", 3)[0] == "完整說明請看 CHANGELOG");
        Check("notes: empty or null -> no lines", UpdateChecker.NotesPreview("", 6).Count == 0 && UpdateChecker.NotesPreview(null, 6).Count == 0);

        // ===== 真的連線（假的 GitHub 伺服器）=====
        r = Fetch("/new");
        Check("http: newer release found (also proves a User-Agent is sent)", r.Status == UpdateStatus.NewVersion && r.Version == "0.9.1" && r.Url == "https://github.com/meme755144/calendarwidget/releases/tag/v0.9.1" && r.Notes.Contains("修正月曆顯示"), r.Status + " " + r.Message);
        Check("http: same version", Fetch("/same").Status == UpdateStatus.UpToDate);
        Check("http: older release is not offered", Fetch("/old").Status == UpdateStatus.UpToDate);
        r = Fetch("/evil");
        Check("http: server-supplied foreign url is never passed on", r.Status == UpdateStatus.NewVersion && r.Url == AppInfo.ReleasesUrl, r.Url);
        Check("http: no release yet (404)", Fetch("/none").Status == UpdateStatus.NoRelease);
        Check("http: rate limited (403)", Fetch("/limit").Status == UpdateStatus.Failed);
        Check("http: server error (500)", Fetch("/boom").Status == UpdateStatus.Failed);
        Check("http: garbage body", Fetch("/garbage").Status == UpdateStatus.Failed);
        Environment.SetEnvironmentVariable("CALENDARWIDGET_UPDATE_URL", "http://127.0.0.1:1/unreachable");
        r = UpdateChecker.Check(V090);
        Check("http: nothing listening -> Failed with a readable message, no exception", r.Status == UpdateStatus.Failed && r.Message.Contains("GitHub"), r.Message);

        // 沒設環境變數時是官方的 API（有設測試資料夾但沒設網址的情況）
        Environment.SetEnvironmentVariable("CALENDARWIDGET_UPDATE_URL", null);
        Check("url: default points at api.github.com for this repo", UpdateChecker.ApiUrl == "https://api.github.com/repos/meme755144/calendarwidget/releases/latest", UpdateChecker.ApiUrl);

        // ===== 版本紀錄 =====
        string changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"), Encoding.UTF8);
        var entries = Changelog.Parse(changelog);
        Check("changelog: at least one version, each with content", entries.Count >= 1 && entries.All(e => e.Lines.Count > 0));
        Check("changelog: heading is shown as version(date)", Regex.IsMatch(entries[0].Heading, @"^\d+\.\d+\.\d+（\d{4}-\d{2}-\d{2}）$"), entries[0].Heading);
        Check("changelog: sections and bullets are recognised", entries[0].Lines.Any(l => l.Kind == ChangelogLineKind.Section) && entries[0].Lines.Any(l => l.Kind == ChangelogLineKind.Bullet && !l.Text.StartsWith("-")));
        var small = Changelog.Parse("前言\n\n## [1.0.0] - 2027-01-01\n### 新增\n- a\n說明文字\n\n## [0.9.0] - 2026-10-30\n- b\n");
        Check("changelog: preface skipped, two versions, kinds assigned",
            small.Count == 2 && small[0].Heading == "1.0.0（2027-01-01）" && small[0].Lines.Count == 3 &&
            small[0].Lines[0].Kind == ChangelogLineKind.Section && small[0].Lines[1].Kind == ChangelogLineKind.Bullet && small[0].Lines[2].Kind == ChangelogLineKind.Text && small[1].Lines[0].Text == "b");
        Check("changelog: empty input -> no entries", Changelog.Parse("").Count == 0 && Changelog.Parse(null).Count == 0);

        // 新版的內容來自 GitHub Release 的內文（網路來的文字）
        var rn = Changelog.ParseNotes("## 新功能\r\n- 修正**月曆**顯示\r\n- 新增 `設定` 項目\r\n\r\nSHA256: abc\r\n#### 已知限制\r\n* 沒有簽章", "新版 0.9.1（尚未安裝）", 60);
        Check("notes: heading kept, any #-level is a section, bullets recognised, markers stripped",
            rn.Heading == "新版 0.9.1（尚未安裝）" && rn.Lines.Count == 6 &&
            rn.Lines[0].Kind == ChangelogLineKind.Section && rn.Lines[0].Text == "新功能" &&
            rn.Lines[1].Kind == ChangelogLineKind.Bullet && rn.Lines[1].Text == "修正月曆顯示" && rn.Lines[2].Text == "新增 設定 項目" &&
            rn.Lines[3].Kind == ChangelogLineKind.Text && rn.Lines[4].Kind == ChangelogLineKind.Section && rn.Lines[4].Text == "已知限制" && rn.Lines[5].Kind == ChangelogLineKind.Bullet,
            string.Join("|", rn.Lines.Select(l => l.Kind + ":" + l.Text)));
        var longNotes = Changelog.ParseNotes(string.Join("\n", Enumerable.Range(0, 200).Select(i => "- " + new string('字', 500))), "x", 60);
        Check("notes: line count capped at 60, long lines cut", longNotes.Lines.Count == 60 && longNotes.Lines[0].Text.Length == 301, longNotes.Lines.Count + " / " + longNotes.Lines[0].Text.Length);
        Check("notes: empty / null / only symbols -> no lines, no exception", Changelog.ParseNotes("", "x", 60).Lines.Count == 0 && Changelog.ParseNotes(null, "x", 60).Lines.Count == 0 && Changelog.ParseNotes("###\n**\n", "x", 60).Lines.Count == 0);

        // ===== 發版時最容易漏的：csproj 的版本號要等於 CHANGELOG 最上面那一筆 =====
        string csproj = File.ReadAllText(Path.Combine(root, "src", "CalendarWidget", "CalendarWidget.csproj"), Encoding.UTF8);
        Match m = Regex.Match(csproj, @"<Version>([^<]+)</Version>");
        string topVersion = Regex.Match(changelog, @"^## \[?([^\]\s]+)\]?", RegexOptions.Multiline).Groups[1].Value;
        Check("release: csproj <Version> equals the newest CHANGELOG version", m.Success && m.Groups[1].Value == topVersion, (m.Success ? m.Groups[1].Value : "no <Version>") + " vs " + topVersion);

        Console.WriteLine(fails == 0 ? "ALL OK" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}

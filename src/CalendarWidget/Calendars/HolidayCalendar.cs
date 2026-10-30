using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace CalendarWidget
{
    // 一天的資料：來自「中華民國政府行政機關辦公日曆表」（行政院人事行政總處，政府資料開放授權條款第 1 版）。
    // 這裡用的是 ruyut/TaiwanCalendar 整理好的 JSON（官方原始檔是 Big5 的 CSV，每年的網址都不一樣），內容與官方一致
    internal sealed class HolidayEntry
    {
        public DateTime Date;
        public bool IsHoliday;       // 放假（含週末、國定假日、補假）
        public string Description;   // 節日名稱、補假、補行上班…；沒有特別說明就是空字串
        public bool IsWeekend { get { return Date.DayOfWeek == DayOfWeek.Saturday || Date.DayOfWeek == DayOfWeek.Sunday; } }
    }

    internal enum HolidayKind
    {
        Holiday,   // 國定假日、補假、小年夜、春節…
        Weekend,   // 一般的週六、週日（沒有特別名稱）
        MakeUp     // 補班日：本來是週末，但要上班
    }

    internal sealed class HolidayOptions
    {
        public bool Holidays = true;      // 國定假日與補假（有名稱的放假日）
        public bool Weekends;             // 一般週末
        public bool MakeUpDays = true;    // 補班日
        public bool Merge = true;         // 相同名稱的連續假日合併成一筆（例如春節）
        public bool Alarm;                // 加入提醒：前一天 09:00
    }

    internal sealed class HolidayEvent
    {
        public DateTime Start;
        public DateTime EndExclusive;   // 最後一天的隔天（ICS 與 Google 的整天事件都是這樣）
        public string Title;
        public HolidayKind Kind;
        public string Note;             // 原始資料裡的說明
    }

    internal static class HolidayCalendar
    {
        public const int FirstYear = 2017;   // 官方資料集最早的年度（民國 106 年）
        public const int NextYearAvailableMonth = 6;   // 行政院每年約 6 月公布次年度的行事曆

        // 可以選的年度：從最早有資料的 2017 年到今年；次年度要等到 6 月以後才開放選（官方還沒公布，選了也沒有資料）
        public static List<int> AvailableYears(DateTime today)
        {
            int last = today.Month >= NextYearAvailableMonth ? today.Year + 1 : today.Year;
            var years = new List<int>();
            for (int y = last; y >= FirstYear; y--) years.Add(y);
            return years;
        }

        // 資料網址。測試時（有設定測試資料夾）可以用環境變數 CALENDARWIDGET_HOLIDAY_URL 換成自己的伺服器，{year} 會被取代成年度
        public static string DataUrl(int year)
        {
            string template = null;
            if (!string.IsNullOrEmpty(DataPaths.Override)) template = Environment.GetEnvironmentVariable("CALENDARWIDGET_HOLIDAY_URL");
            if (string.IsNullOrEmpty(template)) template = "https://cdn.jsdelivr.net/gh/ruyut/TaiwanCalendar/data/{year}.json";
            return template.Replace("{year}", year.ToString(CultureInfo.InvariantCulture));
        }

        // 下載某一年的資料並解析（會卡住呼叫的執行緒，請在背景執行緒呼叫）。失敗丟 InvalidDataException 或 WebException
        public static List<HolidayEntry> Download(int year)
        {
            string error;
            var entries = Parse(FetchText(year), year, out error);
            if (entries == null) throw new InvalidDataException(error);
            return entries;
        }

        // 只下載某一年的原始 JSON 文字（要存快取時用，內容還沒檢查）。失敗的情況同 Download
        public static string FetchText(int year)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var req = (HttpWebRequest)WebRequest.Create(DataUrl(year));
            req.UserAgent = "CalendarWidget/1.0";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            string text;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    // 一整年的資料只有幾十 KB；設上限，不讓怪回應塞爆記憶體
                    var sb = new StringBuilder();
                    var buffer = new char[8192];
                    int n;
                    while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        sb.Append(buffer, 0, n);
                        if (sb.Length > 2 * 1024 * 1024) throw new InvalidDataException("資料太大，已中止");
                    }
                    text = sb.ToString();
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidDataException("還沒有 " + year + " 年的資料（官方通常 6 月以後才公布次年度）");
                throw;
            }
            return text;
        }

        // 解析 JSON 陣列：[{"date":"20260101","week":"四","isHoliday":true,"description":"開國紀念日"}, ...]
        // 資料不完整（天數太少、日期不屬於該年度）就回傳 null 並說明原因
        public static List<HolidayEntry> Parse(string json, int year, out string error)
        {
            error = null;
            var items = Json.ParseList(json);
            if (items == null || items.Count == 0)
            {
                error = "資料格式不正確（不是日期清單）";
                return null;
            }

            var result = new List<HolidayEntry>();
            foreach (var item in items)
            {
                DateTime d;
                if (!DateTime.TryParseExact(Json.Str(item, "date") ?? "", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) || d.Year != year) continue;
                result.Add(new HolidayEntry
                {
                    Date = d,
                    IsHoliday = Json.Bool(item, "isHoliday"),
                    Description = (Json.Str(item, "description") ?? "").Trim()
                });
            }

            int expected = DateTime.IsLeapYear(year) ? 366 : 365;
            if (result.Count < expected - 5)
            {
                error = "資料不完整（只有 " + result.Count + " 天，應該有 " + expected + " 天）";
                return null;
            }
            return result.OrderBy(e => e.Date).ToList();
        }

        // 依勾選的項目，把每天的資料變成事件
        public static List<HolidayEvent> BuildEvents(IEnumerable<HolidayEntry> entries, HolidayOptions options)
        {
            var events = new List<HolidayEvent>();
            foreach (var e in entries.OrderBy(x => x.Date))
            {
                HolidayEvent ev = null;
                if (!e.IsHoliday && e.IsWeekend)
                {
                    // 本來是週末卻不放假：補班日
                    if (options.MakeUpDays) ev = new HolidayEvent { Kind = HolidayKind.MakeUp, Title = "補班日", Note = e.Description };
                }
                else if (e.IsHoliday && e.Description.Length == 0 && e.IsWeekend)
                {
                    if (options.Weekends) ev = new HolidayEvent { Kind = HolidayKind.Weekend, Title = e.Date.DayOfWeek == DayOfWeek.Saturday ? "週六" : "週日" };
                }
                else if (e.IsHoliday)
                {
                    // 有名稱的放假日（含平日的補假、連假）；平日放假卻沒有名稱的（不常見），標成「放假」
                    if (options.Holidays) ev = new HolidayEvent { Kind = HolidayKind.Holiday, Title = e.Description.Length > 0 ? e.Description : "放假", Note = e.Description };
                }
                if (ev == null) continue;

                ev.Start = e.Date;
                ev.EndExclusive = e.Date.AddDays(1);

                var last = events.Count > 0 ? events[events.Count - 1] : null;
                if (options.Merge && last != null && ev.Kind == HolidayKind.Holiday && last.Kind == HolidayKind.Holiday && last.Title == ev.Title && last.EndExclusive == ev.Start)
                    last.EndExclusive = ev.EndExclusive;   // 相同名稱、日期相連：合併成一筆多日行程
                else
                    events.Add(ev);
            }
            return events;
        }

        // 產生 ICS 文字（CRLF 換行、每行最多 75 位元組）。事件都是整天事件，標成「有空」（不會讓你在 Google 日曆上顯示忙碌）
        public static string BuildIcs(IList<HolidayEvent> events, string calendarName, bool alarm, DateTime nowUtc)
        {
            var lines = new List<string>();
            lines.Add("BEGIN:VCALENDAR");
            lines.Add("VERSION:2.0");
            lines.Add("PRODID:-//CalendarWidget//Taiwan Holidays//ZH");
            lines.Add("CALSCALE:GREGORIAN");
            lines.Add("METHOD:PUBLISH");
            lines.Add("X-WR-CALNAME:" + Escape(calendarName));
            lines.Add("X-WR-TIMEZONE:Asia/Taipei");

            string stamp = nowUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            foreach (var ev in events)
            {
                lines.Add("BEGIN:VEVENT");
                lines.Add("UID:tw-" + ev.Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + ev.Kind.ToString().ToLowerInvariant() + "@calendarwidget");
                lines.Add("DTSTAMP:" + stamp);
                lines.Add("DTSTART;VALUE=DATE:" + ev.Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                lines.Add("DTEND;VALUE=DATE:" + ev.EndExclusive.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                lines.Add("SUMMARY:" + Escape(ev.Title));
                if (!string.IsNullOrEmpty(ev.Note) && ev.Note != ev.Title) lines.Add("DESCRIPTION:" + Escape(ev.Note));
                lines.Add("TRANSP:TRANSPARENT");
                if (alarm && ev.Kind != HolidayKind.Weekend)
                {
                    // 整天事件從當天 00:00 算起，往前 15 小時＝前一天 09:00
                    lines.Add("BEGIN:VALARM");
                    lines.Add("ACTION:DISPLAY");
                    lines.Add("DESCRIPTION:" + Escape(ev.Title));
                    lines.Add("TRIGGER:-PT15H");
                    lines.Add("END:VALARM");
                }
                lines.Add("END:VEVENT");
            }
            lines.Add("END:VCALENDAR");

            var sb = new StringBuilder();
            foreach (string line in lines) sb.Append(Fold(line)).Append("\r\n");
            return sb.ToString();
        }

        private static string Escape(string text)
        {
            return (text ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
        }

        // ICS 規定每行不超過 75 位元組（UTF-8），超過的在下一行開頭加一個空白接續；中文字不能被切在中間
        private static string Fold(string line)
        {
            if (Encoding.UTF8.GetByteCount(line) <= 75) return line;

            var sb = new StringBuilder();
            int bytes = 0;
            int limit = 75;
            for (int i = 0; i < line.Length; i++)
            {
                int len = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? Encoding.UTF8.GetByteCount(line.Substring(i, 2)) : Encoding.UTF8.GetByteCount(line[i].ToString());
                if (bytes + len > limit)
                {
                    sb.Append("\r\n ");
                    bytes = 1;   // 接續行開頭的空白佔 1 位元組
                    limit = 75;
                }
                if (len == 4) { sb.Append(line[i]).Append(line[i + 1]); i++; }
                else sb.Append(line[i]);
                bytes += len;
            }
            return sb.ToString();
        }
    }
}

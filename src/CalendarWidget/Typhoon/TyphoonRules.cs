using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CalendarWidget
{
    internal static class TyphoonCities
    {
        // 設定畫面的順序：北、中、南、東、離島
        public static readonly string[] All =
        {
            "臺北市", "新北市", "基隆市", "桃園市", "新竹市", "新竹縣",
            "苗栗縣", "臺中市", "彰化縣", "南投縣", "雲林縣",
            "嘉義市", "嘉義縣", "臺南市", "高雄市", "屏東縣",
            "宜蘭縣", "花蓮縣", "臺東縣",
            "澎湖縣", "金門縣", "連江縣"
        };

        // 「台」「臺」視為相同；拿掉空白
        public static string Normalize(string text)
        {
            return (text ?? "").Replace('台', '臺').Replace(" ", "").Replace("　", "").Trim();
        }
    }

    // 使用者要關注的縣市，以及每個縣市要另外通知的鄉鎮區。
    // 鄉鎮區留空＝不通知鄉鎮區，只有整個縣市停班停課才通知
    internal sealed class TyphoonWatch
    {
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, List<string>> _areas = new Dictionary<string, List<string>>();

        public IList<string> Cities { get { return _order; } }

        public bool Contains(string city)
        {
            return _areas.ContainsKey(TyphoonCities.Normalize(city));
        }

        public IList<string> AreasOf(string city)
        {
            List<string> list;
            return _areas.TryGetValue(TyphoonCities.Normalize(city), out list) ? list : new List<string>();
        }

        // 加入（或更新）一個縣市；不是台灣的縣市名稱就略過，回傳有沒有加進去
        public bool Set(string city, IEnumerable<string> areas)
        {
            string name = TyphoonCities.Normalize(city);
            if (Array.IndexOf(TyphoonCities.All, name) < 0) return false;
            if (!_areas.ContainsKey(name)) _order.Add(name);
            _areas[name] = areas.Select(TyphoonCities.Normalize).Where(a => a.Length > 0).Distinct().ToList();
            return true;
        }

        // 使用者輸入的鄉鎮區：用頓號、逗號、分號或空白分開，「台」「臺」不分
        public static List<string> SplitAreas(string input)
        {
            var parts = (input ?? "").Split(new[] { '、', ',', '，', ';', '；', ' ', '　', '\t', '\r', '\n', '|', ':', '：' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Select(TyphoonCities.Normalize).Where(a => a.Length > 0).Distinct().ToList();
        }

        // 存在 settings.ini 的格式：高雄市:前鎮區、小港區|臺北市:
        public string Serialize()
        {
            return string.Join("|", _order.Select(c => c + ":" + string.Join("、", _areas[c])));
        }

        public static TyphoonWatch Parse(string text)
        {
            var watch = new TyphoonWatch();
            foreach (string entry in (text ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = entry.IndexOf(':');
                string city = colon < 0 ? entry : entry.Substring(0, colon);
                string areas = colon < 0 ? "" : entry.Substring(colon + 1);
                watch.Set(city, SplitAreas(areas));
            }
            return watch;
        }
    }

    // 要通知使用者的一則公告（已經依使用者關注的縣市、鄉鎮區篩選過）
    internal sealed class TyphoonAlert
    {
        public string City = "";
        public string Scope = "";
        public string ScopeKind = "city";
        public DateTime Date;
        public bool Work;
        public bool School;
        public bool Unknown;     // 看不懂的公告：要請使用者自己看人事總處的網頁
        public string Period = "";
        public string From = "";
        public string Text = "";

        private static readonly Dictionary<string, string> PeriodNames = new Dictionary<string, string>
        {
            { "morning", "上午" }, { "noon", "中午" }, { "afternoon", "下午" }, { "evening", "晚上" }
        };

        // 「確認」的紀錄用日期加一段文字當 key（跟節日提醒共用同一種紀錄檔）
        public string AckTitle { get { return "颱風|" + City + "|" + Scope + "|" + Text; } }

        public string DayLabel(DateTime today)
        {
            int days = (int)(Date.Date - today.Date).TotalDays;
            string md = Date.Month + "/" + Date.Day;
            if (days == 0) return "今天（" + md + "）";
            if (days == 1) return "明天（" + md + "）";
            if (days == 2) return "後天（" + md + "）";
            return md;
        }

        // 提醒框裡的一行，例如「高雄市 明天（10/30）停止上班、停止上課」
        public string Line(DateTime today)
        {
            if (Unknown)
                return (City.Length > 0 ? City + " " : "") + Text.TrimEnd('。') + "（程式看不懂這則公告的格式，請自行查看人事總處的網頁）";

            var sb = new StringBuilder(City);
            if (Scope.Length > 0) sb.Append(' ').Append(Scope).Append(ScopeKind == "area" ? "（部分地區）" : "");
            sb.Append(' ').Append(DayLabel(today)).Append(Remainder());
            return sb.ToString();
        }

        // 原文裡日期後面的部分（"停止上班、停止上課"）；沒有原文就用欄位組出來
        private string Remainder()
        {
            string md = Date.Month + "/" + Date.Day;
            string rest = Text.StartsWith(md, StringComparison.Ordinal) ? Text.Substring(md.Length) : Text;
            rest = rest.TrimEnd('。');
            if (rest.Length > 0) return rest;
            return PeriodText() + What();
        }

        private string What()
        {
            return Work && School ? "停班停課" : Work ? "停班" : "停課";
        }

        private string PeriodText()
        {
            string name;
            string period = PeriodNames.TryGetValue(Period, out name) ? name : "";
            return period + (From.Length > 0 ? From + "起" : "");
        }

        // 日期標題後面的簡短說明，例如「高雄市 停班停課」「高雄市那瑪夏區 下午13:00起停班停課」
        public string Short()
        {
            return City + Scope + " " + PeriodText() + What();
        }
    }

    // 從爬蟲的資料裡，挑出使用者要關注的公告
    internal static class TyphoonMatcher
    {
        public const int ShowDays = 2;   // 今天起往後幾天內的公告才算數（頁面上只會有今天、明天，多留一天）
        public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(8);

        public static List<TyphoonAlert> Select(TyphoonData data, TyphoonWatch watch, DateTime today)
        {
            var alerts = new List<TyphoonAlert>();
            if (data == null || watch.Cities.Count == 0) return alerts;

            foreach (string city in watch.Cities)
            {
                List<TyphoonItem> items;
                if (!data.Cities.TryGetValue(city, out items)) continue;
                foreach (var item in items)
                {
                    if (item.Unknown)
                    {
                        // 看不懂的句子沒有日期，當作今天的公告（隔天如果還在，會再提醒一次）
                        alerts.Add(new TyphoonAlert { City = city, Date = today.Date, Unknown = true, Text = item.Text, Work = true, School = true });
                        continue;
                    }
                    if (!item.Work && !item.School) continue;
                    if (item.Date < today.Date || item.Date > today.Date.AddDays(ShowDays)) continue;
                    if (item.ScopeKind == "school") continue;   // 單一學校不通知
                    if (item.ScopeKind == "area" && !AreaWatched(watch, city, item.Scope)) continue;
                    alerts.Add(new TyphoonAlert
                    {
                        City = city, Scope = item.Scope, ScopeKind = item.ScopeKind, Date = item.Date,
                        Work = item.Work, School = item.School, Period = item.Period, From = item.From, Text = item.Text
                    });
                }
            }

            // 頁面有內容但連縣市都看不懂：有關注縣市的人一律提醒，不能漏報
            if (data.State == "unparsed" && data.UnparsedText.Length > 0 && !data.Cities.Keys.Any(watch.Contains))
            {
                alerts.Add(new TyphoonAlert { Date = today.Date, Unknown = true, Text = "人事行政總處的頁面有停班停課訊息，但程式看不懂", Work = true, School = true });
            }

            return alerts.OrderBy(a => a.Date).ThenBy(a => a.City, StringComparer.Ordinal).ThenBy(a => a.Scope, StringComparer.Ordinal).ToList();
        }

        // 公告的範圍是不是使用者選的鄉鎮區。標準區名（設定畫面勾選的）先把範圍對應到它指的區：
        // 「秀林鄉大天祥地區」算秀林鄉、「桃源區拉芙蘭里」算桃源區、「安南區」不會被當成「南區」。
        // 不是標準區名的項目（以前手動輸入的）照原本的「包含」比對，才不會悄悄失效
        private static bool AreaWatched(TyphoonWatch watch, string city, string scope)
        {
            var districts = TyphoonDistricts.Match(city, scope);
            string normalized = TyphoonCities.Normalize(scope);
            foreach (string area in watch.AreasOf(city))
            {
                if (TyphoonDistricts.IsStandard(city, area))
                {
                    if (districts.Contains(area)) return true;
                }
                else if (normalized.Contains(area)) return true;
            }
            return false;
        }

        // 資料太久沒更新（爬蟲可能停了或被擋）
        public static bool IsStale(TyphoonData data, DateTime nowUtc)
        {
            return data == null || !data.CheckedAtUtc.HasValue || nowUtc - data.CheckedAtUtc.Value > StaleAfter;
        }

        // 這天要不要標紅：有關注的縣市（或鄉鎮區）在這天停班或停課
        public static bool IsOff(IEnumerable<TyphoonAlert> alerts, DateTime date)
        {
            return alerts.Any(a => !a.Unknown && a.Date.Date == date.Date && (a.Work || a.School));
        }

        public static string Describe(IEnumerable<TyphoonAlert> alerts, DateTime date)
        {
            var today = alerts.Where(a => !a.Unknown && a.Date.Date == date.Date).Select(a => a.Short()).ToList();
            return today.Count == 0 ? null : "颱風假：" + string.Join("、", today);
        }
    }
}

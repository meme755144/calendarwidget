using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CalendarWidget
{
    // 一筆停班停課公告（來自 taiwan-typhoon-days 這個 repo 的 data/latest.json 的 cities / schools 項目）
    internal sealed class TyphoonItem
    {
        public DateTime Date;
        public string Scope = "";        // 空的是整個縣市；有寫的是鄉鎮區、村里或學校
        public string ScopeKind = "city";   // city / area / school
        public bool Work;                // 停止上班
        public bool School;              // 停止上課
        public string Period = "";       // 空＝全天；morning / noon / afternoon / evening
        public string From = "";         // 從幾點開始，例如 "13:00"；沒有就是空字串
        public string Text = "";         // 原文，日期已換成 "M/d"，例如 "10/30停止上班、停止上課。"
        public bool Unknown;             // 爬蟲看不懂的句子：Text 是原文，要當成「有公告，請自行查看」
    }

    internal sealed class TyphoonError
    {
        public int Status;
        public string Message = "";
        public DateTime? BackoffUntil;
    }

    // data/latest.json 的內容。格式說明在 taiwan-typhoon-days 的 README；這裡只取小工具用得到的欄位
    internal sealed class TyphoonData
    {
        public int Schema;
        public string State = "none";   // none / announced / unparsed
        public DateTime? CheckedAtUtc;   // 爬蟲最近一次寫檔的時間
        public DateTime? PageDate;
        public string UnparsedText = "";
        public Dictionary<string, List<TyphoonItem>> Cities = new Dictionary<string, List<TyphoonItem>>();
        public Dictionary<string, List<TyphoonItem>> Schools = new Dictionary<string, List<TyphoonItem>>();
        public TyphoonError Error;

        // 解析 latest.json。內容不是這個格式（不是物件、沒有 state、cities 不是物件）就回傳 null，並說明原因
        public static TyphoonData Parse(string json, out string problem)
        {
            problem = null;
            Dictionary<string, object> root;
            try { root = Json.ParseObject(json); }
            catch (ArgumentException) { root = null; }
            catch (InvalidOperationException) { root = null; }
            if (root == null)
            {
                problem = "內容不是 JSON 物件";
                return null;
            }

            string state = Json.Str(root, "state");
            if (state == null)
            {
                problem = "缺少 state 欄位（不是停班停課資料）";
                return null;
            }
            if (root.ContainsKey("cities") && Json.Obj(root, "cities") == null)
            {
                problem = "cities 欄位的格式不對";
                return null;
            }

            var data = new TyphoonData();
            int schema;
            if (int.TryParse(Json.Str(root, "schema") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out schema)) data.Schema = schema;
            data.State = state;
            data.UnparsedText = Json.Str(root, "unparsedText") ?? "";
            data.CheckedAtUtc = ParseTime(Json.Str(root, "checkedAt"));
            data.PageDate = ParseDate(Json.Str(root, "pageDate"));
            data.Cities = ReadGroups(Json.Obj(root, "cities"));
            data.Schools = ReadGroups(Json.Obj(root, "schools"));   // 舊格式（schema 1）的 schools 是數字，讀不出項目，自然略過

            var error = Json.Obj(root, "error");
            if (error != null)
            {
                int status;
                int.TryParse(Json.Str(error, "status") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out status);
                data.Error = new TyphoonError
                {
                    Status = status,
                    Message = Json.Str(error, "message") ?? "",
                    BackoffUntil = ParseTime(Json.Str(error, "backoffUntil"))
                };
            }
            return data;
        }

        // 內容的指紋：爬蟲只是更新檢查時間（checkedAt）時指紋不變，小工具就不需要重畫或重新提醒。
        // 不含 schools（單一學校不通知、也不顯示）
        public string Signature()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(State).Append('#').Append(UnparsedText).Append('#').Append(Error == null ? "" : Error.Message).Append('#');
            foreach (string city in Cities.Keys.OrderBy(c => c, StringComparer.Ordinal))
            {
                foreach (var i in Cities[city])
                {
                    sb.Append(city).Append('|').Append(i.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append('|').Append(i.Scope).Append('|').Append(i.ScopeKind)
                      .Append('|').Append(i.Work ? 1 : 0).Append(i.School ? 1 : 0).Append(i.Unknown ? 1 : 0).Append('|').Append(i.Period).Append('|').Append(i.From).Append('|').Append(i.Text).Append('\n');
                }
            }
            return sb.ToString();
        }

        private static Dictionary<string, List<TyphoonItem>> ReadGroups(Dictionary<string, object> groups)
        {
            var result = new Dictionary<string, List<TyphoonItem>>();
            if (groups == null) return result;
            foreach (var pair in groups)
            {
                var list = new List<TyphoonItem>();
                var entries = pair.Value as IEnumerable;
                if (entries == null || pair.Value is string) continue;
                foreach (object entry in entries)
                {
                    var obj = entry as Dictionary<string, object>;
                    if (obj == null) continue;
                    var item = ReadItem(obj);
                    if (item != null) list.Add(item);
                }
                if (list.Count > 0) result[pair.Key] = list;
            }
            return result;
        }

        private static TyphoonItem ReadItem(Dictionary<string, object> obj)
        {
            if (Json.Bool(obj, "unknown"))
                return new TyphoonItem { Unknown = true, Text = Json.Str(obj, "text") ?? "", Work = true, School = true };

            DateTime? date = ParseDate(Json.Str(obj, "date"));
            if (!date.HasValue) return null;   // 沒有日期就無從判斷是哪一天，略過這一筆
            return new TyphoonItem
            {
                Date = date.Value,
                Scope = Json.Str(obj, "scope") ?? "",
                ScopeKind = Json.Str(obj, "scopeKind") ?? "city",
                Work = Json.Bool(obj, "work"),
                School = Json.Bool(obj, "school"),
                Period = Json.Str(obj, "period") ?? "",
                From = Json.Str(obj, "from") ?? "",
                Text = Json.Str(obj, "text") ?? ""
            };
        }

        private static DateTime? ParseDate(string s)
        {
            DateTime d;
            return DateTime.TryParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null;
        }

        private static DateTime? ParseTime(string s)
        {
            DateTime t;
            if (!DateTime.TryParse(s ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return null;
            return t;
        }
    }
}

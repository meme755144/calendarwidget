using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace CalendarWidget
{
    // 讀 Google API 回傳的 JSON 用的小工具（.NET Framework 內建的 JavaScriptSerializer，不需要額外套件）
    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static Dictionary<string, object> ParseObject(string text)
        {
            return Serializer.DeserializeObject(text) as Dictionary<string, object>;
        }

        // 最外層是陣列的 JSON：[{...}, {...}]；不是陣列就回傳 null，陣列裡不是物件的項目略過
        public static List<Dictionary<string, object>> ParseList(string text)
        {
            object parsed;
            try { parsed = Serializer.DeserializeObject(text); }
            catch (System.ArgumentException) { return null; }
            catch (System.InvalidOperationException) { return null; }

            var items = parsed as IEnumerable;
            if (items == null || parsed is string || parsed is IDictionary) return null;
            var result = new List<Dictionary<string, object>>();
            foreach (object item in items)
            {
                var d = item as Dictionary<string, object>;
                if (d != null) result.Add(d);
            }
            return result;
        }

        public static string Str(Dictionary<string, object> obj, string key)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }

        public static bool Bool(Dictionary<string, object> obj, string key)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> obj, string key)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<Dictionary<string, object>> List(Dictionary<string, object> obj, string key)
        {
            var result = new List<Dictionary<string, object>>();
            object v;
            if (obj == null || !obj.TryGetValue(key, out v)) return result;
            var items = v as IEnumerable;
            if (items == null) return result;
            foreach (object item in items)
            {
                var d = item as Dictionary<string, object>;
                if (d != null) result.Add(d);
            }
            return result;
        }
    }
}

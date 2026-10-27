using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CalendarWidget
{
    // Google Calendar API 與 Google Tasks API。讀取只用唯讀授權；新增／刪除行程要另外的「寫入」授權。所有請求都在背景執行緒呼叫
    internal sealed class GoogleClient
    {
        private readonly GoogleAuth _auth;

        public GoogleClient(GoogleAuth auth)
        {
            _auth = auth;
        }

        // ---------- 日曆清單 ----------

        public List<GoogleCalendarEntry> ListCalendars(out string primaryEmail)
        {
            primaryEmail = null;
            var result = new List<GoogleCalendarEntry>();
            string pageToken = null;
            do
            {
                string url = GoogleEndpoints.Calendar + "/users/me/calendarList?maxResults=250&fields=" +
                             Uri.EscapeDataString("nextPageToken,items(id,summary,summaryOverride,backgroundColor,selected,primary,accessRole)") +
                             (pageToken != null ? "&pageToken=" + Uri.EscapeDataString(pageToken) : "");
                var page = Get(url);

                foreach (var item in Json.List(page, "items"))
                {
                    string id = Json.Str(item, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    if (Json.Bool(item, "primary")) primaryEmail = id;

                    string color = Json.Str(item, "backgroundColor");
                    result.Add(new GoogleCalendarEntry
                    {
                        Id = id,
                        Name = Json.Str(item, "summaryOverride") ?? Json.Str(item, "summary") ?? id,
                        Color = Colors.IsValidHex(color) ? color : "#6CB4FF",
                        // Google 上的「節日」日曆網址都是這個結尾，預設就當成節日
                        Kind = id.EndsWith("#holiday@group.v.calendar.google.com", StringComparison.OrdinalIgnoreCase) ? ItemKind.Holiday : ItemKind.Event,
                        // 預設只勾選你在 Google 日曆裡本來就有顯示的日曆
                        Enabled = Json.Bool(item, "selected") || Json.Bool(item, "primary"),
                        // 擁有者或可編輯的才能新增行程；只能讀的（節日日曆、別人分享給你唯讀的）不行
                        CanWrite = IsWritableRole(Json.Str(item, "accessRole")) ? 1 : (Json.Str(item, "accessRole") == null ? -1 : 0)
                    });
                }
                pageToken = Json.Str(page, "nextPageToken");
            } while (pageToken != null);
            return result;
        }

        internal static bool IsWritableRole(string role)
        {
            return role == "owner" || role == "writer";
        }

        // ---------- 新增行程（需要「寫入」授權）----------

        // 在指定的 Google 日曆新增一個行程。提醒用該日曆的預設提醒（useDefault）。回傳新行程的 id
        // 整天行程的 end 只看日期，是最後一天（含）；單日就跟 start 同一天
        public string CreateEvent(string calendarId, string title, bool allDay, DateTime start, DateTime end)
        {
            var body = new Dictionary<string, object>();
            body["summary"] = title;
            if (allDay)
            {
                // 整天行程：end 的日期是「最後一天」（含），送給 Google 的結束日要再加一天（Google 的結束日不含）
                body["start"] = new Dictionary<string, object> { { "date", start.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } };
                body["end"] = new Dictionary<string, object> { { "date", end.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } };
            }
            else
            {
                body["start"] = new Dictionary<string, object> { { "dateTime", Rfc3339(start) } };
                body["end"] = new Dictionary<string, object> { { "dateTime", Rfc3339(end) } };
            }
            body["reminders"] = new Dictionary<string, object> { { "useDefault", true } };

            string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(body);
            string url = GoogleEndpoints.Calendar + "/calendars/" + Uri.EscapeDataString(calendarId) + "/events?fields=id";

            for (int attempt = 0; ; attempt++)
            {
                var result = GoogleHttp.Send("POST", url, _auth.AccessToken(), json, "application/json; charset=utf-8");
                if (result.Status == 401 && attempt == 0)
                {
                    _auth.Invalidate();
                    continue;
                }
                if (result.Status == 200 || result.Status == 201)
                {
                    var created = GoogleAuth.SafeParse(result.Body);
                    return Json.Str(created, "id") ?? "";
                }

                var error = Json.Obj(GoogleAuth.SafeParse(result.Body), "error");
                string message = Json.Str(error, "message") ?? "HTTP " + result.Status;
                bool insufficient = result.Status == 403 && (result.Body ?? "").IndexOf("insufficient", StringComparison.OrdinalIgnoreCase) >= 0;
                if (insufficient)
                    throw new GoogleException("還沒有授權這個程式新增行程", false, true);
                if (result.Status == 403 || result.Status == 404)
                    throw new GoogleException("無法新增到這個日曆（你可能沒有編輯權限）：" + message, false);
                throw new GoogleException("Google：" + message + "（HTTP " + result.Status + "）", false);
            }
        }

        // ---------- 修改行程（需要「寫入」授權）----------

        // 只改標題與時間（PATCH：沒送的欄位，例如提醒、說明、參加者，Google 都保持原樣）。
        // 重複行程傳進來的是「那一次」的 id，只會改那一次。整天與有時間互換時，要把另一種欄位設成 null 清掉
        public void UpdateEvent(string calendarId, string eventId, string title, bool allDay, DateTime start, DateTime end)
        {
            var body = new Dictionary<string, object>();
            body["summary"] = title;
            if (allDay)
            {
                body["start"] = new Dictionary<string, object> { { "date", start.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "dateTime", null } };
                body["end"] = new Dictionary<string, object> { { "date", end.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "dateTime", null } };
            }
            else
            {
                body["start"] = new Dictionary<string, object> { { "dateTime", Rfc3339(start) }, { "date", null } };
                body["end"] = new Dictionary<string, object> { { "dateTime", Rfc3339(end) }, { "date", null } };
            }

            string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(body);
            string url = GoogleEndpoints.Calendar + "/calendars/" + Uri.EscapeDataString(calendarId) + "/events/" + Uri.EscapeDataString(eventId) + "?fields=id";

            for (int attempt = 0; ; attempt++)
            {
                var result = GoogleHttp.Send("PATCH", url, _auth.AccessToken(), json, "application/json; charset=utf-8");
                if (result.Status == 401 && attempt == 0)
                {
                    _auth.Invalidate();
                    continue;
                }
                if (result.Status == 200) return;

                var error = Json.Obj(GoogleAuth.SafeParse(result.Body), "error");
                string message = Json.Str(error, "message") ?? "HTTP " + result.Status;
                bool insufficient = result.Status == 403 && (result.Body ?? "").IndexOf("insufficient", StringComparison.OrdinalIgnoreCase) >= 0;
                if (insufficient)
                    throw new GoogleException("還沒有授權這個程式修改行程", false, true);
                if (result.Status == 404 || result.Status == 410)
                    throw new GoogleException("找不到這個行程，它可能已經在別處被刪除了", false);
                if (result.Status == 403)
                    throw new GoogleException("無法修改這個行程（你可能沒有編輯權限）：" + message, false);
                throw new GoogleException("Google：" + message + "（HTTP " + result.Status + "）", false);
            }
        }

        // ---------- 刪除行程（需要「寫入」授權）----------

        // 刪除指定的 Google 行程。重複行程傳進來的是「那一次」的 id，只會刪掉那一次。已經不存在（404/410）算成功
        public void DeleteEvent(string calendarId, string eventId)
        {
            string url = GoogleEndpoints.Calendar + "/calendars/" + Uri.EscapeDataString(calendarId) + "/events/" + Uri.EscapeDataString(eventId);

            for (int attempt = 0; ; attempt++)
            {
                var result = GoogleHttp.Send("DELETE", url, _auth.AccessToken(), null, null);
                if (result.Status == 401 && attempt == 0)
                {
                    _auth.Invalidate();
                    continue;
                }
                if (result.Status == 200 || result.Status == 204 || result.Status == 404 || result.Status == 410) return;

                var error = Json.Obj(GoogleAuth.SafeParse(result.Body), "error");
                string message = Json.Str(error, "message") ?? "HTTP " + result.Status;
                bool insufficient = result.Status == 403 && (result.Body ?? "").IndexOf("insufficient", StringComparison.OrdinalIgnoreCase) >= 0;
                if (insufficient)
                    throw new GoogleException("還沒有授權這個程式修改行程", false, true);
                if (result.Status == 403)
                    throw new GoogleException("無法刪除這個行程（你可能沒有編輯權限）：" + message, false);
                throw new GoogleException("Google：" + message + "（HTTP " + result.Status + "）", false);
            }
        }

        private static string Rfc3339(DateTime local)
        {
            DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            var offset = new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
            return offset.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        // ---------- 行程 ----------

        // singleEvents=true：由 Google 把重複事件展開成一筆一筆，不用自己算
        public List<CalendarItem> FetchEvents(string calendarId, string color, ItemKind kind, DateTime from, DateTime to)
        {
            var items = new List<CalendarItem>();
            string pageToken = null;
            do
            {
                string url = GoogleEndpoints.Calendar + "/calendars/" + Uri.EscapeDataString(calendarId) + "/events" +
                             "?singleEvents=true&showDeleted=false&maxResults=2500&orderBy=startTime" +
                             "&timeMin=" + Uri.EscapeDataString(Utc(from.Date)) +
                             "&timeMax=" + Uri.EscapeDataString(Utc(to.Date.AddDays(1))) +
                             "&fields=" + Uri.EscapeDataString("nextPageToken,defaultReminders(method,minutes),items(id,recurringEventId,summary,status,start,end,reminders(useDefault,overrides(method,minutes)))") +
                             (pageToken != null ? "&pageToken=" + Uri.EscapeDataString(pageToken) : "");
                var page = Get(url);

                // 這個日曆的預設提醒（行程沒有自己的設定、useDefault 為 true 時用這個）
                int[] defaults = PopupMinutes(Json.List(page, "defaultReminders"));
                foreach (var ev in Json.List(page, "items"))
                {
                    if (Json.Str(ev, "status") == "cancelled") continue;
                    AddEvent(items, ev, color, kind, from, to, defaults);
                }
                pageToken = Json.Str(page, "nextPageToken");
            } while (pageToken != null);
            return items;
        }

        // 提醒清單裡，只留「跳出通知」的（popup）；寄信（email）之類不是桌面上的提醒，不算
        internal static int[] PopupMinutes(List<Dictionary<string, object>> reminders)
        {
            var minutes = new List<int>();
            foreach (var r in reminders)
            {
                int m;
                if (Json.Str(r, "method") == "popup" && int.TryParse(Json.Str(r, "minutes"), out m) && m >= 0) minutes.Add(m);
            }
            return minutes.Distinct().OrderBy(m => m).ToArray();
        }

        // 這個行程實際要用的提醒：自己有設定就用自己的，否則（useDefault）用日曆的預設；沒有任何資料就是沒有提醒
        internal static int[] EventReminders(Dictionary<string, object> ev, int[] calendarDefaults)
        {
            var reminders = Json.Obj(ev, "reminders");
            if (reminders == null) return null;

            int[] result = Json.Bool(reminders, "useDefault") ? calendarDefaults : PopupMinutes(Json.List(reminders, "overrides"));
            return result != null && result.Length > 0 ? result : null;
        }

        internal static void AddEvent(List<CalendarItem> items, Dictionary<string, object> ev, string color, ItemKind kind, DateTime from, DateTime to, int[] calendarDefaults)
        {
            int[] reminders = EventReminders(ev, calendarDefaults);
            string eventId = Json.Str(ev, "id");
            if (string.IsNullOrEmpty(eventId)) eventId = null;
            bool recurring = Json.Str(ev, "recurringEventId") != null;
            string title = Json.Str(ev, "summary");
            if (string.IsNullOrWhiteSpace(title)) title = "（無標題）";
            title = title.Replace('\r', ' ').Replace('\n', ' ').Trim();

            var start = Json.Obj(ev, "start");
            var end = Json.Obj(ev, "end");
            string startDate = Json.Str(start, "date");
            DateTime d;

            if (startDate != null)
            {
                // 整天事件：end.date 是「結束日的隔天」
                if (!DateTime.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return;
                int days = 1;
                DateTime endD;
                if (DateTime.TryParseExact(Json.Str(end, "date") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out endD))
                    days = Math.Max(1, Math.Min(60, (int)(endD - d).TotalDays));
                else
                    endD = d.AddDays(1);
                for (int i = 0; i < days; i++)
                {
                    DateTime day = d.AddDays(i);
                    if (day < from.Date || day > to.Date) continue;
                    items.Add(new CalendarItem
                    {
                        Id = eventId,
                        Recurring = recurring,
                        Date = day,
                        Title = title,
                        Kind = kind,
                        Color = color,
                        AllDay = true,
                        End = endD,
                        // 跨很多天的整天事件，提醒只算在第一天；整天事件的提醒是「從當天 00:00 往前算」
                        Start = i == 0 ? (DateTime?)day : null,
                        Reminders = i == 0 ? reminders : null
                    });
                }
                return;
            }

            DateTimeOffset dto;
            if (!DateTimeOffset.TryParse(Json.Str(start, "dateTime") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out dto)) return;
            DateTime local = dto.ToLocalTime().DateTime;
            if (local.Date < from.Date || local.Date > to.Date) return;
            DateTimeOffset endDto;
            DateTime? endLocal = DateTimeOffset.TryParse(Json.Str(end, "dateTime") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out endDto)
                ? (DateTime?)endDto.ToLocalTime().DateTime : null;
            items.Add(new CalendarItem
            {
                End = endLocal,
                Id = eventId,
                Recurring = recurring,
                Date = local.Date,
                Title = title,
                TimeText = local.ToString("HH:mm", CultureInfo.InvariantCulture),
                Kind = kind,
                Color = color,
                Start = local,
                Reminders = reminders
            });
        }

        // ---------- Google 待辦 ----------

        // 只顯示有「到期日」的待辦（Google 待辦的到期日只有日期，沒有時間）
        public List<CalendarItem> FetchTasks(string color, DateTime from, DateTime to)
        {
            var items = new List<CalendarItem>();
            var lists = Get(GoogleEndpoints.Tasks + "/users/@me/lists?maxResults=100&fields=" + Uri.EscapeDataString("items(id)"));
            foreach (var list in Json.List(lists, "items"))
            {
                string listId = Json.Str(list, "id");
                if (string.IsNullOrEmpty(listId)) continue;

                string pageToken = null;
                do
                {
                    string url = GoogleEndpoints.Tasks + "/lists/" + Uri.EscapeDataString(listId) + "/tasks" +
                                 "?showCompleted=true&showHidden=true&maxResults=100" +
                                 "&dueMin=" + Uri.EscapeDataString(Utc(from.Date)) +
                                 "&dueMax=" + Uri.EscapeDataString(Utc(to.Date.AddDays(1))) +
                                 "&fields=" + Uri.EscapeDataString("nextPageToken,items(title,status,due)") +
                                 (pageToken != null ? "&pageToken=" + Uri.EscapeDataString(pageToken) : "");
                    var page = Get(url);

                    foreach (var task in Json.List(page, "items"))
                    {
                        string due = Json.Str(task, "due");
                        string title = Json.Str(task, "title");
                        DateTime d;
                        if (due == null || due.Length < 10 || string.IsNullOrWhiteSpace(title)) continue;
                        if (!DateTime.TryParseExact(due.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
                        if (d < from.Date || d > to.Date) continue;
                        items.Add(new CalendarItem
                        {
                            Date = d,
                            Title = title.Replace('\r', ' ').Replace('\n', ' ').Trim(),
                            Kind = ItemKind.GoogleTask,
                            Color = color,
                            Done = Json.Str(task, "status") == "completed"
                        });
                    }
                    pageToken = Json.Str(page, "nextPageToken");
                } while (pageToken != null);
            }
            return items;
        }

        // ---------- HTTP ----------

        private static string Utc(DateTime localDate)
        {
            return DateTime.SpecifyKind(localDate, DateTimeKind.Local).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private Dictionary<string, object> Get(string url)
        {
            for (int attempt = 0; ; attempt++)
            {
                var result = GoogleHttp.Send("GET", url, _auth.AccessToken(), null);

                if (result.Status == 401 && attempt == 0)
                {
                    _auth.Invalidate();   // 存取權杖可能剛好過期，換一個再試一次
                    continue;
                }
                if (result.Status == 200)
                {
                    var json = GoogleAuth.SafeParse(result.Body);
                    if (json == null) throw new GoogleException("Google 回傳的內容無法解析", false);
                    return json;
                }

                var error = Json.Obj(GoogleAuth.SafeParse(result.Body), "error");
                string message = Json.Str(error, "message") ?? "HTTP " + result.Status;
                throw new GoogleException("Google：" + message + "（HTTP " + result.Status + "）", result.Status == 401);
            }
        }
    }
}

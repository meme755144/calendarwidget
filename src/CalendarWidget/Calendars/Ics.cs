using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CalendarWidget
{
    // 不依賴任何套件的 ICS 解析：支援 DATE／UTC／TZID 時間、整天與跨日行程、
    // RRULE（DAILY／WEEKLY／MONTHLY／YEARLY + INTERVAL／COUNT／UNTIL／BYDAY／BYMONTHDAY／BYMONTH／BYSETPOS）、
    // EXDATE、RECURRENCE-ID（單次修改）、STATUS:CANCELLED
    internal static class IcsParser
    {
        private sealed class Prop
        {
            public string Name;
            public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Value;
        }

        private sealed class Ev
        {
            public string Uid = "";
            public string Summary = "";
            public bool AllDay;
            public DateTime StartWall;
            public TimeZoneInfo Zone;      // null = 使用電腦的時區（浮動時間或整天）
            public TimeSpan Duration;
            public int Days = 1;           // 整天事件的天數
            public string Rrule;
            public List<DateTime> ExDates = new List<DateTime>();
            public DateTime? RecurrenceKey;
            public bool Cancelled;
            public int[] Reminders;        // VALARM：開始前幾分鐘提醒
        }

        // 往前、往後各展開多少天（以今天為準，由呼叫端決定）
        public static List<CalendarItem> Parse(string text, DateTime rangeStart, DateTime rangeEnd, ItemKind kind, string color)
        {
            var events = ReadEvents(text);
            var result = new List<CalendarItem>();

            // 單次修改的事件：記錄它取代的是哪一次
            var overrides = new HashSet<string>(events.Where(e => e.RecurrenceKey.HasValue)
                .Select(e => e.Uid + "|" + e.RecurrenceKey.Value.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)));

            foreach (var ev in events)
            {
                if (ev.Cancelled) continue;

                // 一個事件的資料壞掉（日期超出範圍、規則怪異…）只略過那一個事件，不能讓整個日曆載入失敗
                int before = result.Count;
                try
                {
                    ExpandEvent(result, ev, overrides, rangeStart, rangeEnd, kind, color);
                }
                catch (ArgumentException) { result.RemoveRange(before, result.Count - before); }
                catch (OverflowException) { result.RemoveRange(before, result.Count - before); }
                catch (FormatException) { result.RemoveRange(before, result.Count - before); }
            }
            return result;
        }

        private static void ExpandEvent(List<CalendarItem> result, Ev ev, HashSet<string> overrides, DateTime rangeStart, DateTime rangeEnd, ItemKind kind, string color)
        {
            if (ev.RecurrenceKey.HasValue || string.IsNullOrEmpty(ev.Rrule))
            {
                AddOccurrence(result, ev, ev.StartWall, rangeStart, rangeEnd, kind, color);
                return;
            }

            var rule = RRule.Parse(ev.Rrule, ev);
            // 以事件自己的時區的「牆上時間」展開；多抓兩天避免時區換算造成邊界遺漏
            DateTime limit = rangeEnd.Date.AddDays(2);
            foreach (DateTime wall in rule.Expand(ev.StartWall, limit))
            {
                DateTime key = ev.AllDay ? wall.Date : ToLocal(wall, ev.Zone);
                if (ev.ExDates.Contains(key)) continue;
                if (overrides.Contains(ev.Uid + "|" + key.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture))) continue;
                AddOccurrence(result, ev, wall, rangeStart, rangeEnd, kind, color);
            }
        }

        private static void AddOccurrence(List<CalendarItem> result, Ev ev, DateTime wall, DateTime rangeStart, DateTime rangeEnd, ItemKind kind, string color)
        {
            if (ev.AllDay)
            {
                // 只走過落在範圍內的那幾天；跨幾十年的整天事件不用逐天跑完
                int firstDay = Math.Max(0, (int)Math.Min(int.MaxValue, (rangeStart.Date - wall.Date).TotalDays));
                int lastDay = (int)Math.Min(ev.Days - 1, (rangeEnd.Date - wall.Date).TotalDays);
                for (int i = firstDay; i <= lastDay; i++)
                {
                    DateTime d = wall.Date.AddDays(i);
                    if (d < rangeStart.Date || d > rangeEnd.Date) continue;
                    result.Add(new CalendarItem
                    {
                        Date = d,
                        Title = ev.Summary,
                        Kind = kind,
                        Color = color,
                        AllDay = true,
                        // 跨很多天的整天事件，提醒只算在第一天
                        Start = i == 0 ? (DateTime?)d : null,
                        Reminders = i == 0 ? ev.Reminders : null
                    });
                }
                return;
            }

            DateTime local = ToLocal(wall, ev.Zone);
            if (local.Date < rangeStart.Date || local.Date > rangeEnd.Date) return;
            result.Add(new CalendarItem
            {
                Date = local.Date,
                Title = ev.Summary,
                TimeText = local.ToString("HH:mm", CultureInfo.InvariantCulture),
                Kind = kind,
                Color = color,
                Start = local,
                Reminders = ev.Reminders
            });
        }

        // ---------- 讀取與欄位解析 ----------

        private static List<Ev> ReadEvents(string text)
        {
            var events = new List<Ev>();
            var props = new List<Prop>();
            var alarms = new List<int>();
            string alarmAction = null;
            Prop alarmTrigger = null;
            bool inEvent = false;
            int alarmDepth = 0;

            foreach (Prop p in ReadProps(text))
            {
                if (p.Name == "BEGIN")
                {
                    string v = p.Value.ToUpperInvariant();
                    if (v == "VEVENT") { inEvent = true; props = new List<Prop>(); alarms = new List<int>(); alarmDepth = 0; }
                    else if (inEvent && v == "VALARM") { alarmDepth++; alarmAction = null; alarmTrigger = null; }
                    continue;
                }
                if (p.Name == "END")
                {
                    string v = p.Value.ToUpperInvariant();
                    if (v == "VALARM" && alarmDepth > 0)
                    {
                        alarmDepth--;
                        int? minutes = ParseAlarm(alarmAction, alarmTrigger);
                        if (minutes.HasValue) alarms.Add(minutes.Value);
                        continue;
                    }
                    if (v == "VEVENT" && inEvent)
                    {
                        inEvent = false;
                        Ev ev = null;
                        try { ev = BuildEvent(props); }
                        catch (ArgumentException) { }   // 壞掉的事件略過，不影響其他事件
                        if (ev != null)
                        {
                            if (alarms.Count > 0) ev.Reminders = alarms.Distinct().OrderBy(m => m).ToArray();
                            events.Add(ev);
                        }
                    }
                    continue;
                }
                if (inEvent && alarmDepth > 0)
                {
                    // 提醒（VALARM）裡面的欄位只用來算提醒時間，不能影響行程本身（例如它也有 SUMMARY）
                    if (p.Name == "ACTION") alarmAction = p.Value;
                    else if (p.Name == "TRIGGER") alarmTrigger = p;
                    continue;
                }
                if (inEvent) props.Add(p);
            }
            return events;
        }

        // 一個 VALARM 換算成「開始前幾分鐘」；寄信的、開始之後才響的、相對於結束時間的、用絕對時間的，都不算
        private static int? ParseAlarm(string action, Prop trigger)
        {
            if (trigger == null) return null;
            if (action != null)
            {
                string a = action.Trim().ToUpperInvariant();
                if (a != "DISPLAY" && a != "AUDIO") return null;
            }

            string valueType, related;
            if (trigger.Params.TryGetValue("VALUE", out valueType) && valueType.ToUpperInvariant() == "DATE-TIME") return null;
            if (trigger.Params.TryGetValue("RELATED", out related) && related.ToUpperInvariant() == "END") return null;
            return ParseTriggerMinutes(trigger.Value);
        }

        // 提醒最多提前 28 天；跟 Settings.MaxEventReminderMinutes 同值（這個檔單獨被測試專案連結，所以不直接引用 Settings）
        private const int MaxTriggerMinutes = 40320;

        // "-PT15M" → 15、"-P1D" → 1440、"-P1DT2H" → 1560、"PT0S" → 0；開始「之後」的（正數）回傳 null
        internal static int? ParseTriggerMinutes(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim().ToUpperInvariant();

            bool negative = false;
            int i = 0;
            if (value[0] == '-') { negative = true; i = 1; }
            else if (value[0] == '+') i = 1;
            if (i >= value.Length || value[i] != 'P') return null;
            i++;

            double total = 0;
            bool inTime = false;
            bool anyUnit = false;
            var number = new StringBuilder();
            for (; i < value.Length; i++)
            {
                char c = value[i];
                if (c == 'T') { inTime = true; continue; }
                if (c >= '0' && c <= '9') { number.Append(c); if (number.Length > 9) return null; continue; }   // 只收半形數字；位數太多的一定是亂寫的
                if (number.Length == 0) return null;

                double n = double.Parse(number.ToString(), CultureInfo.InvariantCulture);
                number.Clear();
                anyUnit = true;
                switch (c)
                {
                    case 'W': total += n * 7 * 1440; break;
                    case 'D': total += n * 1440; break;
                    case 'H': if (!inTime) return null; total += n * 60; break;
                    case 'M': if (!inTime) return null; total += n; break;
                    case 'S': if (!inTime) return null; total += n / 60; break;
                    default: return null;
                }
            }
            if (number.Length > 0 || !anyUnit) return null;   // 數字後面沒有單位、或根本沒有任何單位（例如只有 "-P"）都是無效的
            if (!negative && total > 0) return null;   // 開始之後才響的提醒不處理
            if (total > MaxTriggerMinutes) return null;   // 提前超過 28 天的當作無效
            return (int)Math.Round(total);
        }

        private static IEnumerable<Prop> ReadProps(string text)
        {
            // 展開被折行的內容（下一行以空白或 Tab 開頭代表接續上一行）
            // （用 StringBuilder 接：一個很長的屬性，例如內嵌的附件，分成幾千行時，逐行用字串相加會越接越慢）
            var lines = new List<string>();
            StringBuilder current = null;
            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && current != null)
                {
                    current.Append(raw, 1, raw.Length - 1);
                }
                else
                {
                    if (current != null) lines.Add(current.ToString());
                    current = new StringBuilder(raw);
                }
            }
            if (current != null) lines.Add(current.ToString());

            foreach (string line in lines)
            {
                if (line.Length == 0) continue;
                int colon = IndexOfUnquoted(line, ':');
                if (colon <= 0) continue;

                string head = line.Substring(0, colon);
                var parts = SplitUnquoted(head, ';');
                var prop = new Prop { Name = parts[0].ToUpperInvariant(), Value = line.Substring(colon + 1) };
                for (int i = 1; i < parts.Count; i++)
                {
                    int eq = parts[i].IndexOf('=');
                    if (eq <= 0) continue;
                    prop.Params[parts[i].Substring(0, eq)] = parts[i].Substring(eq + 1).Trim('"');
                }
                yield return prop;
            }
        }

        private static int IndexOfUnquoted(string s, char c)
        {
            bool quoted = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') quoted = !quoted;
                else if (s[i] == c && !quoted) return i;
            }
            return -1;
        }

        private static List<string> SplitUnquoted(string s, char c)
        {
            var parts = new List<string>();
            bool quoted = false;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') quoted = !quoted;
                else if (s[i] == c && !quoted)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        private static Ev BuildEvent(List<Prop> props)
        {
            var ev = new Ev();
            Prop dtStart = null, dtEnd = null, duration = null, recurrenceId = null;

            foreach (Prop p in props)
            {
                switch (p.Name)
                {
                    case "UID": ev.Uid = p.Value; break;
                    case "SUMMARY": ev.Summary = Unescape(p.Value); break;
                    case "DTSTART": dtStart = p; break;
                    case "DTEND": dtEnd = p; break;
                    case "DURATION": duration = p; break;
                    case "RRULE": ev.Rrule = p.Value; break;
                    case "RECURRENCE-ID": recurrenceId = p; break;
                    case "STATUS": if (p.Value.Trim().ToUpperInvariant() == "CANCELLED") ev.Cancelled = true; break;
                    case "EXDATE":
                        foreach (string v in p.Value.Split(','))
                        {
                            bool allDay; DateTime wall; TimeZoneInfo zone;
                            if (TryParseDate(v, p, out allDay, out wall, out zone))
                                ev.ExDates.Add(allDay ? wall.Date : ToLocal(wall, zone));
                        }
                        break;
                }
            }

            if (dtStart == null) return null;
            if (!TryParseDate(dtStart.Value, dtStart, out ev.AllDay, out ev.StartWall, out ev.Zone)) return null;

            if (ev.AllDay)
            {
                ev.Days = 1;
                bool endAllDay; DateTime endWall; TimeZoneInfo endZone;
                if (dtEnd != null && TryParseDate(dtEnd.Value, dtEnd, out endAllDay, out endWall, out endZone))
                    ev.Days = Math.Max(1, (int)(endWall.Date - ev.StartWall.Date).TotalDays);
            }
            else
            {
                bool endAllDay; DateTime endWall; TimeZoneInfo endZone;
                if (dtEnd != null && TryParseDate(dtEnd.Value, dtEnd, out endAllDay, out endWall, out endZone))
                    ev.Duration = ToLocal(endWall, endZone) - ToLocal(ev.StartWall, ev.Zone);
            }

            if (recurrenceId != null)
            {
                bool allDay; DateTime wall; TimeZoneInfo zone;
                if (TryParseDate(recurrenceId.Value, recurrenceId, out allDay, out wall, out zone))
                    ev.RecurrenceKey = allDay ? wall.Date : ToLocal(wall, zone);
            }
            return ev;
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 'n' || n == 'N' ? '\n' : n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString().Replace('\n', ' ').Trim();
        }

        private static bool TryParseDate(string value, Prop p, out bool allDay, out DateTime wall, out TimeZoneInfo zone)
        {
            allDay = false;
            wall = default(DateTime);
            zone = null;
            value = value.Trim();

            string valueType;
            bool dateOnly = (p.Params.TryGetValue("VALUE", out valueType) && valueType.ToUpperInvariant() == "DATE") || value.Length == 8;
            if (dateOnly)
            {
                allDay = true;
                return DateTime.TryParseExact(value.Substring(0, Math.Min(8, value.Length)), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out wall);
            }

            bool utc = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
            string core = utc ? value.Substring(0, value.Length - 1) : value;
            if (!DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out wall)
                && !DateTime.TryParseExact(core, "yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out wall))
                return false;

            if (utc) zone = TimeZoneInfo.Utc;
            else
            {
                string tzid;
                if (p.Params.TryGetValue("TZID", out tzid)) zone = FindZone(tzid);
            }
            return true;
        }

        // ---------- 時區 ----------

        private static readonly Dictionary<string, string> WindowsZoneNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Asia/Taipei", "Taipei Standard Time" },
            { "Asia/Tokyo", "Tokyo Standard Time" },
            { "Asia/Seoul", "Korea Standard Time" },
            { "Asia/Shanghai", "China Standard Time" },
            { "Asia/Hong_Kong", "China Standard Time" },
            { "Asia/Singapore", "Singapore Standard Time" },
            { "Asia/Bangkok", "SE Asia Standard Time" },
            { "Asia/Kolkata", "India Standard Time" },
            { "Australia/Sydney", "AUS Eastern Standard Time" },
            { "Europe/London", "GMT Standard Time" },
            { "Europe/Paris", "Romance Standard Time" },
            { "Europe/Berlin", "W. Europe Standard Time" },
            { "America/New_York", "Eastern Standard Time" },
            { "America/Chicago", "Central Standard Time" },
            { "America/Denver", "Mountain Standard Time" },
            { "America/Los_Angeles", "Pacific Standard Time" },
            { "UTC", "UTC" },
            { "Etc/UTC", "UTC" },
            { "GMT", "UTC" },
            // 以下是常見的其他時區（.NET Framework 在 Windows 上只認 Windows 的時區名稱，不認 IANA 名稱）；
            // 不在表裡的時區會被當成電腦的時區，時間會整個偏掉
            { "Asia/Calcutta", "India Standard Time" },
            { "Asia/Colombo", "Sri Lanka Standard Time" },
            { "Asia/Kathmandu", "Nepal Standard Time" },
            { "Asia/Dhaka", "Bangladesh Standard Time" },
            { "Asia/Karachi", "Pakistan Standard Time" },
            { "Asia/Dubai", "Arabian Standard Time" },
            { "Asia/Jerusalem", "Israel Standard Time" },
            { "Asia/Jakarta", "SE Asia Standard Time" },
            { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" },
            { "Asia/Saigon", "SE Asia Standard Time" },
            { "Asia/Kuala_Lumpur", "Singapore Standard Time" },
            { "Asia/Manila", "Singapore Standard Time" },
            { "Asia/Macau", "China Standard Time" },
            { "Europe/Dublin", "GMT Standard Time" },
            { "Europe/Lisbon", "GMT Standard Time" },
            { "Europe/Amsterdam", "W. Europe Standard Time" },
            { "Europe/Rome", "W. Europe Standard Time" },
            { "Europe/Vienna", "W. Europe Standard Time" },
            { "Europe/Zurich", "W. Europe Standard Time" },
            { "Europe/Stockholm", "W. Europe Standard Time" },
            { "Europe/Oslo", "W. Europe Standard Time" },
            { "Europe/Madrid", "Romance Standard Time" },
            { "Europe/Brussels", "Romance Standard Time" },
            { "Europe/Copenhagen", "Romance Standard Time" },
            { "Europe/Prague", "Central Europe Standard Time" },
            { "Europe/Budapest", "Central Europe Standard Time" },
            { "Europe/Warsaw", "Central European Standard Time" },
            { "Europe/Athens", "GTB Standard Time" },
            { "Europe/Helsinki", "FLE Standard Time" },
            { "Europe/Kiev", "FLE Standard Time" },
            { "Europe/Istanbul", "Turkey Standard Time" },
            { "Europe/Moscow", "Russian Standard Time" },
            { "America/Toronto", "Eastern Standard Time" },
            { "America/Vancouver", "Pacific Standard Time" },
            { "America/Phoenix", "US Mountain Standard Time" },
            { "America/Anchorage", "Alaskan Standard Time" },
            { "America/Halifax", "Atlantic Standard Time" },
            { "America/Mexico_City", "Central Standard Time (Mexico)" },
            { "America/Sao_Paulo", "E. South America Standard Time" },
            { "America/Argentina/Buenos_Aires", "Argentina Standard Time" },
            { "Pacific/Honolulu", "Hawaiian Standard Time" },
            { "Pacific/Auckland", "New Zealand Standard Time" },
            { "Australia/Melbourne", "AUS Eastern Standard Time" },
            { "Australia/Brisbane", "E. Australia Standard Time" },
            { "Australia/Perth", "W. Australia Standard Time" },
            { "Australia/Adelaide", "Cen. Australia Standard Time" },
            { "Africa/Cairo", "Egypt Standard Time" },
            { "Africa/Johannesburg", "South Africa Standard Time" },
            { "Africa/Lagos", "W. Central Africa Standard Time" },
            { "Africa/Nairobi", "E. Africa Standard Time" }
        };

        // 多個來源會在不同的背景工作同時解析，所以快取要加鎖
        private static readonly Dictionary<string, TimeZoneInfo> ZoneCache = new Dictionary<string, TimeZoneInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ZoneLock = new object();

        internal static IEnumerable<string> KnownZoneIds { get { return WindowsZoneNames.Keys; } }

        internal static TimeZoneInfo FindZone(string tzid)
        {
            lock (ZoneLock)
            {
                TimeZoneInfo zone;
                if (ZoneCache.TryGetValue(tzid, out zone)) return zone;

                string windowsName;
                string[] candidates = WindowsZoneNames.TryGetValue(tzid, out windowsName) ? new[] { windowsName, tzid } : new[] { tzid };
                zone = null;
                foreach (string name in candidates)
                {
                    try { zone = TimeZoneInfo.FindSystemTimeZoneById(name); break; }
                    catch (TimeZoneNotFoundException) { }
                    catch (InvalidTimeZoneException) { }
                }
                ZoneCache[tzid] = zone;   // 找不到時為 null，視為電腦的時區
                return zone;
            }
        }

        internal static DateTime ToLocal(DateTime wall, TimeZoneInfo zone)
        {
            if (zone == null || zone.Id == TimeZoneInfo.Local.Id) return DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            DateTime unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(unspecified, zone, TimeZoneInfo.Local), DateTimeKind.Unspecified);
        }

        // ---------- RRULE ----------

        private sealed class RRule
        {
            public string Freq;
            public int Interval = 1;
            public int Count;
            public DateTime? Until;
            public DayOfWeek WeekStart = DayOfWeek.Monday;
            public List<KeyValuePair<int, DayOfWeek>> ByDay = new List<KeyValuePair<int, DayOfWeek>>();
            public List<int> ByMonthDay = new List<int>();
            public List<int> ByMonth = new List<int>();
            public List<int> BySetPos = new List<int>();

            private const int MaxInterval = 10000;   // 再大也只會讓日期算到超出範圍，限制住免得整數相乘溢位

            public static RRule Parse(string text, Ev ev)
            {
                var r = new RRule();
                foreach (string part in text.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = part.Substring(0, eq).ToUpperInvariant();
                    string val = part.Substring(eq + 1);
                    switch (key)
                    {
                        case "FREQ": r.Freq = val.ToUpperInvariant(); break;
                        case "INTERVAL": int.TryParse(val, out r.Interval); r.Interval = Math.Max(1, Math.Min(MaxInterval, r.Interval)); break;
                        case "COUNT": int.TryParse(val, out r.Count); break;
                        case "UNTIL": r.Until = ParseUntil(val, ev); break;
                        case "WKST": r.WeekStart = ParseDay(val); break;
                        case "BYMONTHDAY": r.ByMonthDay = ParseInts(val); break;
                        case "BYMONTH": r.ByMonth = ParseInts(val); break;
                        case "BYSETPOS": r.BySetPos = ParseInts(val); break;
                        case "BYDAY":
                            foreach (string token in val.Split(','))
                            {
                                string t = token.Trim().ToUpperInvariant();
                                if (t.Length < 2) continue;
                                int ordinal = 0;
                                string num = t.Substring(0, t.Length - 2);
                                if (num.Length > 0) int.TryParse(num, out ordinal);
                                r.ByDay.Add(new KeyValuePair<int, DayOfWeek>(ordinal, ParseDay(t.Substring(t.Length - 2))));
                            }
                            break;
                    }
                }
                return r;
            }

            private static DateTime? ParseUntil(string val, Ev ev)
            {
                val = val.Trim();
                DateTime d;
                if (val.Length == 8 && DateTime.TryParseExact(val, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                    return d.Date == DateTime.MaxValue.Date ? DateTime.MaxValue : d.Date.AddDays(1).AddTicks(-1);   // 只有日期時，當天整天都算
                bool utc = val.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
                string core = utc ? val.Substring(0, val.Length - 1) : val;
                if (!DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
                if (utc && ev.Zone != null && ev.Zone.Id != TimeZoneInfo.Utc.Id)
                    d = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeZoneInfo.Utc, ev.Zone);
                return d;
            }

            private static DayOfWeek ParseDay(string s)
            {
                switch (s.ToUpperInvariant())
                {
                    case "SU": return DayOfWeek.Sunday;
                    case "MO": return DayOfWeek.Monday;
                    case "TU": return DayOfWeek.Tuesday;
                    case "WE": return DayOfWeek.Wednesday;
                    case "TH": return DayOfWeek.Thursday;
                    case "FR": return DayOfWeek.Friday;
                    default: return DayOfWeek.Saturday;
                }
            }

            private static List<int> ParseInts(string val)
            {
                var list = new List<int>();
                foreach (string s in val.Split(','))
                {
                    int n;
                    if (int.TryParse(s.Trim(), out n)) list.Add(n);
                }
                return list;
            }

            public IEnumerable<DateTime> Expand(DateTime start, DateTime limit)
            {
                if (Freq != "DAILY" && Freq != "WEEKLY" && Freq != "MONTHLY" && Freq != "YEARLY")
                {
                    yield return start;
                    yield break;
                }

                int produced = 0;
                for (int n = 0; n < 200000; n++)
                {
                    // 週期算到 9999 年之後就沒有下一次了（日期會溢位），視為結束
                    DateTime periodFirst = default(DateTime);
                    List<DateTime> candidates = null;
                    bool overflow = false;
                    try
                    {
                        periodFirst = PeriodFirstDay(start, n);
                        if (periodFirst <= limit.Date) candidates = Candidates(start, n);
                    }
                    catch (ArgumentOutOfRangeException) { overflow = true; }
                    if (overflow || periodFirst > limit.Date) yield break;

                    foreach (DateTime c in candidates)
                    {
                        if (c < start) continue;
                        if (Until.HasValue && c > Until.Value) yield break;
                        if (c > limit) yield break;
                        produced++;
                        yield return c;
                        if (Count > 0 && produced >= Count) yield break;
                    }
                }
            }

            private DateTime PeriodFirstDay(DateTime start, int n)
            {
                switch (Freq)
                {
                    case "DAILY": return start.Date.AddDays((long)n * Interval);
                    case "WEEKLY": return WeekAnchor(start.Date).AddDays(7L * n * Interval);
                    case "MONTHLY": return new DateTime(start.Year, start.Month, 1).AddMonths(n * Interval);
                    default: return new DateTime(start.Year + n * Interval, 1, 1);
                }
            }

            private DateTime WeekAnchor(DateTime date)
            {
                int diff = ((int)date.DayOfWeek - (int)WeekStart + 7) % 7;
                return date.AddDays(-diff);
            }

            private List<DateTime> Candidates(DateTime start, int n)
            {
                TimeSpan tod = start.TimeOfDay;
                var days = new List<DateTime>();

                if (Freq == "DAILY")
                {
                    DateTime d = start.Date.AddDays((long)n * Interval);
                    if (MatchesFilters(d)) days.Add(d);
                }
                else if (Freq == "WEEKLY")
                {
                    DateTime anchor = WeekAnchor(start.Date).AddDays(7L * n * Interval);
                    var weekdays = ByDay.Count > 0 ? ByDay.Select(b => b.Value).Distinct().ToList() : new List<DayOfWeek> { start.DayOfWeek };
                    foreach (DayOfWeek dow in weekdays)
                    {
                        DateTime d = anchor.AddDays(((int)dow - (int)WeekStart + 7) % 7);
                        if (ByMonth.Count == 0 || ByMonth.Contains(d.Month)) days.Add(d);
                    }
                }
                else if (Freq == "MONTHLY")
                {
                    DateTime month = new DateTime(start.Year, start.Month, 1).AddMonths(n * Interval);
                    if (ByMonth.Count == 0 || ByMonth.Contains(month.Month)) days.AddRange(DaysInMonth(month, start));
                }
                else // YEARLY
                {
                    int year = start.Year + n * Interval;
                    var months = ByMonth.Count > 0 ? ByMonth : new List<int> { start.Month };
                    foreach (int m in months)
                    {
                        if (m < 1 || m > 12) continue;
                        days.AddRange(DaysInMonth(new DateTime(year, m, 1), start));
                    }
                }

                days = days.Distinct().OrderBy(d => d).ToList();
                if (BySetPos.Count > 0 && days.Count > 0)
                {
                    var picked = new List<DateTime>();
                    foreach (int pos in BySetPos)
                    {
                        int idx = pos > 0 ? pos - 1 : days.Count + pos;
                        if (idx >= 0 && idx < days.Count) picked.Add(days[idx]);
                    }
                    days = picked.Distinct().OrderBy(d => d).ToList();
                }
                return days.Select(d => d + tod).ToList();
            }

            private bool MatchesFilters(DateTime d)
            {
                if (ByMonth.Count > 0 && !ByMonth.Contains(d.Month)) return false;
                if (ByMonthDay.Count > 0 && !MatchesMonthDay(d)) return false;
                if (ByDay.Count > 0 && !ByDay.Any(b => b.Value == d.DayOfWeek)) return false;
                return true;
            }

            private bool MatchesMonthDay(DateTime d)
            {
                int dim = DateTime.DaysInMonth(d.Year, d.Month);
                return ByMonthDay.Any(x => (x > 0 ? x : dim + x + 1) == d.Day);
            }

            // 某個月份內符合 BYMONTHDAY／BYDAY 的日期；兩者都沒有就用起始日的「幾號」
            private IEnumerable<DateTime> DaysInMonth(DateTime month, DateTime start)
            {
                int dim = DateTime.DaysInMonth(month.Year, month.Month);
                var result = new List<DateTime>();

                if (ByMonthDay.Count == 0 && ByDay.Count == 0)
                {
                    if (start.Day <= dim) result.Add(new DateTime(month.Year, month.Month, start.Day));
                    return result;
                }

                IEnumerable<DateTime> byDay = null;
                if (ByDay.Count > 0)
                {
                    var set = new List<DateTime>();
                    foreach (var b in ByDay)
                    {
                        var matching = Enumerable.Range(1, dim).Select(i => new DateTime(month.Year, month.Month, i))
                            .Where(d => d.DayOfWeek == b.Value).ToList();
                        if (b.Key == 0) set.AddRange(matching);
                        else
                        {
                            int idx = b.Key > 0 ? b.Key - 1 : matching.Count + b.Key;
                            if (idx >= 0 && idx < matching.Count) set.Add(matching[idx]);
                        }
                    }
                    byDay = set;
                }

                IEnumerable<DateTime> byMonthDay = null;
                if (ByMonthDay.Count > 0)
                {
                    byMonthDay = ByMonthDay.Select(x => x > 0 ? x : dim + x + 1)
                        .Where(x => x >= 1 && x <= dim)
                        .Select(x => new DateTime(month.Year, month.Month, x)).ToList();
                }

                if (byDay != null && byMonthDay != null) return byDay.Intersect(byMonthDay);
                return byDay ?? byMonthDay;
            }
        }
    }
}

using System;
using System.Globalization;

namespace CalendarWidget
{
    // 「新增行程」視窗的規則（不碰畫面，方便單獨測試）
    internal static class AddEventLogic
    {
        // 預設的開始時間：新增在「今天」，就用下一個整點或半點（至少 15 分鐘之後；太晚就用 23:00）；新增在其他日子就用 09:00
        public static TimeSpan DefaultStart(DateTime date, DateTime now)
        {
            if (date.Date != now.Date) return TimeSpan.FromHours(9);

            DateTime t = now.AddMinutes(15);
            DateTime start = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddMinutes(t.Minute < 30 ? 30 : 60);
            if (start.Date != now.Date) return new TimeSpan(23, 0, 0);
            return start.TimeOfDay;
        }

        // 讀使用者打的時間：9:30、09:30 都可以；範圍 00:00 ~ 23:59
        public static bool TryParseTime(string text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim().Replace('：', ':');

            TimeSpan parsed;
            if (!TimeSpan.TryParseExact(text, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out parsed)) return false;
            if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1)) return false;
            time = parsed;
            return true;
        }

        public static string Format(TimeSpan t)
        {
            return t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }

        // 行程最長幾天（月曆最多把整天行程展開到這個天數）
        public const int MaxDays = LocalEventStore.MaxSpanDays;

        // 檢查輸入；有問題就回傳要顯示給使用者的訊息，沒問題回傳 null。全天行程不用看時間（單一天）
        public static string Validate(string title, bool allDay, string startText, string endText, out TimeSpan start, out TimeSpan end)
        {
            return ValidateRange(title, allDay, DateTime.Today, DateTime.Today, startText, endText, out start, out end);
        }

        // 同上，另外檢查開始、結束日期：結束日期不能早於開始日期，最長 MaxDays 天；
        // 有時間的行程，同一天內結束時間要晚於開始時間，跨天就不用比
        public static string ValidateRange(string title, bool allDay, DateTime startDate, DateTime endDate, string startText, string endText, out TimeSpan start, out TimeSpan end)
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(title)) return "請輸入行程的標題。";
            if (endDate.Date < startDate.Date) return "結束日期不能早於開始日期。";
            if ((endDate.Date - startDate.Date).TotalDays + 1 > MaxDays) return "行程最長 " + MaxDays + " 天。";
            if (allDay) return null;

            if (!TryParseTime(startText, out start)) return "開始時間的格式不對，請輸入像 09:30 這樣的時間。";
            if (!TryParseTime(endText, out end)) return "結束時間的格式不對，請輸入像 10:30 這樣的時間。";
            if (endDate.Date == startDate.Date && end <= start) return "結束時間要晚於開始時間。";
            return null;
        }
    }
}

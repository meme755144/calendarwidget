using System;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace CalendarWidget
{
    // 一般設定存在 %APPDATA%\CalendarWidget\settings.ini（key=value，一行一項），不引入額外套件
    internal class Settings
    {
        public const string DefaultTodoColor = "#7EE0A1";

        public double Left = double.NaN;
        public double Top = double.NaN;
        public double Opacity = 1.0;                 // 整個月曆的透明度，0.3 ~ 1.0
        public string TodoColor = DefaultTodoColor;  // 手動加入的待辦的顏色
        public const string DefaultLocalEventColor = "#4ECDC4";

        public string LocalEventColor = DefaultLocalEventColor;   // 本機行程的顏色
        public string LastAddCalendar = "";          // 右鍵新增行程時，上一次選的日曆（沒有的話就用第一個可以選的）
        public bool DetailCollapsed;                 // 月曆下方的詳細區塊是否摺起來
        public bool WeekStartsOnMonday;              // 一週從週一開始（預設從週日）
        public double Scale = 1.0;                   // 月曆整體大小（含文字）
        public bool LockPosition;                    // 鎖定位置，不能用滑鼠拖動
        public double DotScale = 1.0;                // 月曆下方清單前面的圓點大小（日曆格子裡的圓點不受影響）
        public double DetailScale = 1.0;             // 月曆下方「選取日期的行程與待辦」的文字大小（只調這一塊）
        public int LoadPastMonths = 6;               // 外部日曆（Google、ICS）載入今天往前幾個月
        public int LoadFutureMonths = 12;            // 往後幾個月
        public const int EventReminderPerEvent = 0;  // 依各行程自己的設定（Google 的提醒、ICS 檔案裡的提醒）
        public const int EventReminderCustom = 1;    // 使用者自訂：所有有時間的行程，開始前固定幾分鐘
        public const int EventReminderOff = 2;       // 不提醒

        public int EventReminderMode = EventReminderPerEvent;
        public int EventReminderMinutes = 10;        // 自訂模式用：開始前幾分鐘
        public bool HolidayReminder = true;          // 隔天是節日時，提醒到按「好」為止
        public int ReminderIntervalMinutes = 60;     // 多久檢查、提醒一次
        public int ReminderStartHour;                // 只在這個時段內提醒：從幾點（含）
        public int ReminderEndHour = 24;             // 到幾點（不含），24 代表一整天

        public static bool ReadOnly;

        private static string FilePath
        {
            get { return Path.Combine(DataPaths.Dir, "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    string key = parts[0].Trim();
                    string val = parts[1].Trim();
                    double v;
                    bool isNumber = double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

                    if (key == "Left" && isNumber) s.Left = v;
                    else if (key == "Top" && isNumber) s.Top = v;
                    else if (key == "Opacity" && isNumber) s.Opacity = ClampOpacity(v);
                    else if (key == "TodoColor" && Colors.IsValidHex(val)) s.TodoColor = val;
                    else if (key == "HolidayReminder") s.HolidayReminder = val != "0";
                    else if (key == "DetailCollapsed") s.DetailCollapsed = val == "1";
                    else if (key == "LocalEventColor" && Colors.IsValidHex(val)) s.LocalEventColor = val;
                    else if (key == "LastAddCalendar") s.LastAddCalendar = val;
                    else if (key == "EventReminderMode" && isNumber && v >= 0 && v <= 2) s.EventReminderMode = (int)v;
                    else if (key == "EventReminderMinutes" && isNumber && v >= 0 && v <= 40320) s.EventReminderMinutes = (int)v;
                    else if (key == "WeekStartsOnMonday") s.WeekStartsOnMonday = val == "1";
                    else if (key == "LockPosition") s.LockPosition = val == "1";
                    else if (key == "Scale" && isNumber) s.Scale = ClampScale(v);
                    else if (key == "DotScale" && isNumber) s.DotScale = ClampDotScale(v);
                    else if (key == "DetailScale" && isNumber) s.DetailScale = ClampDetailScale(v);
                    else if (key == "LoadPastMonths" && isNumber && v >= 1 && v <= 60) s.LoadPastMonths = (int)v;
                    else if (key == "LoadFutureMonths" && isNumber && v >= 1 && v <= 60) s.LoadFutureMonths = (int)v;
                    else if (key == "ReminderIntervalMinutes" && isNumber && v >= 1 && v <= 1440) s.ReminderIntervalMinutes = (int)v;
                    else if (key == "ReminderStartHour" && isNumber && v >= 0 && v <= 23) s.ReminderStartHour = (int)v;
                    else if (key == "ReminderEndHour" && isNumber && v >= 1 && v <= 24) s.ReminderEndHour = (int)v;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            LoadRange.Set(s);
            return s;
        }

        public static double ClampScale(double v)
        {
            if (double.IsNaN(v)) return 1.0;
            return Math.Max(0.8, Math.Min(1.6, v));
        }

        public static double ClampDotScale(double v)
        {
            if (double.IsNaN(v)) return 1.0;
            return Math.Max(0.8, Math.Min(2.5, v));
        }

        public static double ClampDetailScale(double v)
        {
            if (double.IsNaN(v)) return 1.0;
            return Math.Max(0.8, Math.Min(2.0, v));
        }

        public static double ClampOpacity(double v)
        {
            if (double.IsNaN(v)) return 1.0;
            return Math.Max(0.3, Math.Min(1.0, v));
        }

        public void Save()
        {
            if (ReadOnly) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "Left=" + Left.ToString("R", CultureInfo.InvariantCulture),
                    "Top=" + Top.ToString("R", CultureInfo.InvariantCulture),
                    "Opacity=" + Opacity.ToString("R", CultureInfo.InvariantCulture),
                    "TodoColor=" + TodoColor,
                    "LocalEventColor=" + LocalEventColor,
                    "LastAddCalendar=" + (LastAddCalendar ?? "").Replace('\r', ' ').Replace('\n', ' '),
                    "DetailCollapsed=" + (DetailCollapsed ? "1" : "0"),
                    "EventReminderMode=" + EventReminderMode.ToString(CultureInfo.InvariantCulture),
                    "EventReminderMinutes=" + EventReminderMinutes.ToString(CultureInfo.InvariantCulture),
                    "WeekStartsOnMonday=" + (WeekStartsOnMonday ? "1" : "0"),
                    "LockPosition=" + (LockPosition ? "1" : "0"),
                    "Scale=" + Scale.ToString("R", CultureInfo.InvariantCulture),
                    "DotScale=" + DotScale.ToString("R", CultureInfo.InvariantCulture),
                    "DetailScale=" + DetailScale.ToString("R", CultureInfo.InvariantCulture),
                    "LoadPastMonths=" + LoadPastMonths.ToString(CultureInfo.InvariantCulture),
                    "LoadFutureMonths=" + LoadFutureMonths.ToString(CultureInfo.InvariantCulture),
                    "HolidayReminder=" + (HolidayReminder ? "1" : "0"),
                    "ReminderIntervalMinutes=" + ReminderIntervalMinutes.ToString(CultureInfo.InvariantCulture),
                    "ReminderStartHour=" + ReminderStartHour.ToString(CultureInfo.InvariantCulture),
                    "ReminderEndHour=" + ReminderEndHour.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // 外部日曆（Google、ICS）要載入的日期範圍：今天往前、往後各幾個月（設定 → 一般 → 載入範圍）
    internal static class LoadRange
    {
        public static int PastMonths = 6;
        public static int FutureMonths = 12;

        public static DateTime From { get { return DateTime.Today.AddMonths(-PastMonths); } }
        public static DateTime To { get { return DateTime.Today.AddMonths(FutureMonths); } }

        public static void Set(Settings s)
        {
            PastMonths = s.LoadPastMonths;
            FutureMonths = s.LoadFutureMonths;
        }
    }

    internal static class Colors
    {
        // 設定視窗提供的色票
        public static readonly string[] Palette =
        {
            "#FF6B7A", "#FF9F43", "#FFD166", "#7EE0A1", "#4ECDC4", "#6CB4FF", "#A78BFA", "#F78FB3", "#C8D0E0"
        };

        public static bool IsValidHex(string s)
        {
            if (s == null || s.Length != 7 || s[0] != '#') return false;
            for (int i = 1; i < 7; i++)
            {
                if (!Uri.IsHexDigit(s[i])) return false;
            }
            return true;
        }
    }

    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CalendarWidget";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(ValueName) != null;
            }
        }

        public static void Set(bool enabled)
        {
            // 示範與測試模式不去改登錄檔
            if (Settings.ReadOnly || !string.IsNullOrEmpty(DataPaths.Override)) return;

            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null) return;
                if (enabled)
                    key.SetValue(ValueName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                else
                    key.DeleteValue(ValueName, false);
            }
        }
    }
}

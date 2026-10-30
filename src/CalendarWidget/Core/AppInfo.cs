using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace CalendarWidget
{
    // 版本號、專案網址，以及「關於」頁與檢查更新共用的小工具
    internal static class AppInfo
    {
        public const string Owner = "meme755144";
        public const string RepoUrl = "https://github.com/meme755144/calendarwidget";
        public const string IssuesUrl = RepoUrl + "/issues";
        public const string ReleasesUrl = RepoUrl + "/releases";
        public const string PrivacyUrl = "https://meme755144.github.io/calendarwidget/privacy.html";
        public const string TermsUrl = "https://meme755144.github.io/calendarwidget/terms.html";

        // 版本號只在 CalendarWidget.csproj 的 <Version> 設定；組件版本是 X.Y.Z.0，這裡去掉最後一段
        public static Version Version
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            }
        }

        public static string VersionText { get { return Version.ToString(); } }

        // 只允許打開本專案自己的網址（GitHub 專案頁與 Pages）。伺服器回傳的網址不能直接信任：
        // 要精確比對主機與路徑，不能只比開頭（否則 github.com/meme755144/calendarwidget-evil 也會通過）
        public static bool IsOfficialUrl(string url)
        {
            Uri uri;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;

            string path = uri.AbsolutePath;
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return IsUnder(path, "/meme755144/calendarwidget");
            if (uri.Host.Equals("meme755144.github.io", StringComparison.OrdinalIgnoreCase))
                return IsUnder(path, "/calendarwidget");
            return false;
        }

        private static bool IsUnder(string path, string root)
        {
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        // 用預設瀏覽器打開官方網址。不是官方網址就不打開
        public static bool OpenUrl(string url)
        {
            if (!IsOfficialUrl(url)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 包在程式裡的 CHANGELOG.md；沒有就回傳空字串
        public static string ReadChangelog()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md"))
            {
                if (stream == null) return "";
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
    }
}

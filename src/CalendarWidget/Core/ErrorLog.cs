using System;
using System.IO;
using System.Text;

namespace CalendarWidget
{
    // 意外的例外記到 %LOCALAPPDATA%\CalendarWidget\error.log，方便回報問題時附上。只存在你的電腦，不會自己傳到任何地方；
    // 內容是例外的類型、訊息與呼叫堆疊（堆疊裡可能有程式所在的資料夾路徑），回報時請自己看過再貼。
    // 檔案超過 256 KB 就從頭重寫，不會越長越大
    internal static class ErrorLog
    {
        private const long MaxBytes = 256 * 1024;
        private static readonly object Gate = new object();

        public static string FilePath { get { return Path.Combine(DataPaths.LocalDir, "error.log"); } }

        public static void Write(Exception ex)
        {
            if (Settings.ReadOnly) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(DataPaths.LocalDir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes) info.Delete();
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  v" + AppInfo.VersionText + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

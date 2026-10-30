using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CalendarWidget
{
    // 存檔一律「先寫到暫存檔，寫完再換掉正式檔」：寫到一半當機、斷電或被防毒軟體鎖住時，
    // 正式檔不是舊的完整內容就是新的完整內容，不會被截斷，也不會出現「已經刪掉舊檔、新檔還沒就位」的空窗。
    // 失敗時丟出 IOException／UnauthorizedAccessException，由呼叫端決定要不要吞掉（跟直接寫檔一樣）
    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string text, Encoding encoding)
        {
            Write(path, temp => File.WriteAllText(temp, text, encoding));
        }

        public static void WriteAllLines(string path, IEnumerable<string> lines, Encoding encoding)
        {
            Write(path, temp => File.WriteAllLines(temp, lines, encoding));
        }

        private static void Write(string path, Action<string> writeTemp)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                writeTemp(temp);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

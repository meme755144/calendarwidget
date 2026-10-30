using System;
using System.IO;
using System.Net;
using System.Text;

namespace CalendarWidget
{
    // 讀取爬蟲產生的 latest.json（taiwan-typhoon-days 這個 repo）。小工具自己不去爬人事總處的網頁
    internal static class TyphoonFeed
    {
        public const string DefaultUrl = "https://raw.githubusercontent.com/meme755144/taiwan-typhoon-days/main/data/latest.json";

        internal sealed class Result
        {
            public bool NotModified;   // 304：跟上次一樣
            public string Text;
            public string ETag;
        }

        // 設定裡填的網址（留空用預設）。只接受 http、https
        public static string ResolveUrl(string configured)
        {
            string url = (configured ?? "").Trim();
            if (url.Length == 0) return DefaultUrl;
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)) return url;
            return null;
        }

        private const int MaxChars = 2 * 1024 * 1024;   // 正常的資料只有幾 KB；網址可以自訂，所以設上限，不讓怪伺服器塞爆記憶體

        private static string ReadLimited(StreamReader reader)
        {
            var sb = new StringBuilder();
            var buffer = new char[8192];
            int n;
            while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                sb.Append(buffer, 0, n);
                if (sb.Length > MaxChars) throw new InvalidDataException("資料太大，已中止");
            }
            return sb.ToString();
        }

        // 下載。會卡住呼叫的執行緒，請在背景執行緒呼叫。網路錯誤丟 WebException；伺服器回 304 時 NotModified 是 true
        public static Result Fetch(string url, string etag)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "CalendarWidget/1.0";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Headers[HttpRequestHeader.CacheControl] = "no-cache";   // 要最新的；raw.githubusercontent.com 本身還有約 5 分鐘的快取，這邊擋不掉
            if (!string.IsNullOrEmpty(etag)) req.Headers[HttpRequestHeader.IfNoneMatch] = etag;

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return new Result { Text = ReadLimited(reader), ETag = resp.Headers[HttpResponseHeader.ETag] };
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.NotModified) return new Result { NotModified = true, ETag = etag };
                throw;
            }
        }
    }
}

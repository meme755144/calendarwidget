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
                    return new Result { Text = reader.ReadToEnd(), ETag = resp.Headers[HttpResponseHeader.ETag] };
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

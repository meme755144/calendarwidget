using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CalendarWidget
{
    // 用戶端 ID／密鑰、登入憑證（refresh token）、帳號 email：整包用 Windows 資料保護加密，
    // 放在 %LOCALAPPDATA%\CalendarWidget\google.dat，只有這台電腦的這個使用者能解開
    internal sealed class GoogleSecrets
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CalendarWidget.Google");

        public string ClientId = "";
        public string ClientSecret = "";
        public string RefreshToken = "";
        public string Email = "";
        public string Scopes = "";   // 目前已經取得的授權範圍（Google 回傳的，空白分隔）

        private static string FilePath { get { return Path.Combine(DataPaths.LocalDir, "google.dat"); } }

        // 內建的用戶端：編譯時如果專案資料夾裡有 google-client.json（Google Cloud 下載的那個檔案），
        // 就會包進程式裡，使用者不需要自己申請、也不用填，按「連接」登入自己的帳號就好
        public sealed class ClientInfo
        {
            public string Id;
            public string Secret;
        }

        private static ClientInfo _builtIn;
        private static bool _builtInLoaded;

        public static ClientInfo BuiltIn
        {
            get
            {
                if (_builtInLoaded) return _builtIn;
                _builtInLoaded = true;
                try
                {
                    using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("google-client.json"))
                    {
                        if (stream == null) return null;
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            var root = Json.ParseObject(reader.ReadToEnd());
                            var client = Json.Obj(root, "installed") ?? Json.Obj(root, "web");
                            string id = Json.Str(client, "client_id"), secret = Json.Str(client, "client_secret");
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret)) _builtIn = new ClientInfo { Id = id.Trim(), Secret = secret.Trim() };
                        }
                    }
                }
                catch (Exception) { _builtIn = null; }
                return _builtIn;
            }
        }

        // 目前用的是內建的用戶端（使用者沒有另外填自己的）
        public bool UsingBuiltIn
        {
            get { return BuiltIn != null && ClientId == BuiltIn.Id && ClientSecret == BuiltIn.Secret; }
        }

        public void UseBuiltInIfEmpty()
        {
            if (BuiltIn != null && ClientId.Length == 0 && ClientSecret.Length == 0)
            {
                ClientId = BuiltIn.Id;
                ClientSecret = BuiltIn.Secret;
            }
        }

        public static GoogleSecrets Load()
        {
            var s = LoadStored();
            if (BuiltIn != null)
            {
                // 有內建用戶端：一律使用內建的（公開版本不讓使用者自己填）
                s.ClientId = BuiltIn.Id;
                s.ClientSecret = BuiltIn.Secret;
            }
            return s;
        }

        private static GoogleSecrets LoadStored()
        {
            var s = new GoogleSecrets();
            try
            {
                if (!File.Exists(FilePath)) return s;
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(FilePath).Trim()), Entropy, DataProtectionScope.CurrentUser);
                foreach (string line in Encoding.UTF8.GetString(plain).Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq);
                    string val = line.Substring(eq + 1).TrimEnd('\r');
                    if (key == "ClientId") s.ClientId = val;
                    else if (key == "ClientSecret") s.ClientSecret = val;
                    else if (key == "RefreshToken") s.RefreshToken = val;
                    else if (key == "Email") s.Email = val;
                    else if (key == "Scopes") s.Scopes = val;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (CryptographicException) { }   // 換了電腦或使用者就解不開，視為還沒設定
            catch (FormatException) { }
            return s;
        }

        public void Save()
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(DataPaths.LocalDir);
                // 內建的用戶端不另外存一份（不要把它複製到使用者的資料夾）
                string storedId = UsingBuiltIn ? "" : ClientId;
                string storedSecret = UsingBuiltIn ? "" : ClientSecret;
                string text = "ClientId=" + Clean(storedId) + "\nClientSecret=" + Clean(storedSecret) +
                              "\nRefreshToken=" + Clean(RefreshToken) + "\nEmail=" + Clean(Email) + "\nScopes=" + Clean(Scopes) + "\n";
                byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllText(FilePath, Convert.ToBase64String(cipher));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (CryptographicException) { }
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }

    internal sealed class GoogleCalendarEntry
    {
        public string Id = "";
        public string Name = "";
        public string Color = "#6CB4FF";
        public ItemKind Kind = ItemKind.Event;   // Event（行程）或 Holiday（節日）
        public bool Enabled = true;
        public int CanWrite = -1;                // 你對這個日曆有沒有編輯權限：1 有、0 沒有、-1 還不知道（舊版存的資料）

        public GoogleCalendarEntry Clone() { return (GoogleCalendarEntry)MemberwiseClone(); }
    }

    // 要顯示哪些 Google 日曆、待辦、更新頻率：放在 %APPDATA%（可備份，不含任何憑證）
    internal sealed class GoogleConfig
    {
        public List<GoogleCalendarEntry> Calendars = new List<GoogleCalendarEntry>();
        public bool ShowTasks;
        public string TasksColor = "#FFD166";
        public int IntervalMinutes = 30;

        private static string IniFile { get { return Path.Combine(DataPaths.Dir, "google.ini"); } }
        private static string CalendarsFile { get { return Path.Combine(DataPaths.Dir, "google-calendars.txt"); } }

        public static GoogleConfig Load()
        {
            var c = new GoogleConfig();
            try
            {
                if (File.Exists(IniFile))
                {
                    foreach (string line in File.ReadAllLines(IniFile, Encoding.UTF8))
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        if (parts.Length != 2) continue;
                        string key = parts[0].Trim(), val = parts[1].Trim();
                        int n;
                        if (key == "ShowTasks") c.ShowTasks = val == "1";
                        else if (key == "TasksColor" && Colors.IsValidHex(val)) c.TasksColor = val;
                        else if (key == "IntervalMinutes" && int.TryParse(val, out n) && n >= 0) c.IntervalMinutes = n;
                    }
                }
                if (File.Exists(CalendarsFile))
                {
                    foreach (string line in File.ReadAllLines(CalendarsFile, Encoding.UTF8))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length < 5) continue;
                        c.Calendars.Add(new GoogleCalendarEntry
                        {
                            Id = f[0],
                            Enabled = f[1] == "1",
                            Kind = f[2] == "H" ? ItemKind.Holiday : ItemKind.Event,
                            Color = Colors.IsValidHex(f[3]) ? f[3] : "#6CB4FF",
                            Name = f[4],
                            CanWrite = f.Length >= 6 ? (f[5] == "1" ? 1 : f[5] == "0" ? 0 : -1) : -1
                        });
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return c;
        }

        public void Save()
        {
            if (Settings.ReadOnly) return;
            try
            {
                Directory.CreateDirectory(DataPaths.Dir);
                File.WriteAllLines(IniFile, new[]
                {
                    "ShowTasks=" + (ShowTasks ? "1" : "0"),
                    "TasksColor=" + TasksColor,
                    "IntervalMinutes=" + IntervalMinutes.ToString(CultureInfo.InvariantCulture)
                }, new UTF8Encoding(false));
                File.WriteAllLines(CalendarsFile, Calendars.Select(e =>
                    e.Id + "\t" + (e.Enabled ? "1" : "0") + "\t" + (e.Kind == ItemKind.Holiday ? "H" : "E") + "\t" + e.Color + "\t" +
                    (e.Name ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim() + "\t" +
                    (e.CanWrite == 1 ? "1" : e.CanWrite == 0 ? "0" : "?")), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public GoogleConfig Clone()
        {
            return new GoogleConfig
            {
                Calendars = Calendars.Select(e => e.Clone()).ToList(),
                ShowTasks = ShowTasks,
                TasksColor = TasksColor,
                IntervalMinutes = IntervalMinutes
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalendarWidget
{
    // 設定視窗的「Google 帳號」分頁：只管連接帳號與更新頻率。
    // 要顯示哪些日曆、類型、顏色、順序，統一在「日曆」分頁設定
    public partial class SettingsWindow
    {
        private GoogleManager _google;
        private TextBox _gClientId;
        private PasswordBox _gSecret;
        private TextBlock _gStatus;
        private Button _gConnect;
        private Button _gDisconnect;
        private Button _gCopyUrl;
        private StackPanel _gSignedInPanel;
        private ComboBox _gInterval;

        private void BuildGoogleTab()
        {
            var host = GoogleHost;
            GoogleConfig cfg = _google.Config;

            host.Children.Add(new TextBlock { Text = "連接 Google 帳號", FontWeight = FontWeights.SemiBold });
            host.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 14),
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Text = "連接後，你的 Google 日曆和有到期日的 Google 待辦會出現在「日曆」分頁，在那裡選擇要顯示哪些、顏色與順序。程式只有「讀取」的權限，不會修改你的 Google 資料。"
            });

            // 用戶端設定：程式有內建用戶端（公開發布的版本）時完全不顯示，使用者只需要登入自己的帳號。
            // 沒有內建（自己從原始碼編譯、沒放 google-client.json）時，才需要在這裡填自己的用戶端
            _gClientId = new TextBox { Text = _google.ClientId, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 6) };
            _gSecret = new PasswordBox { Password = _google.ClientSecret, Padding = new Thickness(4, 3, 4, 3) };
            if (!_google.HasBuiltInClient)
            {
                var client = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                client.Children.Add(new TextBlock { Text = "1. 用戶端設定（第一次使用才需要）", FontWeight = FontWeights.SemiBold });
                client.Children.Add(new TextBlock
                {
                    Margin = new Thickness(0, 4, 0, 8),
                    Foreground = Brushes.DimGray,
                    TextWrapping = TextWrapping.Wrap,
                    Text = "到 Google Cloud 建立「電腦版應用程式」類型的 OAuth 用戶端，把「用戶端 ID」和「用戶端密鑰」貼在下面（也可以直接匯入下載的 JSON 檔）。這些資料只存在這台電腦，並且加密保存。"
                });

                var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddGridCell(grid, new TextBlock { Text = "用戶端 ID", VerticalAlignment = VerticalAlignment.Center }, 0, 0);
                AddGridCell(grid, _gClientId, 0, 1);
                AddGridCell(grid, new TextBlock { Text = "用戶端密鑰", VerticalAlignment = VerticalAlignment.Center }, 1, 0);
                AddGridCell(grid, _gSecret, 1, 1);
                client.Children.Add(grid);

                var import = new Button { Content = "從 JSON 檔匯入…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4) };
                import.Click += delegate { ImportClientJson(); };
                client.Children.Add(import);
                host.Children.Add(client);
            }

            // 帳號
            host.Children.Add(new TextBlock { Text = _google.HasBuiltInClient ? "登入" : "2. 登入", FontWeight = FontWeights.SemiBold });
            _gStatus = new TextBlock { Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
            host.Children.Add(_gStatus);

            var accountButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            _gConnect = new Button { Padding = new Thickness(14, 5, 14, 5) };
            _gConnect.Click += delegate
            {
                if (_google.Connecting)
                {
                    _google.CancelConnect();
                    return;
                }
                _google.SetClientCredentials(_gClientId.Text, _gSecret.Password);
                _google.Connect();
            };
            _gDisconnect = new Button { Content = "中斷連接", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
            _gDisconnect.Click += delegate { Disconnect(); };
            _gCopyUrl = new Button { Content = "複製登入網址", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
            _gCopyUrl.Click += delegate
            {
                string url = _google.AuthUrl;
                if (url == null)
                {
                    MessageBox.Show(this, "登入網址還沒準備好，請稍等一下再按。", "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                try { Clipboard.SetText(url); }
                catch (System.Runtime.InteropServices.ExternalException) { return; }   // 剪貼簿正被別的程式佔用
                _gStatus.Text = "登入網址已複製。請貼到瀏覽器的網址列開啟，登入完成後會自動回到這裡。";
            };
            accountButtons.Children.Add(_gConnect);
            accountButtons.Children.Add(_gDisconnect);
            accountButtons.Children.Add(_gCopyUrl);
            host.Children.Add(accountButtons);

            // 連接後才出現的區塊：更新頻率與手動更新
            _gSignedInPanel = new StackPanel();
            host.Children.Add(_gSignedInPanel);

            _gSignedInPanel.Children.Add(new TextBlock { Text = "更新", FontWeight = FontWeights.SemiBold });
            var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            intervalRow.Children.Add(new TextBlock { Text = "更新頻率", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            _gInterval = new ComboBox { Width = 120 };
            foreach (var i in Intervals) _gInterval.Items.Add(i.Key);
            int idx = Array.FindIndex(Intervals, i => i.Value == cfg.IntervalMinutes);
            _gInterval.SelectedIndex = idx >= 0 ? idx : 2;
            intervalRow.Children.Add(_gInterval);

            var refresh = new Button { Content = "立即更新", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(12, 0, 0, 0) };
            refresh.Click += delegate
            {
                SaveGoogle();   // 先把目前的選擇記下來，再去更新
                _google.RefreshNow();
            };
            intervalRow.Children.Add(refresh);

            var reload = new Button { Content = "重新載入日曆清單", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
            reload.Click += delegate { _google.ReloadCalendars(); };
            intervalRow.Children.Add(reload);
            _gSignedInPanel.Children.Add(intervalRow);

            UpdateGoogleUi();

            _google.StatusChanged += UpdateGoogleUi;
            _google.CalendarsChanged += RebuildCalendarList;
        }

        private void DisposeGoogleTab()
        {
            _google.StatusChanged -= UpdateGoogleUi;
            _google.CalendarsChanged -= RebuildCalendarList;
        }

        private static void AddGridCell(Grid grid, UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        private void UpdateGoogleUi()
        {
            _gStatus.Text = _google.StatusText();
            _gConnect.Content = _google.Connecting ? "取消登入" : (_google.IsSignedIn ? "重新連接" : "連接 Google 帳號");
            _gDisconnect.Visibility = _google.IsSignedIn && !_google.Connecting ? Visibility.Visible : Visibility.Collapsed;
            _gCopyUrl.Visibility = _google.Connecting ? Visibility.Visible : Visibility.Collapsed;
            _gSignedInPanel.Visibility = _google.IsSignedIn && !_google.NeedsSignIn ? Visibility.Visible : Visibility.Collapsed;
        }

        // 把「日曆」分頁裡 Google 相關的選擇，連同這個分頁的更新頻率，一起存起來
        private void SaveGoogle()
        {
            _google.SetClientCredentials(_gClientId.Text, _gSecret.Password);
            if (!_google.IsSignedIn) return;

            GoogleConfig cfg = _google.Config;   // 這是副本，改了不會影響原本的
            foreach (var entry in cfg.Calendars)
            {
                var row = FindRow(CalendarIds.Google(entry.Id));
                if (row == null) continue;
                entry.Enabled = row.Enabled;
                entry.Kind = row.Kind;
                entry.Color = row.Color;
            }

            var tasksRow = FindRow(CalendarIds.GoogleTasks);
            bool showTasks = tasksRow != null ? tasksRow.Enabled : cfg.ShowTasks;
            string tasksColor = tasksRow != null ? tasksRow.Color : cfg.TasksColor;

            _google.ApplyConfig(cfg.Calendars, showTasks, tasksColor, Intervals[Math.Max(0, _gInterval.SelectedIndex)].Value);
        }

        private void Disconnect()
        {
            var answer = MessageBox.Show(this, "確定要中斷 Google 帳號的連接嗎？\n程式會刪除本機的登入憑證和已下載的 Google 行程，並通知 Google 撤銷授權。",
                "月曆小工具", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) _google.Disconnect();
        }

        private void ImportClientJson()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON 檔 (*.json)|*.json|所有檔案 (*.*)|*.*", Title = "選擇從 Google Cloud 下載的用戶端 JSON" };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var root = Json.ParseObject(System.IO.File.ReadAllText(dialog.FileName));
                var client = Json.Obj(root, "installed") ?? Json.Obj(root, "web");
                string id = Json.Str(client, "client_id");
                string secret = Json.Str(client, "client_secret");
                if (id == null || secret == null)
                {
                    MessageBox.Show(this, "這個檔案裡找不到用戶端 ID 和密鑰。請確認是在 Google Cloud 建立「電腦版應用程式」後下載的 JSON。",
                        "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _gClientId.Text = id;
                _gSecret.Password = secret;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "無法讀取這個檔案：" + ex.Message, "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}

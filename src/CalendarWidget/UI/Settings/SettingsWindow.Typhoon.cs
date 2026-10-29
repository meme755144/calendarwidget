using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CalendarWidget
{
    // 「颱風假」分頁：開關、要關注的縣市（點選）、各縣市另外要通知的鄉鎮區（勾選）、讀取狀態。
    // 資料網址不放在畫面上：換成別的來源，資料格式不一樣就解析不了；settings.ini 的 TyphoonUrl 只給開發測試用
    public partial class SettingsWindow
    {
        private TyphoonManager _typhoon;
        private CheckBox _tyEnable;
        private TextBlock _tyStatus;
        private StackPanel _tyAreaHost;   // 「鄉鎮區」區塊：每個已選的縣市一個可以展開的區塊

        private readonly HashSet<string> _tyCities = new HashSet<string>();                  // 已選的縣市
        private readonly Dictionary<string, List<string>> _tyAreas = new Dictionary<string, List<string>>();   // 各縣市已選的鄉鎮區；取消選縣市後先留著，再選回來還在
        private readonly HashSet<string> _tyExpanded = new HashSet<string>();                // 展開中的縣市（重畫時保持）
        private readonly Dictionary<string, TextBlock> _tyHeaders = new Dictionary<string, TextBlock>();

        private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

        private static TextBlock GrayText(string text, Thickness margin)
        {
            return new TextBlock { Text = text, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = margin };
        }

        private ToggleButton MakeChip(string text, bool isChecked)
        {
            var chip = new ToggleButton { Content = text, IsChecked = isChecked, Style = (Style)FindResource("Chip") };
            return chip;
        }

        private void BuildTyphoonTab()
        {
            var host = TyphoonHost;

            host.Children.Add(GrayText(
                "關注的縣市有停班停課時，跳出提醒框（跟節日提醒一樣：按「好」才會收起來，或選「N 分鐘後再問我」；間隔與時段沿用「一般 → 提醒」的設定）。" +
                "資料來自 taiwan-typhoon-days：一個定時整理人事行政總處「天然災害停止上班及上課情形」網頁的專案，不是官方資料，可能延遲或遺漏，實際停班停課以官方公告為準。",
                new Thickness(0)));

            _tyEnable = new CheckBox { Content = "開啟颱風假通知（需要連網，每 10 分鐘讀取一次；關閉時完全不連網）", Margin = new Thickness(0, 16, 0, 0), FontWeight = FontWeights.SemiBold };
            _tyEnable.IsChecked = _settings.TyphoonEnabled;
            host.Children.Add(_tyEnable);

            // 目前儲存的關注清單
            var watch = TyphoonWatch.Parse(_settings.TyphoonWatchList);
            foreach (string city in watch.Cities)
            {
                _tyCities.Add(city);
                _tyAreas[city] = watch.AreasOf(city).ToList();
            }

            host.Children.Add(new TextBlock { Text = "要關注的縣市", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 4) });
            host.Children.Add(GrayText("點一下選取、再點一下取消。選了的縣市，整個縣市停班或停課時會通知你。", new Thickness(0, 0, 0, 8)));
            BuildCityChips(host);

            host.Children.Add(new TextBlock { Text = "鄉鎮區（選填）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
            host.Children.Add(GrayText(
                "每個縣市可以另外選要通知的鄉鎮區（例如你住的區），那個區單獨停班停課時也會通知你。沒選任何區＝不通知鄉鎮區。" +
                "公告寫的是區裡的某個地方（例如「秀林鄉大天祥地區」）時，算那個區（秀林鄉）。單一學校的停課一律不通知。",
                new Thickness(0, 0, 0, 8)));
            _tyAreaHost = new StackPanel();
            host.Children.Add(_tyAreaHost);
            RebuildTyphoonAreas();

            host.Children.Add(new TextBlock { Text = "讀取狀態", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 4) });
            _tyStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
            host.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xD8, 0xE3)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Child = _tyStatus
            });

            var refresh = new Button { Content = "儲存這個分頁的設定並立即讀取", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            refresh.Click += delegate { RefreshTyphoonNow(); };
            host.Children.Add(refresh);
            host.Children.Add(GrayText("這個按鈕只會儲存本分頁的設定，其他分頁還沒按「儲存」的修改不受影響。", new Thickness(0, 6, 0, 0)));

            _typhoon.StatusChanged += RefreshTyphoonStatus;
            RefreshTyphoonStatus();
        }

        // 縣市選取鈕：依北、中、南、東、離島分行
        private void BuildCityChips(StackPanel host)
        {
            var regions = new[] { "北部", "中部", "南部", "東部", "離島" };
            var regionStart = new[] { 0, 6, 11, 16, 19, TyphoonCities.All.Length };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int r = 0; r < regions.Length; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = regions[r], Foreground = Gray, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
                Grid.SetRow(label, r);
                grid.Children.Add(label);

                var wrap = new WrapPanel();
                for (int i = regionStart[r]; i < regionStart[r + 1]; i++)
                {
                    string city = TyphoonCities.All[i];
                    var chip = MakeChip(city, _tyCities.Contains(city));
                    chip.Checked += delegate { OnCityToggled(city, true); };
                    chip.Unchecked += delegate { OnCityToggled(city, false); };
                    wrap.Children.Add(chip);
                }
                Grid.SetRow(wrap, r);
                Grid.SetColumn(wrap, 1);
                grid.Children.Add(wrap);
            }
            host.Children.Add(grid);
        }

        private void OnCityToggled(string city, bool selected)
        {
            if (selected)
            {
                _tyCities.Add(city);
                _tyExpanded.Add(city);   // 剛選的縣市直接展開，讓人看到可以再選鄉鎮區
            }
            else
            {
                _tyCities.Remove(city);
            }
            RebuildTyphoonAreas();
        }

        // 已選縣市各一個區塊（標題寫已選了哪些區，點開來勾選）
        private void RebuildTyphoonAreas()
        {
            _tyAreaHost.Children.Clear();
            _tyHeaders.Clear();

            var selected = TyphoonCities.All.Where(_tyCities.Contains).ToList();
            if (selected.Count == 0)
            {
                _tyAreaHost.Children.Add(GrayText("選了縣市之後，這裡可以選那個縣市的鄉鎮區。", new Thickness(0)));
                return;
            }

            foreach (string city in selected)
            {
                string c = city;
                var header = new TextBlock();
                _tyHeaders[c] = header;
                UpdateAreaHeader(c);

                var expander = new Expander { Header = header, IsExpanded = _tyExpanded.Contains(c), Margin = new Thickness(0, 2, 0, 4) };
                expander.Expanded += delegate { _tyExpanded.Add(c); };
                expander.Collapsed += delegate { _tyExpanded.Remove(c); };
                expander.Content = BuildDistrictPicker(c);
                _tyAreaHost.Children.Add(expander);
            }
        }

        private FrameworkElement BuildDistrictPicker(string city)
        {
            List<string> chosen;
            if (!_tyAreas.TryGetValue(city, out chosen)) _tyAreas[city] = chosen = new List<string>();

            var panel = new StackPanel { Margin = new Thickness(18, 6, 0, 6) };
            var wrap = new WrapPanel();
            var chips = new List<KeyValuePair<string, ToggleButton>>();

            // 標準區名；另外把以前手動輸入、不在標準清單裡的項目也列出來（已選），不要悄悄弄丟
            var names = TyphoonDistricts.Of(city).ToList();
            names.AddRange(chosen.Where(a => !names.Contains(a)));
            foreach (string name in names)
            {
                string area = name;
                var chip = MakeChip(area, chosen.Contains(area));
                chip.Checked += delegate { if (!chosen.Contains(area)) chosen.Add(area); UpdateAreaHeader(city); };
                chip.Unchecked += delegate { chosen.Remove(area); UpdateAreaHeader(city); };
                wrap.Children.Add(chip);
                chips.Add(new KeyValuePair<string, ToggleButton>(area, chip));
            }

            var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var all = new Button { Content = "全選", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
            all.Click += delegate { foreach (var c in chips) c.Value.IsChecked = true; };
            var none = new Button { Content = "清除", Padding = new Thickness(10, 2, 10, 2) };
            none.Click += delegate { foreach (var c in chips) c.Value.IsChecked = false; };
            links.Children.Add(all);
            links.Children.Add(none);

            panel.Children.Add(links);
            panel.Children.Add(wrap);
            return panel;
        }

        private void UpdateAreaHeader(string city)
        {
            TextBlock header;
            if (!_tyHeaders.TryGetValue(city, out header)) return;
            List<string> chosen;
            _tyAreas.TryGetValue(city, out chosen);
            header.Inlines.Clear();
            header.Inlines.Add(new System.Windows.Documents.Run(city) { FontWeight = FontWeights.SemiBold });
            if (chosen == null || chosen.Count == 0)
            {
                header.Inlines.Add(new System.Windows.Documents.Run("　只通知整個縣市（沒選鄉鎮區）") { Foreground = Gray });
            }
            else
            {
                var ordered = TyphoonDistricts.Of(city).Where(chosen.Contains).Concat(chosen.Where(a => !TyphoonDistricts.IsStandard(city, a))).ToList();
                header.Inlines.Add(new System.Windows.Documents.Run("　另外通知 " + ordered.Count + " 個：" + string.Join("、", ordered)) { Foreground = Gray });
            }
        }

        // 開發檢查畫面用：展開全部已選縣市的鄉鎮區
        internal void ExpandTyphoonAreasForTest()
        {
            foreach (string city in TyphoonCities.All.Where(_tyCities.Contains)) _tyExpanded.Add(city);
            RebuildTyphoonAreas();
        }

        private void DisposeTyphoonTab()
        {
            _typhoon.StatusChanged -= RefreshTyphoonStatus;
        }

        private void RefreshTyphoonStatus()
        {
            _tyStatus.Text = _typhoon.StatusText();
        }

        // 把本分頁的內容寫進 _settings（還沒存檔）
        private void ReadTyphoonSettings()
        {
            var watch = new TyphoonWatch();
            foreach (string city in TyphoonCities.All.Where(_tyCities.Contains))
            {
                List<string> chosen;
                if (!_tyAreas.TryGetValue(city, out chosen)) chosen = new List<string>();
                // 依標準清單的順序存（再接上不在清單裡的舊項目），檔案裡的順序才固定
                watch.Set(city, TyphoonDistricts.Of(city).Where(chosen.Contains).Concat(chosen.Where(a => !TyphoonDistricts.IsStandard(city, a))));
            }
            _settings.TyphoonEnabled = _tyEnable.IsChecked == true;
            _settings.TyphoonWatchList = watch.Serialize();
        }

        private void RefreshTyphoonNow()
        {
            ReadTyphoonSettings();
            _settings.Save();
            _applied();   // 主視窗會重新套用颱風假的設定（開關、縣市）並立刻讀取
        }
    }
}

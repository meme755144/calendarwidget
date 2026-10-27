using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalendarWidget
{
    public partial class SettingsWindow : Window
    {
        private static readonly KeyValuePair<string, int>[] Intervals =
        {
            new KeyValuePair<string, int>("只手動更新", 0),
            new KeyValuePair<string, int>("每 15 分鐘", 15),
            new KeyValuePair<string, int>("每 30 分鐘", 30),
            new KeyValuePair<string, int>("每小時", 60),
            new KeyValuePair<string, int>("每 3 小時", 180),
            new KeyValuePair<string, int>("每 6 小時", 360),
            new KeyValuePair<string, int>("每天", 1440)
        };

        // 「ICS 來源」分頁的一張卡片：只管網址與更新頻率（帳號層級的設定）
        private sealed class Card
        {
            public string Id;
            public string SavedUrl;      // 已儲存的網址；新來源為 null
            public TextBox Name;
            public ComboBox Interval;
            public TextBox Url;
            public TextBlock Status;
            public Border Root;
        }

        private readonly Settings _settings;
        private readonly SourceManager _manager;
        private readonly Action<double> _previewOpacity;
        private readonly Action _applied;
        private readonly List<Card> _cards = new List<Card>();

        internal SettingsWindow(Settings settings, SourceManager manager, GoogleManager google, Action<double> previewOpacity, Action applied)
        {
            InitializeComponent();

            // 螢幕比較小時，縮到螢幕放得下的大小
            var area = SystemParameters.WorkArea;
            Width = Math.Min(Width, area.Width - 40);
            Height = Math.Min(Height, area.Height - 40);
            MinWidth = Math.Min(MinWidth, Width);
            MinHeight = Math.Min(MinHeight, Height);

            _settings = settings;
            _manager = manager;
            _google = google;
            _previewOpacity = previewOpacity;
            _applied = applied;

            OpacitySlider.Value = Math.Round(settings.Opacity * 100 / 5) * 5;
            OpacityText.Text = OpacitySlider.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
            OpacitySlider.ValueChanged += delegate
            {
                OpacityText.Text = OpacitySlider.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
                _previewOpacity(OpacitySlider.Value / 100.0);
            };

            AutoStartBox.IsChecked = AutoStart.IsEnabled();
            ReminderBox.IsChecked = settings.HolidayReminder;
            SetupReminderControls();
            SetupEventReminderControls();
            SetupAppearanceControls();

            foreach (var src in manager.Sources) AddCard(src, false);
            BuildGoogleTab();
            BuildIcsMakerTab();
            WireCalendarDrag();
            RebuildCalendarList();

            AddSourceButton.Click += delegate
            {
                var card = AddCard(new CalendarSource(), true);
                card.Name.Focus();
            };
            SaveButton.Click += delegate { SaveAll(); };
            SaveCloseButton.Click += delegate
            {
                if (SaveAll()) Close();
            };
            // 取消：不儲存直接關閉（這是非強制回應視窗，IsCancel 不會自動關；透明度的預覽在 Closed 時還原）
            CancelButton.Click += delegate { Close(); };
            KeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key == System.Windows.Input.Key.Escape && !(System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)) Close();
            };

            manager.StatusChanged += RefreshStatuses;
            Closed += delegate
            {
                _manager.StatusChanged -= RefreshStatuses;
                DisposeGoogleTab();
                _previewOpacity(_settings.Opacity);   // 沒有儲存的預覽一律還原成已儲存的透明度
            };
        }

        // ---------- 外觀：大小、一週的第一天、鎖定位置 ----------

        private static readonly KeyValuePair<string, double>[] Scales =
        {
            new KeyValuePair<string, double>("小（90%）", 0.9),
            new KeyValuePair<string, double>("標準（100%）", 1.0),
            new KeyValuePair<string, double>("大（115%）", 1.15),
            new KeyValuePair<string, double>("特大（130%）", 1.3)
        };

        private void SetupAppearanceControls()
        {
            foreach (var s in Scales) ScaleBox.Items.Add(s.Key);
            int idx = Array.FindIndex(Scales, s => Math.Abs(s.Value - _settings.Scale) < 0.005);
            if (idx < 0)
            {
                // 設定檔裡是別的比例（手動改過）：加進清單，不要被悄悄改掉
                ScaleBox.Items.Add("自訂（" + Math.Round(_settings.Scale * 100) + "%）");
                idx = ScaleBox.Items.Count - 1;
            }
            ScaleBox.SelectedIndex = idx;

            WeekStartBox.Items.Add("週日");
            WeekStartBox.Items.Add("週一");
            WeekStartBox.SelectedIndex = _settings.WeekStartsOnMonday ? 1 : 0;

            LockBox.IsChecked = _settings.LockPosition;

            foreach (var s in DetailScales) DetailScaleBox.Items.Add(s.Key);
            int di = Array.FindIndex(DetailScales, s => Math.Abs(s.Value - _settings.DetailScale) < 0.005);
            if (di < 0)
            {
                DetailScaleBox.Items.Add("自訂（" + Math.Round(_settings.DetailScale * 100) + "%）");
                di = DetailScaleBox.Items.Count - 1;
            }
            DetailScaleBox.SelectedIndex = di;

            foreach (var s in DotScales) DotScaleBox.Items.Add(s.Key);
            int dti = Array.FindIndex(DotScales, s => Math.Abs(s.Value - _settings.DotScale) < 0.005);
            if (dti < 0)
            {
                DotScaleBox.Items.Add("自訂（" + Math.Round(_settings.DotScale * 100) + "%）");
                dti = DotScaleBox.Items.Count - 1;
            }
            DotScaleBox.SelectedIndex = dti;

            FillMonths(LoadPastBox, PastChoices, _settings.LoadPastMonths);
            FillMonths(LoadFutureBox, FutureChoices, _settings.LoadFutureMonths);
        }

        private static readonly KeyValuePair<string, double>[] DetailScales =
        {
            new KeyValuePair<string, double>("標準（100%）", 1.0),
            new KeyValuePair<string, double>("大（120%）", 1.2),
            new KeyValuePair<string, double>("較大（140%）", 1.4),
            new KeyValuePair<string, double>("特大（160%）", 1.6),
            new KeyValuePair<string, double>("超大（190%）", 1.9)
        };

        private static readonly KeyValuePair<string, double>[] DotScales =
        {
            new KeyValuePair<string, double>("標準（100%）", 1.0),
            new KeyValuePair<string, double>("大（140%）", 1.4),
            new KeyValuePair<string, double>("較大（180%）", 1.8),
            new KeyValuePair<string, double>("跟待辦方框一樣大", 2.2)
        };

        private double SelectedDotScale()
        {
            int i = DotScaleBox.SelectedIndex;
            return i >= 0 && i < DotScales.Length ? DotScales[i].Value : _settings.DotScale;
        }

        private static readonly int[] PastChoices = { 2, 3, 6, 12, 24, 36 };
        private static readonly int[] FutureChoices = { 3, 6, 12, 24, 36 };

        private static string MonthsText(int m)
        {
            return m % 12 == 0 ? (m / 12) + " 年" : m + " 個月";
        }

        private static void FillMonths(ComboBox box, int[] choices, int current)
        {
            var list = choices.ToList();
            if (!list.Contains(current)) { list.Add(current); list.Sort(); }   // 設定檔裡手動改成別的數字：保留
            foreach (int m in list) box.Items.Add(MonthsText(m));
            box.SelectedIndex = list.IndexOf(current);
            box.Tag = list;
        }

        private static int SelectedMonths(ComboBox box, int fallback)
        {
            var list = box.Tag as List<int>;
            return list != null && box.SelectedIndex >= 0 && box.SelectedIndex < list.Count ? list[box.SelectedIndex] : fallback;
        }

        private double SelectedDetailScale()
        {
            int i = DetailScaleBox.SelectedIndex;
            return i >= 0 && i < DetailScales.Length ? DetailScales[i].Value : _settings.DetailScale;
        }

        private double SelectedScale()
        {
            int i = ScaleBox.SelectedIndex;
            return i >= 0 && i < Scales.Length ? Scales[i].Value : _settings.Scale;
        }

        // ---------- 行程提醒 ----------

        private static readonly KeyValuePair<string, int>[] EventMinutes =
        {
            new KeyValuePair<string, int>("5 分鐘", 5),
            new KeyValuePair<string, int>("10 分鐘", 10),
            new KeyValuePair<string, int>("15 分鐘", 15),
            new KeyValuePair<string, int>("30 分鐘", 30),
            new KeyValuePair<string, int>("1 小時", 60),
            new KeyValuePair<string, int>("2 小時", 120),
            new KeyValuePair<string, int>("1 天", 1440)
        };

        private void SetupEventReminderControls()
        {
            EventModeBox.Items.Add("依各行程自己的設定（預設）");
            EventModeBox.Items.Add("自訂：所有行程開始前固定的時間");
            EventModeBox.Items.Add("不提醒");
            EventModeBox.SelectedIndex = Math.Max(0, Math.Min(2, _settings.EventReminderMode));

            foreach (var m in EventMinutes) EventMinutesBox.Items.Add(m.Key);
            int idx = Array.FindIndex(EventMinutes, m => m.Value == _settings.EventReminderMinutes);
            if (idx < 0)
            {
                // 設定檔裡是別的分鐘數（手動改過）：加進清單，不要被悄悄改掉
                EventMinutesBox.Items.Add(_settings.EventReminderMinutes + " 分鐘");
                idx = EventMinutesBox.Items.Count - 1;
            }
            EventMinutesBox.SelectedIndex = idx;

            EventModeBox.SelectionChanged += delegate { UpdateEventReminderUi(); };
            UpdateEventReminderUi();
        }

        private void UpdateEventReminderUi()
        {
            int mode = EventModeBox.SelectedIndex;
            EventCustomPanel.Visibility = mode == Settings.EventReminderCustom ? Visibility.Visible : Visibility.Collapsed;
            if (mode == Settings.EventReminderCustom)
                EventReminderHint.Text = "不管行程自己怎麼設定，所有「有時間的行程」都在開始前固定的時間提醒。全天行程沒有「開始前幾分鐘」，不提醒。";
            else if (mode == Settings.EventReminderOff)
                EventReminderHint.Text = "行程不會跳出任何提醒。（節日的提醒不受影響，那是另外的設定。）";
            else
                EventReminderHint.Text = "Google 的行程用你在 Google 日曆上為它設定的提醒，ICS 的行程用檔案裡帶的提醒；行程自己沒有設定提醒的就不提醒。只有類型是「行程」的日曆會提醒，節日有自己的提醒。";
        }

        private int SelectedEventMinutes()
        {
            int i = EventMinutesBox.SelectedIndex;
            return i >= 0 && i < EventMinutes.Length ? EventMinutes[i].Value : _settings.EventReminderMinutes;
        }

        // ---------- 節日提醒的時間 ----------

        private static readonly KeyValuePair<string, int>[] ReminderIntervals =
        {
            new KeyValuePair<string, int>("每 30 分鐘", 30),
            new KeyValuePair<string, int>("每小時", 60),
            new KeyValuePair<string, int>("每 2 小時", 120),
            new KeyValuePair<string, int>("每 3 小時", 180)
        };

        private void SetupReminderControls()
        {
            foreach (var i in ReminderIntervals) ReminderIntervalBox.Items.Add(i.Key);
            int idx = Array.FindIndex(ReminderIntervals, i => i.Value == _settings.ReminderIntervalMinutes);
            if (idx < 0)
            {
                // 設定檔裡是別的間隔（手動改過）：加進清單，不要被悄悄改掉
                ReminderIntervalBox.Items.Add("每 " + _settings.ReminderIntervalMinutes + " 分鐘");
                idx = ReminderIntervalBox.Items.Count - 1;
            }
            ReminderIntervalBox.SelectedIndex = idx;

            for (int h = 0; h <= 23; h++) ReminderStartBox.Items.Add(h.ToString("00") + ":00");
            for (int h = 1; h <= 24; h++) ReminderEndBox.Items.Add(h.ToString("00") + ":00");
            ReminderStartBox.SelectedIndex = _settings.ReminderStartHour;
            ReminderEndBox.SelectedIndex = _settings.ReminderEndHour - 1;

            SelectionChangedEventHandler update = delegate { UpdateReminderHint(); };
            ReminderIntervalBox.SelectionChanged += update;
            ReminderStartBox.SelectionChanged += update;
            ReminderEndBox.SelectionChanged += update;
            UpdateReminderHint();
        }

        private int SelectedReminderInterval()
        {
            int i = ReminderIntervalBox.SelectedIndex;
            return i >= 0 && i < ReminderIntervals.Length ? ReminderIntervals[i].Value : _settings.ReminderIntervalMinutes;
        }

        private void UpdateReminderHint()
        {
            int start = ReminderStartBox.SelectedIndex;
            int end = ReminderEndBox.SelectedIndex + 1;
            if (start < 0 || end < 1) return;

            if (start >= end)
            {
                ReminderHint.Text = "開始時間必須早於結束時間。";
                return;
            }

            DateTime now = DateTime.Now;
            DateTime next = HolidayReminder.NextCheck(now, SelectedReminderInterval(), start, end);
            string when = next.Date == now.Date ? "今天 " : "明天 ";
            ReminderHint.Text = "依照目前的設定，下一次檢查是 " + when + next.ToString("HH:mm") +
                                "。每天從 " + start.ToString("00") + ":00 開始，每隔一段時間檢查一次，到 " + end.ToString("00") + ":00 前為止。";
        }

        // 開發檢查畫面用：把「一般」分頁捲到最底下
        internal void ScrollGeneralToEnd()
        {
            Tabs.SelectedIndex = 0;
            GeneralScroll.ScrollToEnd();
        }

        internal void SelectTab(int index)
        {
            if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
        }

        // ---------- ICS 來源卡片（網址與更新頻率）----------

        private Card AddCard(CalendarSource src, bool isNew)
        {
            var card = new Card { Id = src.Id, SavedUrl = isNew ? null : src.Url };

            card.Name = new TextBox { Text = src.Name, Padding = new Thickness(4, 3, 4, 3), MaxLength = 40, ToolTip = "名稱" };

            card.Interval = new ComboBox { Width = 120, ToolTip = "更新頻率" };
            foreach (var i in Intervals) card.Interval.Items.Add(i.Key);
            int idx = Array.FindIndex(Intervals, i => i.Value == src.IntervalMinutes);
            card.Interval.SelectedIndex = idx >= 0 ? idx : 3;

            card.Url = new TextBox { Text = src.Url, Padding = new Thickness(4, 3, 4, 3), ToolTip = "ICS 網址" };
            card.Status = new TextBlock { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

            var refresh = new Button { Content = "立即更新", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
            refresh.Click += delegate { RefreshCard(card); };
            var delete = new Button { Content = "刪除", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            delete.Click += delegate { DeleteCard(card); };

            var row1 = new Grid();
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.Interval.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(card.Name, 0);
            Grid.SetColumn(card.Interval, 1);
            row1.Children.Add(card.Name);
            row1.Children.Add(card.Interval);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(refresh);
            buttons.Children.Add(delete);

            var row3 = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(card.Status, 0);
            Grid.SetColumn(buttons, 1);
            row3.Children.Add(card.Status);
            row3.Children.Add(buttons);

            var stack = new StackPanel();
            stack.Children.Add(row1);
            card.Url.Margin = new Thickness(0, 8, 0, 8);
            stack.Children.Add(card.Url);
            stack.Children.Add(row3);

            card.Root = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xD8, 0xE3)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 6, 0, 4),
                Child = stack
            };

            _cards.Add(card);
            SourceList.Children.Add(card.Root);
            RefreshStatuses();
            return card;
        }

        private void RefreshStatuses()
        {
            foreach (var card in _cards)
            {
                card.Status.Text = card.SavedUrl == null ? "尚未儲存" : _manager.StatusText(card.Id);
            }
        }

        // 按「立即更新」時，先把這一個來源的設定記下來（新增或剛改過都一樣），再去抓；
        // 只會儲存這一個來源，其他還沒按儲存的修改（其他卡片、透明度等）不受影響
        private void RefreshCard(Card card)
        {
            CalendarSource src = ToSource(card, _cards.IndexOf(card) + 1);
            card.Name.Text = src.Name;

            if (src.Url.Length == 0)
            {
                MessageBox.Show(this, "請先貼上 ICS 網址。", "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!src.Enabled)
            {
                MessageBox.Show(this, "這個日曆目前沒有勾選顯示，請先到「日曆」分頁勾選。", "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var list = _manager.Sources;
            int i = list.FindIndex(s => s.Id == src.Id);
            if (i >= 0) list[i] = src; else list.Add(src);

            _manager.Apply(list);           // 網址是新的或改過，這裡就會開始抓
            card.SavedUrl = src.Url;
            _manager.RefreshNow(card.Id);   // 網址沒變時 Apply 不會重抓，這裡補上要求（正在抓的話會自動略過）
            RefreshStatuses();
            RebuildCalendarList();          // 新增的來源要出現在「日曆」分頁
        }

        // 網址、名稱、頻率來自這張卡片；顯示與否、類型、顏色來自「日曆」分頁（新來源還沒有那一列時用預設值）
        private CalendarSource ToSource(Card card, int position)
        {
            string name = card.Name.Text.Trim();
            var row = FindRow(CalendarIds.Ics(card.Id));
            return new CalendarSource
            {
                Id = card.Id,
                Name = name.Length == 0 ? "日曆 " + position : name,
                Url = card.Url.Text.Trim(),
                IntervalMinutes = Intervals[Math.Max(0, card.Interval.SelectedIndex)].Value,
                Enabled = row == null || row.Enabled,
                Kind = row == null ? ItemKind.Event : row.Kind,
                Color = row == null ? "#6CB4FF" : row.Color
            };
        }

        private void DeleteCard(Card card)
        {
            string name = string.IsNullOrWhiteSpace(card.Name.Text) ? "這個來源" : "「" + card.Name.Text.Trim() + "」";
            var answer = MessageBox.Show(this, "確定要刪除 " + name + " 嗎？\n（按「儲存」之後才會真正生效）", "月曆小工具",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            _cards.Remove(card);
            SourceList.Children.Remove(card.Root);
            RemoveRow(CalendarIds.Ics(card.Id));
        }

        // ---------- 儲存 ----------

        // 儲存全部設定；不關閉視窗（「儲存並關閉」會在這之後再關）
        private bool SaveAll()
        {
            int reminderStart = ReminderStartBox.SelectedIndex;
            int reminderEnd = ReminderEndBox.SelectedIndex + 1;
            if (reminderStart >= reminderEnd)
            {
                Tabs.SelectedIndex = 0;
                MessageBox.Show(this, "節日提醒的開始時間必須早於結束時間，請調整後再儲存。", "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var list = new List<CalendarSource>();
            for (int i = 0; i < _cards.Count; i++) list.Add(ToSource(_cards[i], i + 1));

            var localRow = FindRow(CalendarIds.Local);
            _settings.Opacity = Settings.ClampOpacity(OpacitySlider.Value / 100.0);
            if (localRow != null) _settings.TodoColor = localRow.Color;
            var localEventRow = FindRow(CalendarIds.LocalEvents);
            if (localEventRow != null) _settings.LocalEventColor = localEventRow.Color;
            _settings.Scale = SelectedScale();
            _settings.DetailScale = SelectedDetailScale();
            _settings.DotScale = SelectedDotScale();
            int oldPast = _settings.LoadPastMonths, oldFuture = _settings.LoadFutureMonths;
            _settings.LoadPastMonths = SelectedMonths(LoadPastBox, oldPast);
            _settings.LoadFutureMonths = SelectedMonths(LoadFutureBox, oldFuture);
            bool rangeChanged = oldPast != _settings.LoadPastMonths || oldFuture != _settings.LoadFutureMonths;
            _settings.WeekStartsOnMonday = WeekStartBox.SelectedIndex == 1;
            _settings.LockPosition = LockBox.IsChecked == true;
            _settings.EventReminderMode = Math.Max(0, EventModeBox.SelectedIndex);
            _settings.EventReminderMinutes = SelectedEventMinutes();
            _settings.HolidayReminder = ReminderBox.IsChecked == true;
            _settings.ReminderIntervalMinutes = SelectedReminderInterval();
            _settings.ReminderStartHour = reminderStart;
            _settings.ReminderEndHour = reminderEnd;
            _settings.Save();

            bool autoStart = AutoStartBox.IsChecked == true;
            if (autoStart != AutoStart.IsEnabled()) AutoStart.Set(autoStart);

            LoadRange.Set(_settings);
            _manager.Apply(list);
            SaveGoogle();
            if (rangeChanged)
            {
                _manager.Reparse();      // ICS：用已下載的內容依新範圍重新解析
                _google.RefreshNow();    // Google：要重新抓
            }
            CalendarOrder.Save(_calRows.Select(r => r.Id));

            for (int i = 0; i < _cards.Count; i++)
            {
                _cards[i].Name.Text = list[i].Name;
                _cards[i].SavedUrl = list[i].Url;
            }
            RebuildCalendarList();   // 名稱可能被自動命名了
            _applied();
            RefreshStatuses();
            return true;
        }
    }
}

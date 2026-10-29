using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CalendarWidget
{
    public partial class MainWindow : Window
    {
        private const double CellWidth = 36;
        private const double CellHeight = 38;
        private const int MaxDetailRows = 8;
        private static readonly string[] WeekdayNames = { "日", "一", "二", "三", "四", "五", "六" };

        private static readonly Brush TextNormal = Frozen("#E8E8F4");
        private static readonly Brush TextDim = Frozen("#6C6C84");     // 月曆格子裡「不是這個月」的日期、星期列：本來就要淡
        private static readonly Brush TextMuted = Frozen("#B3B5CC");   // 下方區塊的提示文字（無事項、新增待辦、還有 N 項、刪除鈕）：要看得清楚
        private static readonly Brush TextTime = Frozen("#A9ABC3");    // 行程前面的時間
        private static readonly Brush TextDone = Frozen("#8E90A8");    // 已完成的項目（有刪除線，仍然要讀得到）
        private static readonly Brush TextWeekend = Frozen("#FF9AA2");
        private static readonly Brush TodayFill = Frozen("#5B8DEF");
        private static readonly Brush HoverFill = Frozen("#33FFFFFF");
        private static readonly Brush HoverFillWeak = Frozen("#14FFFFFF");
        private static readonly Brush SelectedBorder = Frozen("#CCFFFFFF");

        private static readonly Dictionary<string, Brush> BrushCache = new Dictionary<string, Brush>();

        // 項目與來源的顏色都是 "#RRGGBB" 字串，這裡轉成可重複使用的 Brush
        private static Brush BrushFor(string hex)
        {
            Brush brush;
            if (hex == null || !Colors.IsValidHex(hex)) hex = "#6CB4FF";
            if (!BrushCache.TryGetValue(hex, out brush))
            {
                brush = Frozen(hex);
                BrushCache[hex] = brush;
            }
            return brush;
        }

        private readonly ItemStore _store;
        private readonly LocalTodoStore _todos;
        private readonly LocalEventStore _localEvents;
        private readonly SourceManager _sources;
        private readonly GoogleManager _google;
        private readonly AccountRegistry _accounts;
        private readonly OffDayStore _offDays = new OffDayStore();
        private readonly TyphoonManager _typhoon;
        private readonly TyphoonReminder _typhoonReminder;
        private readonly HolidayReminder _reminder;
        private readonly EventReminder _eventReminder;
        private SettingsWindow _settingsWindow;

        internal HolidayReminder Reminder { get { return _reminder; } }
        internal EventReminder EventReminder { get { return _eventReminder; } }
        private IntPtr _hwnd;
        private string _pendingDeleteId;
        private readonly Settings _settings = Settings.Load();
        private readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _midnightTimer = new DispatcherTimer();
        private TrayIcon _tray;
        private DateTime _viewMonth;
        private DateTime _selected;
        private DateTime _lastToday;
        // 點月份標題會一層一層往上：日期 → 選月份 → 選年份（再點一次回到日期）
        private enum PickerLevel { None, Months, Years }
        private PickerLevel _pickerLevel = PickerLevel.None;
        private int _pickerYear;
        private int _yearPageStart;   // 選年份畫面這一頁的第一個年份（一頁 12 個）
        private readonly WheelStepper _wheel = new WheelStepper();
        private bool _trimQueued;
        private readonly Dictionary<DateTime, Border> _cells = new Dictionary<DateTime, Border>();
        private Style _dayTipStyle;

        internal MainWindow(ItemStore store, LocalTodoStore todos, LocalEventStore localEvents, SourceManager sources, GoogleManager google, AccountRegistry accounts, int selectOffsetDays)
        {
            InitializeComponent();

            _store = store;
            _todos = todos;
            _localEvents = localEvents;
            _sources = sources;
            _google = google;
            _accounts = accounts;
            _sources.Changed += delegate
            {
                BuildCalendar();
                TrimMemory();
                _eventReminder.Recheck();   // 行程更新了，可能有新的提醒
            };
            _accounts.Changed += delegate
            {
                BuildCalendar();
                TrimMemory();
                _eventReminder.Recheck();
            };
            _offDays.Enabled = _settings.MarkOffDays;
            _offDays.Changed += delegate { Dispatcher.BeginInvoke(new Action(BuildCalendar)); };   // 背景下載好行事曆了，重畫紅字
            Opacity = _settings.Opacity;
            _reminder = new HolidayReminder(_store, _settings, AckStore.Load(DateTime.Today));
            _reminder.Start();
            _eventReminder = new EventReminder(_store, _settings, EventAckStore.Load(DateTime.Now));
            _eventReminder.Recheck();
            _typhoon = new TyphoonManager(Dispatcher, _settings);
            _typhoonReminder = new TyphoonReminder(_typhoon.Alerts, _settings, AckStore.Load(DateTime.Today, "typhoon-acked.txt"));
            _typhoon.Changed += delegate
            {
                BuildCalendar();                  // 公告有變，紅字要跟著變
                _typhoonReminder.OnDataChanged();   // 有還沒確認的公告就跳出提醒
            };
            RefreshLocalSources();
            _lastToday = DateTime.Today;
            _selected = _lastToday.AddDays(selectOffsetDays);
            _viewMonth = FirstOfMonth(_selected);
            _dayTipStyle = (Style)FindResource("DayTip");
            ApplyScale();
            ApplyCollapsed();
            BuildWeekdayRow();
            BuildCalendar();
            RestorePosition();
            WireEvents();
            CreateTray();
            ScheduleMidnightRefresh();
            _typhoon.Apply();   // 功能開著才會讀取；關著就什麼都不做
            _typhoonReminder.Start();
            ContentRendered += delegate
            {
                TrimMemory();
                ClampToScreen();
            };
        }

        // 月曆整體大小：連文字一起等比例縮放
        private void ApplyScale()
        {
            double s = Settings.ClampScale(_settings.Scale);
            Root.LayoutTransform = Math.Abs(s - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);

            // 下方「選取日期的行程與待辦」可以另外調大小
            DetailTitle.FontSize = Fs(13);
            CollapseGlyph.FontSize = Fs(13);
            TodoInput.FontSize = Fs(12);
            TodoHint.FontSize = Fs(12);
        }

        private double DetailFactor { get { return Settings.ClampDetailScale(_settings.DetailScale); } }
        private double DotFactor { get { return Settings.ClampDotScale(_settings.DotScale); } }

        // 下方清單的字級：基本字級乘上「下方清單的大小」，以 0.5 為單位
        private double Fs(double basic)
        {
            return Math.Round(basic * DetailFactor * 2) / 2;
        }

        // 放大或展開後視窗可能超出螢幕，把它拉回來（用整個虛擬螢幕的範圍，多螢幕時不會被誤拉到主螢幕）
        private void ClampToScreen()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
            {
                double right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
                double bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
                double left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(Left, right - ActualWidth));
                double top = Math.Max(SystemParameters.VirtualScreenTop, Math.Min(Top, bottom - ActualHeight));
                if (Math.Abs(left - Left) > 0.5) Left = left;
                if (Math.Abs(top - Top) > 0.5) Top = top;
            }));
        }

        private int FirstDow { get { return _settings.WeekStartsOnMonday ? 1 : 0; } }

        private static Brush Frozen(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            brush.Freeze();
            return brush;
        }

        private static DateTime FirstOfMonth(DateTime d)
        {
            return new DateTime(d.Year, d.Month, 1);
        }

        // ---------- 畫面 ----------

        // 星期列：依「一週從週日還是週一開始」的設定排列
        private void BuildWeekdayRow()
        {
            WeekdayRow.Children.Clear();
            for (int i = 0; i < 7; i++)
            {
                int dow = (FirstDow + i) % 7;
                WeekdayRow.Children.Add(MakeCell(WeekdayNames[dow], dow == 0 || dow == 6 ? TextWeekend : TextDim, null));
            }
        }

        private void BuildCalendar()
        {
            DayGrid.Children.Clear();
            _cells.Clear();

            if (_pickerLevel != PickerLevel.None)
            {
                WeekdayRow.Visibility = Visibility.Collapsed;
                DayGrid.Visibility = Visibility.Collapsed;
                MonthPicker.Visibility = Visibility.Visible;
                MonthButton.Visibility = Visibility.Collapsed;   // 選月份、選年份的畫面只留年這一塊文字
                if (_pickerLevel == PickerLevel.Months)
                {
                    // 選月份：只做月份。上面的年份只是告訴你現在選的是哪一年（‹ › 可以換年），不能點
                    YearText.Text = _pickerYear + " 年";
                    YearButton.IsHitTestVisible = false;
                    BuildMonthPicker();
                }
                else
                {
                    // 選年份：只做年份；再點一次這塊文字就取消
                    YearText.Text = _yearPageStart + " – " + (_yearPageStart + 11);
                    YearButton.IsHitTestVisible = true;
                    BuildYearPicker();
                }
            }
            else
            {
                YearText.Text = _viewMonth.Year + " 年";
                MonthText.Text = _viewMonth.Month + " 月";
                YearButton.IsHitTestVisible = true;
                MonthButton.Visibility = Visibility.Visible;
                MonthPicker.Visibility = Visibility.Collapsed;
                WeekdayRow.Visibility = Visibility.Visible;
                DayGrid.Visibility = Visibility.Visible;

                DateTime start = _viewMonth.AddDays(-((((int)_viewMonth.DayOfWeek - FirstDow) + 7) % 7));
                _offDays.Prefetch(start, start.AddDays(41), DateTime.Today);
                for (int i = 0; i < 42; i++)
                {
                    DateTime date = start.AddDays(i);
                    var cell = MakeDayCell(date);
                    _cells[date] = cell;
                    DayGrid.Children.Add(cell);
                }
            }
            BuildDetail();
        }

        // ---------- 選年月 ----------

        private void BuildMonthPicker()
        {
            MonthPicker.Children.Clear();
            DateTime today = DateTime.Today;
            for (int m = 1; m <= 12; m++)
            {
                int month = m;
                bool isToday = _pickerYear == today.Year && m == today.Month;
                bool isViewed = _pickerYear == _viewMonth.Year && m == _viewMonth.Month;

                var cell = new Border
                {
                    Margin = new Thickness(3),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = isViewed ? SelectedBorder : Brushes.Transparent,
                    Background = isToday ? TodayFill : HoverFillWeak,
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = m + " 月",
                        FontSize = 14,
                        Foreground = isToday ? Brushes.White : TextNormal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                cell.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
                cell.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    SelectMonth(month);
                };
                MonthPicker.Children.Add(cell);
            }
        }

        // 選年份：一頁 12 個年份，今天所在的年份是藍色，目前看的那一年有外框
        private void BuildYearPicker()
        {
            MonthPicker.Children.Clear();
            int thisYear = DateTime.Today.Year;
            for (int i = 0; i < 12; i++)
            {
                int year = _yearPageStart + i;
                bool isToday = year == thisYear;
                bool isViewed = year == _viewMonth.Year;

                var cell = new Border
                {
                    Margin = new Thickness(3),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = isViewed ? SelectedBorder : Brushes.Transparent,
                    Background = isToday ? TodayFill : HoverFillWeak,
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = year.ToString(),
                        FontSize = 14,
                        Foreground = isToday ? Brushes.White : TextNormal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                cell.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
                cell.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    SelectYear(year);
                };
                MonthPicker.Children.Add(cell);
            }
        }

        // 年只管年：選好年份就直接回到日期畫面，月份維持原本看的那個月，不用再選月份
        private void SelectYear(int year)
        {
            ShowMonth(new DateTime(year, _viewMonth.Month, 1));
        }

        // 月只管月：選好月份就直接回到日期畫面（年份是選月份畫面上顯示的那一年）
        private void SelectMonth(int month)
        {
            ShowMonth(new DateTime(_pickerYear, month, 1));
        }

        // 點「年」：選年份。再點一次年就取消、回到日期畫面。（選月份的畫面裡，年只是文字，不能點）
        private void OnYearClicked()
        {
            if (_pickerLevel == PickerLevel.Years)
            {
                _pickerLevel = PickerLevel.None;
            }
            else
            {
                _pickerLevel = PickerLevel.Years;
                _yearPageStart = ClampYearPage(_viewMonth.Year - 4);
            }
            BuildCalendar();
        }

        // 點「月」：選月份（只有日期畫面才看得到這個按鈕）
        private void OnMonthClicked()
        {
            _pickerLevel = PickerLevel.Months;
            _pickerYear = _viewMonth.Year;
            BuildCalendar();
        }

        private static int ClampYearPage(int start)
        {
            return Math.Max(1, Math.Min(9999 - 11, start));
        }

        // 往前或往後一格：日期畫面換月、選月份畫面換年、選年份畫面換一頁（12 年）
        private void Step(int direction)
        {
            switch (_pickerLevel)
            {
                case PickerLevel.Months:
                    _pickerYear = Math.Max(1, Math.Min(9999, _pickerYear + direction));
                    BuildCalendar();
                    break;
                case PickerLevel.Years:
                    _yearPageStart = ClampYearPage(_yearPageStart + 12 * direction);
                    BuildCalendar();
                    break;
                default:
                    if (direction < 0 && _viewMonth.Year <= 1 && _viewMonth.Month == 1) return;
                    if (direction > 0 && _viewMonth.Year >= 9999 && _viewMonth.Month == 12) return;
                    ShowMonth(_viewMonth.AddMonths(direction));
                    break;
            }
        }

        // 滾輪：往上滾 = 前一個，往下滾 = 後一個。滑鼠在下方詳細區塊上時不處理。
        // 怎麼把滾動量換算成格數，見 WheelStepper
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (DetailPanel.IsMouseOver) return;

            int steps = _wheel.Feed(e.Delta, Environment.TickCount);
            int direction = steps < 0 ? -1 : 1;
            for (int i = 0; i < Math.Abs(steps); i++) Step(direction);
            e.Handled = true;
        }

        // ---------- 收合下方的詳細區塊 ----------

        private void ApplyCollapsed()
        {
            bool collapsed = _settings.DetailCollapsed;
            DetailBody.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            CollapseGlyph.Text = collapsed ? "▾" : "▴";
            DetailHeader.ToolTip = collapsed ? "點一下展開" : "點一下收合";
        }

        private void ToggleCollapsed()
        {
            _settings.DetailCollapsed = !_settings.DetailCollapsed;
            _settings.Save();
            if (_settings.DetailCollapsed && TodoInput.IsKeyboardFocused) Keyboard.ClearFocus();
            ApplyCollapsed();
            TrimMemory();
            ClampToScreen();   // 展開後視窗變高，避免超出螢幕下緣
        }

        // ---------- 滑到日期上顯示當天內容 ----------

        // 填入提示的內容；當天沒有任何事項就回傳 false（不顯示提示）
        private bool FillDayTip(ToolTip tip, DateTime date)
        {
            var items = _store.For(date);
            if (items.Count == 0) return false;

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = date.ToString("M 月 d 日") + "　週" + WeekdayNames[(int)date.DayOfWeek] + (date == DateTime.Today ? "（今天）" : ""),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Frozen("#F5F5FF"),
                Margin = new Thickness(0, 0, 0, 5)
            });

            const int maxRows = 10;
            for (int i = 0; i < items.Count && i < maxRows; i++)
            {
                var item = items[i];
                var row = new DockPanel { Margin = new Thickness(0, 1.5, 0, 1.5) };
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Margin = new Thickness(0, 5, 7, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Fill = BrushFor(item.Color)
                };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);

                var label = new TextBlock
                {
                    FontSize = 12,
                    MaxWidth = 260,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = item.Done ? TextDone : TextNormal,
                    TextDecorations = item.Done ? TextDecorations.Strikethrough : null
                };
                if (item.TimeText != null) label.Inlines.Add(new System.Windows.Documents.Run(item.TimeText + "  ") { Foreground = TextTime });
                label.Inlines.Add(new System.Windows.Documents.Run(item.Title));
                row.Children.Add(label);
                panel.Children.Add(row);
            }
            if (items.Count > maxRows)
            {
                panel.Children.Add(new TextBlock { Text = "…還有 " + (items.Count - maxRows) + " 項", FontSize = 12, Foreground = TextMuted, Margin = new Thickness(13, 2, 0, 0) });
            }
            tip.Content = panel;
            return true;
        }

        // 開發檢查畫面用：直接進入選年月畫面
        // 開發檢查畫面用：在目前的選年份／選月份畫面「點」第 N 個（年份畫面是年份數字，月份畫面是 1~12）
        internal void PickForTest(int value)
        {
            if (_pickerLevel == PickerLevel.Years) SelectYear(value);
            else if (_pickerLevel == PickerLevel.Months) SelectMonth(value);
        }

        internal void StepForTest(int steps)
        {
            for (int i = 0; i < Math.Abs(steps); i++) Step(steps < 0 ? -1 : 1);
        }

        internal void ShowPickerForTest(bool years)
        {
            _pickerYear = _viewMonth.Year;
            if (years) OnYearClicked(); else OnMonthClicked();
        }

        // 開發檢查畫面用：不需要真的把滑鼠移上去，直接把某一天的提示打開
        internal void ShowDayTipForTest(int dayOffset)
        {
            DateTime date = DateTime.Today.AddDays(dayOffset);
            Border cell;
            if (!_cells.TryGetValue(date, out cell)) return;
            var tip = cell.ToolTip as ToolTip;
            if (tip == null || !FillDayTip(tip, date)) return;
            tip.PlacementTarget = cell;
            tip.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            tip.IsOpen = true;
        }

        private Border MakeDayCell(DateTime date)
        {
            bool inMonth = date.Month == _viewMonth.Month;
            bool off = _offDays.IsOff(date) || _typhoon.IsOff(date);   // 週六日，加上官方公告的平日放假（補班的週六日不算），以及關注的縣市的停班停課
            bool today = date == DateTime.Today;
            bool selected = date == _selected;

            Brush fore = today ? Brushes.White : (!inMonth ? TextDim : (off ? TextWeekend : TextNormal));

            var number = new TextBlock
            {
                Text = date.Day.ToString(),
                FontSize = 13,
                Foreground = fore,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 一個格子最多顯示 4 個圓點（依來源顏色，同色只畫一個）
            foreach (string color in _store.For(date).Select(i => i.Color).Distinct().Take(4))
            {
                dots.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 5,     // 日曆格子裡的圓點固定大小，不受「圓點大小」設定影響（只有下方清單的圓點可調）
                    Height = 5,
                    Margin = new Thickness(1, 0, 1, 0),
                    Fill = BrushFor(color)
                });
            }

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            Grid.SetRow(number, 0);
            Grid.SetRow(dots, 1);
            content.Children.Add(number);
            content.Children.Add(dots);

            var cell = new Border
            {
                Width = CellWidth,
                Height = CellHeight,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1.5),
                BorderBrush = selected ? SelectedBorder : Brushes.Transparent,
                Background = today ? TodayFill : Brushes.Transparent,
                Child = content
            };

            // 滑鼠移上去顯示當天的內容（只有當天有事項才會顯示）
            var tip = new ToolTip { Style = _dayTipStyle, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            cell.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(cell, 250);
            ToolTipService.SetShowDuration(cell, 30000);
            cell.ToolTipOpening += delegate(object s, ToolTipEventArgs e)
            {
                if (!FillDayTip(tip, date)) e.Handled = true;
            };

            // 日期格子自己處理點擊（不讓視窗把它當成拖曳）
            cell.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            cell.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                SelectDate(date);
            };

            // 按右鍵：先跳出選單，再選「新增行程」（避免誤觸直接開視窗）
            cell.MouseRightButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            cell.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ShowDayMenu(date);
            };
            return cell;
        }

        // 日期格子的右鍵選單。選取日期會重畫月曆（格子會被換掉），所以先選取、再用滑鼠位置開選單
        private void ShowDayMenu(DateTime date, bool atWindow = false)
        {
            SelectDate(date);

            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            if (atWindow)
            {
                // 開發檢查畫面用：沒有真的滑鼠位置，固定開在視窗中間偏上
                menu.PlacementTarget = this;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
                menu.HorizontalOffset = 90;
                menu.VerticalOffset = 190;
            }
            var add = new MenuItem { Header = "新增行程（" + date.ToString("M 月 d 日") + "）" };
            add.Click += delegate { OpenAddEvent(date); };
            menu.Items.Add(add);
            menu.Closed += delegate { TrimMemory(); };
            menu.IsOpen = true;
        }

        private void SelectDate(DateTime date)
        {
            _pendingDeleteId = null;
            _pickerLevel = PickerLevel.None;
            _selected = date;
            if (date.Month != _viewMonth.Month || date.Year != _viewMonth.Year)
                _viewMonth = FirstOfMonth(date);
            BuildCalendar();
        }

        private void BuildDetail()
        {
            string title = _selected.ToString("M 月 d 日") + "　週" + WeekdayNames[(int)_selected.DayOfWeek];
            if (_selected == DateTime.Today) title += "（今天）";
            string offNote = _offDays.Describe(_selected);
            if (offNote != null) title += "　" + offNote;
            DetailTitle.Text = title;

            DetailList.Children.Clear();
            // 颱風假的說明比較長，放在標題下面一行（紅字），不要擠在標題後面把視窗撐寬
            string typhoonNote = _typhoon.Describe(_selected);
            if (typhoonNote != null)
            {
                DetailList.Children.Add(new TextBlock
                {
                    Text = typhoonNote,
                    FontSize = Fs(12),
                    Foreground = TextWeekend,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 260,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 3)
                });
            }
            var items = _store.For(_selected);
            if (items.Count == 0)
            {
                DetailList.Children.Add(new TextBlock { Text = "無事項", FontSize = Fs(12), Foreground = TextMuted });
                return;
            }

            for (int i = 0; i < items.Count && i < MaxDetailRows; i++)
            {
                DetailList.Children.Add(MakeDetailRow(items[i]));
            }
            if (items.Count > MaxDetailRows)
            {
                DetailList.Children.Add(new TextBlock
                {
                    Text = "…還有 " + (items.Count - MaxDetailRows) + " 項",
                    FontSize = Fs(12),
                    Foreground = TextMuted,
                    Margin = new Thickness(11, 2, 0, 0)
                });
            }
        }

        // 刪除確認列：用在原位置顯示，不開新視窗（桌面小工具視窗在最底層，彈出的對話框可能被其他視窗蓋住）
        private UIElement MakeDeleteConfirmRow(CalendarItem item)
        {
            string title = item.Title.Length > 18 ? item.Title.Substring(0, 18) + "…" : item.Title;
            string id = item.Id;
            bool isLocalEvent = item.CalendarId == CalendarIds.LocalEvents;
            var onlineRef = isLocalEvent || item.Kind == ItemKind.LocalTodo ? null : _accounts.Resolve(item);

            // 跨多天的整天行程：每一天都有一筆，但刪除會刪掉整個行程
            bool multiDay = item.AllDay && item.End.HasValue && (!item.Start.HasValue || item.End.Value > item.Date.AddDays(1));
            string question = "確定刪除「" + title + "」？" + (multiDay ? "（整個行程，所有天數）" : "");
            if (onlineRef != null)
                question = "確定從 " + onlineRef.Account.Name + " 日曆刪除「" + title + "」？" + (item.Recurring ? "（重複行程，只刪這一次）" : multiDay ? "（整個行程，所有天數）" : "");

            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            var questionText = new TextBlock
            {
                Text = question,
                FontSize = Fs(12),
                Foreground = TextNormal,
                MaxWidth = 236,
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(questionText);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
            buttons.Children.Add(MakePillButton("刪除", Frozen("#66FF6B7A"), delegate
            {
                if (onlineRef != null)
                {
                    // 線上帳號的刪除要連線，先把按鈕換成進度文字；完成後重新抓資料，行程才會從月曆上消失
                    buttons.Visibility = Visibility.Collapsed;
                    questionText.Text = "刪除中…";
                    onlineRef.Account.DeleteEvent(onlineRef.Calendar.RemoteId, id,
                        delegate(string progress) { questionText.Text = progress; },
                        delegate(string error)
                        {
                            if (error == null)
                            {
                                _pendingDeleteId = null;
                                BuildCalendar();
                                return;
                            }
                            questionText.Text = "刪除失敗：" + error;
                            questionText.Foreground = TextWeekend;
                            buttons.Visibility = Visibility.Visible;
                        });
                    return;
                }
                _pendingDeleteId = null;
                if (isLocalEvent) _localEvents.Remove(id);
                else _todos.Remove(id);
                OnTodosChanged();   // 同時更新本機待辦和本機行程這兩個來源，並重畫
            }));
            buttons.Children.Add(MakePillButton("取消", HoverFill, delegate
            {
                _pendingDeleteId = null;
                BuildDetail();
            }));
            panel.Children.Add(buttons);
            return panel;
        }

        // 刪除確認用的識別：同一個行程 id 可能出現在不同日曆（例如被邀請的行程），所以連日曆一起算
        private static string DeleteKey(CalendarItem item)
        {
            return item.Id == null ? null : (item.CalendarId ?? "") + "|" + item.Id;
        }

        private Border MakePillButton(string text, Brush fill, Action onClick)
        {
            var pill = new Border
            {
                Background = fill,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, FontSize = Fs(12), Foreground = TextNormal }
            };
            pill.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            pill.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; onClick(); };
            return pill;
        }

        private UIElement MakeDetailRow(CalendarItem item)
        {
            string key = DeleteKey(item);
            if (key != null && key == _pendingDeleteId) return MakeDeleteConfirmRow(item);

            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };
            bool isTodo = item.Kind == ItemKind.LocalTodo && item.Id != null;
            bool isLocalEvent = item.Kind == ItemKind.Event && item.Id != null && item.CalendarId == CalendarIds.LocalEvents;
            bool isOnlineEvent = !isTodo && !isLocalEvent && _accounts.Resolve(item) != null;   // 你有編輯權限的線上帳號日曆
            bool canDelete = isTodo || isLocalEvent || isOnlineEvent;
            double boxSize = 13 * DetailFactor;
            bool canEdit = (isLocalEvent || isOnlineEvent) && item.IsEditable;   // 要知道結束時間，跨多天整天行程只有第一天那筆可改
            Brush kindBrush = BrushFor(item.Color);

            if (isTodo)
            {
                // 可勾選的方框
                var box = new Border
                {
                    Width = boxSize,
                    Height = boxSize,
                    Margin = new Thickness(0, 2 * DetailFactor, 6, 0),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = kindBrush,
                    Background = item.Done ? kindBrush : Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Top,
                    Cursor = Cursors.Hand,
                    Child = item.Done
                        ? new TextBlock { Text = "✓", FontSize = Fs(10), Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                        : null
                };
                DockPanel.SetDock(box, Dock.Left);
                string id = item.Id;
                box.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
                box.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    _todos.Toggle(id);
                    OnTodosChanged();
                };
                row.Children.Add(box);
                row.Children.Add(MakeDeleteGlyph(key));
            }
            else
            {
                // 圓點放在跟待辦方框一樣寬的欄位正中央，文字的左邊才會跟本機待辦對齊
                double dotSize = 6 * DetailFactor * DotFactor;
                double dotSide = Math.Max(0, (boxSize - dotSize) / 2);
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = dotSize,
                    Height = dotSize,
                    Margin = new Thickness(dotSide, 2 * DetailFactor + dotSide, dotSide + 6, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Fill = kindBrush
                };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                if (isLocalEvent || isOnlineEvent) row.Children.Add(MakeDeleteGlyph(key));   // 本機行程、你能編輯的 Google 行程可以刪（ICS 等其他來源不行）
                if (canEdit) row.Children.Add(MakeEditGlyph(item));
            }

            var label = new TextBlock
            {
                FontSize = Fs(12),
                // 有 MaxWidth 的元素在剩餘空間比它寬時會被置中；要明確靠左，不同種類的列文字左緣才會對齊
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = canEdit ? 185 : canDelete ? 205 : 236,
                TextWrapping = TextWrapping.Wrap,
                Foreground = item.Done ? TextDone : TextNormal,
                TextDecorations = item.Done ? TextDecorations.Strikethrough : null
            };
            if (item.TimeText != null)
            {
                label.Inlines.Add(new System.Windows.Documents.Run(item.TimeText + "  ") { Foreground = TextTime });
            }
            label.Inlines.Add(new System.Windows.Documents.Run(item.Title));
            row.Children.Add(label);
            return row;
        }

        // 右邊的 ✕：不直接刪除，先在原位改成「確定刪除？」的確認列
        private TextBlock MakeDeleteGlyph(string key)
        {
            var del = new TextBlock
            {
                Text = "✕",
                FontSize = Fs(11),
                Foreground = TextMuted,
                Margin = new Thickness(6, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = "刪除"
            };
            DockPanel.SetDock(del, Dock.Right);
            del.MouseEnter += delegate { del.Foreground = TextWeekend; };
            del.MouseLeave += delegate { del.Foreground = TextMuted; };
            del.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            del.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                _pendingDeleteId = key;
                BuildDetail();
            };
            return del;
        }

        // ✎：打開編輯視窗
        private TextBlock MakeEditGlyph(CalendarItem item)
        {
            var edit = new TextBlock
            {
                Text = "✎",
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = Fs(12),
                Foreground = TextMuted,
                Margin = new Thickness(6, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = "編輯"
            };
            DockPanel.SetDock(edit, Dock.Right);
            edit.MouseEnter += delegate { edit.Foreground = TextNormal; };
            edit.MouseLeave += delegate { edit.Foreground = TextMuted; };
            edit.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            edit.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                OpenAddEvent(item.Date, item);
            };
            return edit;
        }

        // 本機待辦和本機行程這兩個「只存在這台電腦」的來源
        private void RefreshLocalSources()
        {
            _store.SetSource("local", _todos.ToItems(_settings.TodoColor));
            _store.SetSource("localevents", _localEvents.ToItems(_settings.LocalEventColor));
        }

        private void OnTodosChanged()
        {
            _pendingDeleteId = null;
            RefreshLocalSources();
            BuildCalendar();
            _eventReminder.Recheck();   // 本機行程有增減，提醒要重新算
        }

        // ---------- 在日期上按右鍵新增行程 ----------

        private AddEventWindow _addWindow;

        // editing 不是 null 時是「編輯」那個行程
        internal void OpenAddEvent(DateTime date, CalendarItem editing = null)
        {
            if (_addWindow != null)
            {
                WindowHelper.BringToFront(_addWindow);
                return;
            }
            _addWindow = new AddEventWindow(date, _accounts, _localEvents, _settings, OnTodosChanged, editing);
            _addWindow.Closed += delegate
            {
                _addWindow = null;
                TrimMemory();
            };
            _addWindow.Show();
            WindowHelper.BringToFront(_addWindow);
        }

        // 開發檢查畫面用：直接跳出「今天起第 N 天」的右鍵選單
        internal void OpenDayMenuForTest(int dayOffset)
        {
            ShowDayMenu(DateTime.Today.AddDays(dayOffset), true);
        }

        // 開發檢查畫面用：把選取那天的第 N 項（從 0 算）直接變成「確定刪除？」的確認列
        internal void AskDeleteForTest(int index)
        {
            var items = _store.For(_selected);
            if (index < items.Count) _pendingDeleteId = DeleteKey(items[index]);
            BuildDetail();
        }

        // 開發檢查畫面用：直接打開選取那天第 N 項（從 0 算）的編輯視窗
        internal void OpenEditForTest(int index)
        {
            var items = _store.For(_selected);
            if (index < items.Count) OpenAddEvent(_selected, items[index]);
        }

        // 開發檢查畫面用：直接打開「今天起第 N 天」的新增行程視窗（openPicker：同時展開開始日期的選擇月曆）
        internal void OpenAddEventForTest(int dayOffset, bool openPicker = false)
        {
            OpenAddEvent(DateTime.Today.AddDays(dayOffset));
            if (openPicker && _addWindow != null) _addWindow.Dispatcher.BeginInvoke(new Action(delegate { _addWindow.OpenDatePickerForTest(); }), DispatcherPriority.ApplicationIdle);
        }

        // 設定視窗：滑桿拖動時即時預覽，儲存後套用待辦顏色
        internal void PreviewOpacity(double value)
        {
            Opacity = Settings.ClampOpacity(value);
        }

        internal void ApplySettings()
        {
            _offDays.Enabled = _settings.MarkOffDays;   // 放假日標示可能開了或關了（OnTodosChanged 會重畫月曆）
            _typhoonReminder.ScheduleNext();            // 颱風假通知可能開了、關了，或提醒的間隔、時段改了
            _typhoon.Apply();                           // 開關、關注的縣市、資料網址可能改了
            _reminder.ScheduleNext();   // 提醒的間隔或時段可能改了
            _eventReminder.Recheck();   // 行程提醒的方式可能改了
            _store.SetOrder(CalendarOrder.Load());   // 日曆順序可能改了
            Opacity = _settings.Opacity;
            ApplyScale();            // 大小、一週的起始日可能改了
            BuildWeekdayRow();
            ClampToScreen();
            OnTodosChanged();
        }

        private void OpenSettings() { OpenSettings(0); }

        internal void DragCalendarForTest(int from, int to)
        {
            if (_settingsWindow != null) _settingsWindow.Dispatcher.BeginInvoke(new Action(delegate { _settingsWindow.DragRowForTest(from, to); }), DispatcherPriority.ApplicationIdle);
        }

        internal void MakerPreviewForTest()
        {
            if (_settingsWindow != null) _settingsWindow.RunHolidayMakerForTest();
        }

        internal void ExpandTyphoonForTest()
        {
            if (_settingsWindow != null) _settingsWindow.Dispatcher.BeginInvoke(new Action(delegate { _settingsWindow.ExpandTyphoonAreasForTest(); }), DispatcherPriority.ApplicationIdle);
        }

        internal void ScrollSettingsForTest()
        {
            if (_settingsWindow != null) _settingsWindow.Dispatcher.BeginInvoke(new Action(delegate { _settingsWindow.ScrollGeneralToEnd(); }), DispatcherPriority.ApplicationIdle);
        }

        internal void OpenSettings(int tab)
        {
            if (_settingsWindow != null)
            {
                WindowHelper.BringToFront(_settingsWindow);
                return;
            }
            _settingsWindow = new SettingsWindow(_settings, _sources, _google, _typhoon, PreviewOpacity, ApplySettings);
            _settingsWindow.Closed += delegate
            {
                _settingsWindow = null;
                TrimMemory();
            };
            _settingsWindow.SelectTab(tab);
            _settingsWindow.Show();
            WindowHelper.BringToFront(_settingsWindow);
        }

        // ---------- 新增待辦 ----------

        private void WireTodoInput()
        {
            // 視窗平常不搶焦點；要打字時才暫時允許啟用。
            // 不能只看 WPF 認為輸入框有沒有焦點：切到別的程式再點回來時，WPF 內部狀態可能還留著「有焦點」，
            // 但系統的前景視窗已經不是我們，所以每次點輸入框都要確認自己是前景視窗
            TodoInput.PreviewMouseLeftButtonDown += delegate
            {
                SetNoActivate(false);
                if (NativeMethods.GetForegroundWindow() != _hwnd)
                {
                    Activate();
                    NativeMethods.SetForegroundWindow(_hwnd);
                }
                if (!TodoInput.IsKeyboardFocused) TodoInput.Focus();
            };
            TodoInput.GotKeyboardFocus += delegate { TodoHint.Visibility = Visibility.Collapsed; };
            TodoInput.LostKeyboardFocus += delegate
            {
                SetNoActivate(true);
                TodoHint.Visibility = TodoInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            };

            // 視窗失去前景（點到別的程式）時，把輸入框的焦點狀態一併清掉，避免留下過期的狀態
            Deactivated += delegate
            {
                SetNoActivate(true);
                if (TodoInput.IsKeyboardFocusWithin) Keyboard.ClearFocus();
            };
            TodoInput.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    AddTodo();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    TodoInput.Clear();
                    Keyboard.ClearFocus();
                }
            };
        }

        private void AddTodo()
        {
            string text = TodoInput.Text.Trim();
            if (text.Length == 0) return;
            _todos.Add(_selected, text);
            TodoInput.Clear();
            OnTodosChanged();
        }

        private void SetNoActivate(bool on)
        {
            if (_hwnd == IntPtr.Zero) return;
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex = on ? (ex | NativeMethods.WS_EX_NOACTIVATE) : (ex & ~NativeMethods.WS_EX_NOACTIVATE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }

        private static Border MakeCell(string text, Brush foreground, Brush fill)
        {
            return new Border
            {
                Width = CellWidth,
                Height = CellHeight,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Background = fill ?? Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    Foreground = foreground,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        // ---------- 事件 ----------

        private void WireEvents()
        {
            SourceInitialized += OnSourceInitialized;
            LocationChanged += delegate { _saveTimer.Stop(); _saveTimer.Start(); };
            _saveTimer.Tick += delegate
            {
                _saveTimer.Stop();
                _settings.Left = Left;
                _settings.Top = Top;
                _settings.Save();
            };

            MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (TodoInput.IsKeyboardFocused) Keyboard.ClearFocus();
                if (_settings.LockPosition) return;   // 已鎖定位置，不能拖動
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            WireTodoInput();

            HookButton(PrevButton, delegate { Step(-1); });
            HookButton(NextButton, delegate { Step(1); });
            HookButton(YearButton, OnYearClicked);
            HookButton(MonthButton, OnMonthClicked);
            HookButton(TodayButton, GoToToday);
            HookButton(DetailHeader, ToggleCollapsed);
            PreviewMouseWheel += OnWheel;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Closed += delegate
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                if (_tray != null) _tray.Dispose();
            };
        }

        private void HookButton(Border button, Action onClick)
        {
            Brush rest = button.Background;   // 滑鼠離開時恢復成原本的底色
            button.MouseEnter += delegate { button.Background = HoverFill; };
            button.MouseLeave += delegate { button.Background = rest; };
            button.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            button.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; onClick(); };
        }

        private void ShowMonth(DateTime month)
        {
            _pickerLevel = PickerLevel.None;
            _viewMonth = month;
            BuildCalendar();
            TrimMemory();
        }

        // 啟動與重畫後把暫時用不到的記憶體頁交還系統，工作管理員看到的用量會小很多；
        // 需要時系統會自動換回，閒置的小工具幾乎感覺不到
        private void TrimMemory()
        {
            if (_trimQueued) return;   // 滾輪連續換月時只排一次，不要每一格都整理一次
            _trimQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
            {
                _trimQueued = false;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                NativeMethods.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
            }));
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    RefreshForNewDay();
                    ScheduleMidnightRefresh();
                }));
            }
        }

        // 跨日時重畫，讓「今天」的標記跟著換；整天只有一個計時器，不輪詢
        private void ScheduleMidnightRefresh()
        {
            _midnightTimer.Stop();
            _midnightTimer.Interval = DateTime.Today.AddDays(1).AddSeconds(1) - DateTime.Now;
            _midnightTimer.Tick -= OnMidnight;
            _midnightTimer.Tick += OnMidnight;
            _midnightTimer.Start();
        }

        private void OnMidnight(object sender, EventArgs e)
        {
            RefreshForNewDay();
            ScheduleMidnightRefresh();
        }

        // 選取的日期如果原本是「今天」，跨日後跟著移到新的今天
        private void RefreshForNewDay()
        {
            DateTime today = DateTime.Today;
            if (_selected == _lastToday) _selected = today;
            _lastToday = today;
            BuildCalendar();
            TrimMemory();
            _sources.RefreshDue();   // 睡眠醒來或跨日後，補抓到期的來源
            _accounts.RefreshDue();
            _reminder.ScheduleNext();   // 睡眠期間計時器可能錯過時間，重新對齊下一次檢查
            _typhoonReminder.ScheduleNext();
            _typhoon.RefreshDue();      // 睡眠醒來或跨日後，補讀一次停班停課資料
            _eventReminder.Recheck();
        }

        private void GoToToday()
        {
            SelectDate(DateTime.Today);
            TrimMemory();
        }

        // ---------- 位置 ----------

        private void RestorePosition()
        {
            double left = _settings.Left;
            double top = _settings.Top;

            bool valid = !double.IsNaN(left) && !double.IsNaN(top)
                && left > SystemParameters.VirtualScreenLeft - 20
                && top > SystemParameters.VirtualScreenTop - 20
                && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60
                && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;

            if (!valid)
            {
                var area = SystemParameters.WorkArea;
                left = area.Right - 320;
                top = area.Top + 40;
            }
            Left = left;
            Top = top;
        }

        // ---------- 桌面小工具行為 ----------

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _hwnd = hwnd;

            // 不出現在 Alt+Tab、點擊時不搶焦點
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // 任何會改變 Z 順序的操作，一律改成放到最底層
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_WINDOWPOSCHANGING)
            {
                var pos = (NativeMethods.WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(NativeMethods.WINDOWPOS));
                if ((pos.flags & NativeMethods.SWP_NOZORDER) == 0)
                {
                    pos.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
            }
            return IntPtr.Zero;
        }

        // ---------- 系統匣 ----------

        private void CreateTray()
        {
            _tray = new TrayIcon(
                "月曆小工具",
                OpenSettings,
                GoToToday,
                delegate { Application.Current.Shutdown(); });
        }
    }
}

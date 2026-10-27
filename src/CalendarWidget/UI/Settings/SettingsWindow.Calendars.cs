using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CalendarWidget
{
    // 設定視窗的「日曆」分頁：所有可以顯示的日曆（不管來自 ICS、Google 還是以後的其他帳號）集中在這裡，
    // 統一設定要不要顯示、類型、顏色，以及先後順序。各個帳號分頁只負責「連接帳號」
    public partial class SettingsWindow
    {
        private enum RowType
        {
            Ics,
            GoogleCalendar,
            GoogleTasks,
            Local,
            LocalEvents
        }

        private sealed class CalRow
        {
            public string Id;
            public RowType Type;
            public string Name;
            public string SourceLabel;
            public bool Enabled;
            public ItemKind Kind;
            public string Color;

            public Border Root;
            public Border Grip;
            public Border ColorDot;
            public TextBlock NameText;
        }

        private readonly List<CalRow> _calRows = new List<CalRow>();

        private CalRow FindRow(string id)
        {
            return _calRows.FirstOrDefault(r => r.Id == id);
        }

        private void RemoveRow(string id)
        {
            var row = FindRow(id);
            if (row == null) return;
            _calRows.Remove(row);
            RenderRows();
        }

        // 依目前的來源與帳號重建清單；畫面上已經改過、還沒儲存的選擇和順序會保留
        private void RebuildCalendarList()
        {
            var previous = _calRows.ToDictionary(r => r.Id);
            List<string> order = _calRows.Count > 0 ? _calRows.Select(r => r.Id).ToList() : CalendarOrder.Load();

            var rows = new List<CalRow>();
            rows.Add(new CalRow
            {
                Id = CalendarIds.Local,
                Type = RowType.Local,
                Name = "本機待辦（你自己加的）",
                SourceLabel = "本機",
                Enabled = true,
                Kind = ItemKind.LocalTodo,
                Color = _settings.TodoColor
            });
            rows.Add(new CalRow
            {
                Id = CalendarIds.LocalEvents,
                Type = RowType.LocalEvents,
                Name = "本機行程（右鍵新增的）",
                SourceLabel = "本機",
                Enabled = true,
                Kind = ItemKind.Event,
                Color = _settings.LocalEventColor
            });
            foreach (var s in _manager.Sources)
            {
                rows.Add(new CalRow { Id = CalendarIds.Ics(s.Id), Type = RowType.Ics, Name = s.Name, SourceLabel = "ICS", Enabled = s.Enabled, Kind = s.Kind, Color = s.Color });
            }
            if (_google.IsSignedIn && !_google.NeedsSignIn)
            {
                GoogleConfig cfg = _google.Config;
                foreach (var c in cfg.Calendars)
                {
                    rows.Add(new CalRow { Id = CalendarIds.Google(c.Id), Type = RowType.GoogleCalendar, Name = c.Name, SourceLabel = "Google", Enabled = c.Enabled, Kind = c.Kind, Color = c.Color });
                }
                rows.Add(new CalRow
                {
                    Id = CalendarIds.GoogleTasks,
                    Type = RowType.GoogleTasks,
                    Name = "Google 待辦（有到期日的）",
                    SourceLabel = "Google",
                    Enabled = cfg.ShowTasks,
                    Kind = ItemKind.GoogleTask,
                    Color = cfg.TasksColor
                });
            }

            foreach (var r in rows)
            {
                CalRow old;
                if (previous.TryGetValue(r.Id, out old))
                {
                    r.Enabled = old.Enabled;
                    r.Kind = old.Kind;
                    r.Color = old.Color;
                }
            }

            _calRows.Clear();
            _calRows.AddRange(SortRows(rows, order));
            foreach (var r in _calRows) BuildRowUi(r);
            RenderRows();
        }

        // 先照儲存過的順序；沒有出現在順序裡的（新的、還沒排過的）接在後面，預設依種類：節日、行程、Google 待辦、本機待辦
        private static List<CalRow> SortRows(List<CalRow> rows, List<string> order)
        {
            Func<CalRow, int> rank = r => r.Type == RowType.Local ? 3 : r.Type == RowType.GoogleTasks ? 2 : (r.Kind == ItemKind.Holiday ? 0 : 1);

            var byId = rows.ToDictionary(r => r.Id);
            var result = new List<CalRow>();
            foreach (string id in order)
            {
                CalRow r;
                if (byId.TryGetValue(id, out r) && !result.Contains(r)) result.Add(r);
            }
            result.AddRange(rows.Where(r => !result.Contains(r)).Select((r, i) => new { r, i }).OrderBy(x => rank(x.r)).ThenBy(x => x.i).Select(x => x.r));
            return result;
        }

        private static Brush BrushOf(string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Colors.IsValidHex(hex) ? hex : "#6CB4FF")); }
            catch (FormatException) { return Brushes.Gray; }
        }

        private void RenderRows()
        {
            CalendarRows.Children.Clear();
            for (int i = 0; i < _calRows.Count; i++)
            {
                CalendarRows.Children.Add(_calRows[i].Root);
            }
            if (_calRows.Count == 0)
                CalendarRows.Children.Add(new TextBlock { Text = "（還沒有任何日曆）", Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0) });
        }

        // ---------- 拖曳排序 ----------
        // 按住每列最左邊的握把上下拖：被拖的列會跟著指標換位置，放開就定案（按「儲存」才會存到順序）。
        // 滑鼠抓在整個清單面板上（不是握把），因為列在換位置時會暫時離開畫面樹，抓在列上的話會中途斷掉

        private CalRow _dragRow;
        private readonly System.Windows.Threading.DispatcherTimer _autoScroll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        private double _autoScrollSpeed;
        private double _lastDragY;

        private void WireCalendarDrag()
        {
            CalendarRows.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_dragRow == null) return;
                _lastDragY = e.GetPosition(CalendarRows).Y;
                DragTo(_lastDragY);
                UpdateAutoScroll(e.GetPosition(CalendarScroll).Y);
            };
            CalendarRows.MouseLeftButtonUp += delegate { EndDrag(); };
            CalendarRows.LostMouseCapture += delegate { EndDrag(); };

            // 拖到清單上下邊緣時自動捲動（手不用動也會繼續捲）
            _autoScroll.Tick += delegate
            {
                if (_dragRow == null || _autoScrollSpeed == 0) return;
                CalendarScroll.ScrollToVerticalOffset(CalendarScroll.VerticalOffset + _autoScrollSpeed);
                CalendarRows.UpdateLayout();
                _lastDragY = Mouse.GetPosition(CalendarRows).Y;
                DragTo(_lastDragY);
            };
        }

        private void StartDrag(CalRow r)
        {
            _dragRow = r;
            r.Root.Background = new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFF));
            r.Root.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
            r.Root.Opacity = 0.85;
            CalendarRows.CaptureMouse();
            _autoScroll.Start();
        }

        // 指標到了垂直位置 y（相對於清單面板）：被拖的列換到對應的位置
        private void DragTo(double y)
        {
            if (_dragRow == null) return;
            int current = _calRows.IndexOf(_dragRow);
            var centers = new List<double>();
            foreach (var row in _calRows)
            {
                var top = row.Root.TranslatePoint(new Point(0, 0), CalendarRows).Y;
                centers.Add(top + row.Root.ActualHeight / 2);
            }
            int target = RowReorder.TargetIndex(centers, current, y);
            if (target == current) return;

            RowReorder.Move(_calRows, current, target);
            CalendarRows.Children.Remove(_dragRow.Root);
            CalendarRows.Children.Insert(target, _dragRow.Root);
            CalendarRows.UpdateLayout();
        }

        private void UpdateAutoScroll(double yInViewport)
        {
            const double edge = 36;
            double h = CalendarScroll.ViewportHeight;
            if (yInViewport < edge) _autoScrollSpeed = -Math.Min(18, (edge - yInViewport) / 2 + 2);
            else if (yInViewport > h - edge) _autoScrollSpeed = Math.Min(18, (yInViewport - (h - edge)) / 2 + 2);
            else _autoScrollSpeed = 0;
        }

        private void EndDrag()
        {
            if (_dragRow == null) return;
            var r = _dragRow;
            _dragRow = null;
            _autoScroll.Stop();
            _autoScrollSpeed = 0;
            r.Root.Background = Brushes.White;
            r.Root.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xD8, 0xE3));
            r.Root.Opacity = 1.0;
            if (CalendarRows.IsMouseCaptured) CalendarRows.ReleaseMouseCapture();
        }

        // 開發檢查用：不用真的滑鼠，直接把第 from 列拖到第 to 列的位置（跟滑鼠拖曳走同一段程式）
        internal void DragRowForTest(int from, int to)
        {
            if (from < 0 || from >= _calRows.Count || to < 0 || to >= _calRows.Count) return;
            CalendarRows.UpdateLayout();
            var row = _calRows[from];
            _dragRow = row;
            var target = _calRows[to];
            double targetTop = target.Root.TranslatePoint(new Point(0, 0), CalendarRows).Y;
            // 往下拖要越過目標列的中心，往上拖只要到目標列的上半部
            DragTo(from < to ? targetTop + target.Root.ActualHeight - 1 : targetTop + 1);
            _dragRow = null;
        }

        private void BuildRowUi(CalRow r)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 拖曳排序的握把
            r.Grip = new Border
            {
                Width = 22,
                Height = 28,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "按住上下拖曳調整順序",
                Child = new TextBlock
                {
                    Text = "⠿",
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    FontSize = 18,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0xA3)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            r.Grip.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; StartDrag(r); };
            var move = r.Grip;

            // 要不要顯示
            r.NameText = new TextBlock
            {
                Text = r.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0),
                ToolTip = r.Name,
                Opacity = r.Enabled ? 1.0 : 0.5
            };
            var enabled = new CheckBox
            {
                IsChecked = r.Enabled,
                IsEnabled = r.Type != RowType.Local && r.Type != RowType.LocalEvents,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip = (r.Type == RowType.Local || r.Type == RowType.LocalEvents) ? "本機的一定會顯示" : "顯示這個日曆"
            };
            enabled.Checked += delegate { r.Enabled = true; r.NameText.Opacity = 1.0; };
            enabled.Unchecked += delegate { r.Enabled = false; r.NameText.Opacity = 0.5; };

            // 來源標籤
            var tag = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xF0)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = r.SourceLabel, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66)) }
            };

            // 類型（行程或節日；待辦固定）
            var kind = new ComboBox { Width = 74, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            if (r.Type == RowType.Ics || r.Type == RowType.GoogleCalendar)
            {
                kind.Items.Add("行程");
                kind.Items.Add("節日");
                kind.SelectedIndex = r.Kind == ItemKind.Holiday ? 1 : 0;
                kind.ToolTip = "節日才會觸發前一天的提醒";
                kind.SelectionChanged += delegate { r.Kind = kind.SelectedIndex == 1 ? ItemKind.Holiday : ItemKind.Event; };
            }
            else
            {
                kind.Items.Add(r.Type == RowType.LocalEvents ? "行程" : "待辦");
                kind.SelectedIndex = 0;
                kind.IsEnabled = false;
            }

            // 顏色
            r.ColorDot = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = BrushOf(r.Color),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0xA6)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "選顏色"
            };
            r.ColorDot.MouseLeftButtonUp += delegate { ShowColorPopup(r); };

            Grid.SetColumn(move, 0);
            Grid.SetColumn(enabled, 1);
            Grid.SetColumn(r.NameText, 2);
            Grid.SetColumn(tag, 3);
            Grid.SetColumn(kind, 4);
            Grid.SetColumn(r.ColorDot, 5);
            grid.Children.Add(move);
            grid.Children.Add(enabled);
            grid.Children.Add(r.NameText);
            grid.Children.Add(tag);
            grid.Children.Add(kind);
            grid.Children.Add(r.ColorDot);

            r.Root = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xD8, 0xE3)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 7, 10, 7),
                Margin = new Thickness(0, 3, 0, 3),
                Child = grid
            };
        }

        // 點顏色圓點：跳出色票選單
        private void ShowColorPopup(CalRow r)
        {
            var popup = new Popup
            {
                PlacementTarget = r.ColorDot,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade
            };

            var colors = new List<string>(Colors.Palette);
            // 目前的顏色不在色票裡（例如 Google 日曆預設的顏色）：多放一個，才看得出現在選的是哪個
            if (Colors.IsValidHex(r.Color) && !colors.Any(c => string.Equals(c, r.Color, StringComparison.OrdinalIgnoreCase)))
                colors.Insert(0, r.Color);

            var panel = new WrapPanel { Width = 5 * 30 };
            foreach (string hex in colors)
            {
                string color = hex;
                var swatch = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Margin = new Thickness(4),
                    Background = BrushOf(color),
                    BorderThickness = new Thickness(2),
                    BorderBrush = string.Equals(color, r.Color, StringComparison.OrdinalIgnoreCase) ? Brushes.Black : Brushes.Transparent,
                    Cursor = Cursors.Hand
                };
                swatch.MouseLeftButtonUp += delegate
                {
                    r.Color = color;
                    r.ColorDot.Background = BrushOf(color);
                    popup.IsOpen = false;
                };
                panel.Children.Add(swatch);
            }

            popup.Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC5, 0xC8, 0xD3)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6),
                Margin = new Thickness(4),
                Child = panel
            };
            popup.IsOpen = true;
        }
    }
}

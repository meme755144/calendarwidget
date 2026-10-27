using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalendarWidget
{
    // 提醒框裡的一行：前面一個顏色圓點，後面一段文字
    internal sealed class ReminderLine
    {
        public string Text;
        public string Color;
    }

    // 節日提醒、行程提醒共用的提醒框。位置固定在螢幕右下角，同時有好幾個提醒框時由下往上疊起來
    public partial class ReminderWindow : Window
    {
        private static readonly List<ReminderWindow> Stack = new List<ReminderWindow>();   // 目前顯示中的，由下往上

        private Size _size;

        internal event Action Acknowledged;
        internal event Action Snoozed;

        public ReminderWindow()
        {
            InitializeComponent();
            AckButton.MouseLeftButtonUp += delegate { var h = Acknowledged; if (h != null) h(); };
            SnoozeButton.MouseLeftButtonUp += delegate { var h = Snoozed; if (h != null) h(); };

            // 收起來或關掉之後，不再佔位子，其他的提醒框往下補上來
            IsVisibleChanged += delegate
            {
                if (!IsVisible)
                {
                    Stack.Remove(this);
                    Relayout();
                }
            };
            Closed += delegate
            {
                Stack.Remove(this);
                Relayout();
            };
        }

        // 第一次是顯示；框已經存在（還沒處理或被「稍後再問」收起）時，只更新內容並重新顯示，不會多開一個
        internal void ShowOrUpdate(string heading, string subHeading, IList<ReminderLine> lines, string ackText, string snoozeText)
        {
            HeadText.Text = heading;
            DateText.Text = subHeading;
            AckText.Text = ackText;
            SnoozeText.Text = snoozeText;

            ItemList.Children.Clear();
            foreach (var line in lines)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Margin = new Thickness(0, 6, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Fill = (Brush)new BrushConverter().ConvertFromString(Colors.IsValidHex(line.Color) ? line.Color : "#FF6B7A")
                };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(new TextBlock
                {
                    Text = line.Text,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF4)),
                    TextWrapping = TextWrapping.Wrap
                });
                ItemList.Children.Add(row);
            }

            // 視窗還沒顯示時量不到實際大小，所以先量內容需要的大小，再靠右下角擺放
            var content = (FrameworkElement)Content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _size = content.DesiredSize;

            if (!Stack.Contains(this)) Stack.Add(this);
            Relayout();

            if (!IsVisible) Show();   // ShowActivated=False：不會搶走你正在輸入的視窗焦點
        }

        private static void Relayout()
        {
            var area = SystemParameters.WorkArea;
            double bottom = area.Bottom - 24;
            foreach (var w in Stack)
            {
                w.Left = area.Right - w._size.Width - 24;
                w.Top = bottom - w._size.Height;
                bottom = w.Top - 10;
            }
        }
    }
}

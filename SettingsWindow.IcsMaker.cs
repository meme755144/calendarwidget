using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalendarWidget
{
    // 「ICS 產生」分頁：依勾選的條件，用行政院人事行政總處公布的辦公日曆表產生節日 ICS，
    // 可以存成 .ics 檔（匯入 Google 日曆、手機…），也可以直接加進月曆當成 ICS 來源
    public partial class SettingsWindow
    {
        private ComboBox _hmYear, _hmYearTo;
        private CheckBox _hmHolidays, _hmWeekends, _hmMakeUp, _hmMerge, _hmAlarm;
        private Button _hmDownload, _hmSave, _hmAdd;
        private TextBlock _hmSummary;
        private TextBox _hmPreview;
        private List<HolidayEntry> _hmEntries;   // 目前下載好的資料（可能是好幾年）
        private string _hmLabel = "";            // 這份資料的年度說明，例如 "2026" 或 "2026–2027"（用在檔名與日曆名稱）
        private bool _hmBusy;

        private void BuildIcsMakerTab()
        {
            var host = IcsMakerHost;

            host.Children.Add(new TextBlock
            {
                Text = "依條件產生台灣的節日、補班日 ICS 檔。資料來自行政院人事行政總處的「政府行政機關辦公日曆表」（經 ruyut/TaiwanCalendar 整理成 JSON，內容與官方一致；授權：政府資料開放授權條款第 1 版）。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                TextWrapping = TextWrapping.Wrap
            });

            host.Children.Add(new TextBlock { Text = "年度", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
            var yearRow = new StackPanel { Orientation = Orientation.Horizontal };
            var years = HolidayCalendar.AvailableYears(DateTime.Today);   // 新的在前面
            _hmYear = new ComboBox { Width = 110 };
            _hmYearTo = new ComboBox { Width = 110, Margin = new Thickness(10, 0, 0, 0) };
            foreach (int y in years) { _hmYear.Items.Add(y + " 年"); _hmYearTo.Items.Add(y + " 年"); }
            _hmYear.Tag = years;
            _hmYearTo.Tag = years;
            // 預設：從今年到最新可選的年度（今年 6 月以後就是今年和明年），月曆上現在看得到的月份也有資料
            _hmYear.SelectedIndex = Math.Max(0, years.IndexOf(DateTime.Today.Year));
            _hmYearTo.SelectedIndex = 0;
            _hmYear.SelectionChanged += delegate
            {
                if (SelectedHolidayYear(_hmYear) > SelectedHolidayYear(_hmYearTo)) _hmYearTo.SelectedIndex = _hmYear.SelectedIndex;
                ClearHolidayData();
            };
            _hmYearTo.SelectionChanged += delegate
            {
                if (SelectedHolidayYear(_hmYearTo) < SelectedHolidayYear(_hmYear)) _hmYear.SelectedIndex = _hmYearTo.SelectedIndex;
                ClearHolidayData();
            };
            yearRow.Children.Add(_hmYear);
            yearRow.Children.Add(new TextBlock { Text = "到", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            yearRow.Children.Add(_hmYearTo);
            host.Children.Add(yearRow);
            host.Children.Add(new TextBlock
            {
                Text = "官方每年約 " + HolidayCalendar.NextYearAvailableMonth + " 月才公布次年度的行事曆，所以次年度要到 " + HolidayCalendar.NextYearAvailableMonth + " 月以後才能選；選了以後如果還沒有資料，會提示你。可以一次產生好幾年。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });

            host.Children.Add(new TextBlock { Text = "要包含哪些日子", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
            _hmHolidays = AddMakerCheck(host, "國定假日與補假（春節、清明節、中秋節、補假…）", true);
            _hmWeekends = AddMakerCheck(host, "一般週末（週六、週日）", false);
            _hmMakeUp = AddMakerCheck(host, "補班日（本來是週末、但要上班的日子）", true);
            _hmMerge = AddMakerCheck(host, "相同名稱的連續假日合併成一筆（例如春節 3 天變成一筆多日行程）", true);
            _hmAlarm = AddMakerCheck(host, "加上提醒：前一天 09:00（週末不加）", false);

            AddDownloadButton(host);

            _hmSummary = new TextBlock { Margin = new Thickness(0, 14, 0, 6), TextWrapping = TextWrapping.Wrap };
            host.Children.Add(_hmSummary);

            _hmPreview = new TextBox
            {
                IsReadOnly = true,
                Height = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas, Microsoft JhengHei UI"),
                Background = Brushes.White,
                Padding = new Thickness(6)
            };
            host.Children.Add(_hmPreview);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            _hmSave = new Button { Content = "儲存成 .ics 檔…", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            _hmSave.Click += delegate { SaveHolidayFile(); };
            _hmAdd = new Button { Content = "加到月曆（當成 ICS 來源）", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false };
            _hmAdd.Click += delegate { AddHolidaySource(); };
            buttons.Children.Add(_hmSave);
            buttons.Children.Add(_hmAdd);
            host.Children.Add(buttons);
            host.Children.Add(new TextBlock
            {
                Text = "「加到月曆」會把檔案存在這台電腦，並加進「ICS 來源」；沒勾選「一般週末」時類型是「節日」（會觸發節日前一天的提醒）。之後想換年度或條件，再產生一次即可。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });

            UpdateHolidayPreview();
        }

        // 開發檢查畫面用：直接按下「下載資料並預覽」
        internal void RunHolidayMakerForTest()
        {
            DownloadHolidayData();
        }

        private CheckBox AddMakerCheck(StackPanel host, string text, bool isChecked)
        {
            var box = new CheckBox { Content = text, IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 3) };
            box.Checked += delegate { UpdateHolidayPreview(); };
            box.Unchecked += delegate { UpdateHolidayPreview(); };
            host.Children.Add(box);
            return box;
        }

        private void AddDownloadButton(StackPanel host)
        {
            _hmDownload = new Button { Content = "下載資料並預覽", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
            _hmDownload.Click += delegate { DownloadHolidayData(); };
            host.Children.Add(_hmDownload);
        }

        private static int SelectedHolidayYear(ComboBox box)
        {
            var years = box.Tag as List<int>;
            return years != null && box.SelectedIndex >= 0 ? years[box.SelectedIndex] : DateTime.Today.Year;
        }

        private static string YearLabel(int from, int to)
        {
            return from == to ? from.ToString(CultureInfo.InvariantCulture) : from + "–" + to;
        }

        private HolidayOptions CurrentHolidayOptions()
        {
            return new HolidayOptions
            {
                Holidays = _hmHolidays.IsChecked == true,
                Weekends = _hmWeekends.IsChecked == true,
                MakeUpDays = _hmMakeUp.IsChecked == true,
                Merge = _hmMerge.IsChecked == true,
                Alarm = _hmAlarm.IsChecked == true
            };
        }

        private void ClearHolidayData()
        {
            _hmEntries = null;
            UpdateHolidayPreview();
        }

        private void DownloadHolidayData()
        {
            if (_hmBusy) return;
            int from = SelectedHolidayYear(_hmYear), to = SelectedHolidayYear(_hmYearTo);
            string label = YearLabel(from, to);
            _hmBusy = true;
            _hmDownload.IsEnabled = false;
            _hmSummary.Foreground = Brushes.DimGray;
            _hmSummary.Text = "正在下載 " + label + " 年的資料…";

            Task.Run(() =>
            {
                var all = new List<HolidayEntry>();
                string error = null;
                for (int year = from; year <= to && error == null; year++)
                {
                    try { all.AddRange(HolidayCalendar.Download(year)); }
                    catch (Exception ex) { error = year + " 年：" + (ex is InvalidDataException ? ex.Message : GoogleHttp.Describe(ex)); }
                }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _hmBusy = false;
                    _hmDownload.IsEnabled = true;
                    if (YearLabel(SelectedHolidayYear(_hmYear), SelectedHolidayYear(_hmYearTo)) != label) { UpdateHolidayPreview(); return; }   // 下載的期間又換了年度，結果不用
                    if (error != null)
                    {
                        _hmEntries = null;
                        UpdateHolidayPreview();
                        _hmSummary.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                        _hmSummary.Text = "無法取得資料，" + error;
                        return;
                    }
                    _hmEntries = all;
                    _hmLabel = label;
                    UpdateHolidayPreview();
                }));
            });
        }

        // 依目前勾選的條件重新算一次事件（不用再下載）
        private List<HolidayEvent> CurrentHolidayEvents()
        {
            return _hmEntries == null ? new List<HolidayEvent>() : HolidayCalendar.BuildEvents(_hmEntries, CurrentHolidayOptions());
        }

        private void UpdateHolidayPreview()
        {
            if (_hmEntries == null)
            {
                _hmPreview.Text = "";
                _hmSave.IsEnabled = _hmAdd.IsEnabled = false;
                if (!_hmBusy) { _hmSummary.Foreground = Brushes.DimGray; _hmSummary.Text = "選好年度，按「下載資料並預覽」。"; }
                return;
            }

            var events = CurrentHolidayEvents();
            _hmSummary.Foreground = Brushes.Black;
            _hmSummary.Text = _hmLabel + " 年：共 " + events.Count + " 筆事件（國定假日與補假 " + events.Count(e => e.Kind == HolidayKind.Holiday) +
                              "、補班日 " + events.Count(e => e.Kind == HolidayKind.MakeUp) + "、週末 " + events.Count(e => e.Kind == HolidayKind.Weekend) + "）" +
                              (events.Count(e => e.Kind == HolidayKind.MakeUp) == 0 && _hmMakeUp.IsChecked == true ? "。這段期間官方沒有安排補班日。" : "");

            int from = _hmEntries.Min(e => e.Date.Year), to = _hmEntries.Max(e => e.Date.Year);
            var sb = new StringBuilder();
            foreach (var ev in events)
            {
                DateTime last = ev.EndExclusive.AddDays(-1);
                string range = ev.Start.ToString(from == to ? "MM/dd" : "yy/MM/dd", CultureInfo.InvariantCulture) + (last > ev.Start ? "～" + last.ToString("MM/dd", CultureInfo.InvariantCulture) : "");
                sb.Append(range.PadRight(from == to ? 13 : 17)).Append(ev.Title).Append("\r\n");
            }
            _hmPreview.Text = sb.ToString();
            _hmSave.IsEnabled = _hmAdd.IsEnabled = events.Count > 0;
        }

        private string BuildHolidayIcs(List<HolidayEvent> events)
        {
            return HolidayCalendar.BuildIcs(events, "台灣行事曆 " + _hmLabel, CurrentHolidayOptions().Alarm, DateTime.UtcNow);
        }

        private void SaveHolidayFile()
        {
            var events = CurrentHolidayEvents();
            if (events.Count == 0) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "儲存節日 ICS 檔",
                Filter = "ICS 日曆檔 (*.ics)|*.ics",
                FileName = "台灣行事曆_" + _hmLabel + ".ics",
                DefaultExt = ".ics"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                File.WriteAllText(dialog.FileName, BuildHolidayIcs(events), new UTF8Encoding(false));
                MessageBox.Show(this, "已儲存：\n" + dialog.FileName + "\n\n可以在 Google 日曆「設定 → 匯入與匯出」匯入這個檔案。", "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(this, "無法儲存檔案：" + ex.Message, "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 把檔案存在資料夾裡，並加進「ICS 來源」（已經有同一個檔案的來源就只更新內容）
        private void AddHolidaySource()
        {
            var events = CurrentHolidayEvents();
            if (events.Count == 0) return;

            string url;
            try
            {
                string dir = Path.Combine(DataPaths.Dir, "generated");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "tw-holidays-" + _hmLabel + ".ics");
                File.WriteAllText(path, BuildHolidayIcs(events), new UTF8Encoding(false));
                url = new Uri(path).AbsoluteUri;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(this, "無法儲存檔案：" + ex.Message, "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var list = _manager.Sources;
            var existing = list.FirstOrDefault(s => s.Url == url);
            if (existing != null)
            {
                _manager.RefreshNow(existing.Id);   // 檔案剛剛已經用新的條件重寫過，重新讀進來
                MessageBox.Show(this, "「" + existing.Name + "」已經在 ICS 來源裡，內容已用新的條件更新。", "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var src = new CalendarSource
            {
                Name = "台灣行事曆 " + _hmLabel,
                Url = url,
                Kind = _hmWeekends.IsChecked == true ? ItemKind.Event : ItemKind.Holiday,   // 含週末的話不能當節日，不然每週五都會提醒
                Color = "#FF6B7A",
                IntervalMinutes = 0   // 本機檔案，只在重新產生時更新
            };
            list.Add(src);
            _manager.Apply(list);
            AddCard(src, false);
            RebuildCalendarList();
            MessageBox.Show(this, "已加進「ICS 來源」。顯示與否、顏色、順序可以到「日曆」分頁調整。", "月曆小工具", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

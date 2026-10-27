using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalendarWidget
{
    // 在日期上按右鍵跳出的「新增行程」視窗：可以放進 Google 日曆（有編輯權限的），或只存在這台電腦的「本機行程」。
    // 也用來「編輯」既有的行程（傳入 editing）：日曆固定不能換，只改標題、日期、時間
    public partial class AddEventWindow : Window
    {
        private sealed class Target
        {
            public string Id;                // 存到設定裡的代號："local" 或日曆代號（例如 "g:日曆代號"）
            public ICalendarAccount Account; // 本機行程為 null
            public string RemoteCalendarId;  // 該服務自己的日曆代號；本機行程為 null
            public string Label;
            public override string ToString() { return Label; }
        }

        private const string LocalTargetId = "local";

        private readonly AccountRegistry _accounts;
        private readonly LocalEventStore _local;
        private readonly Settings _settings;
        private readonly Action _localChanged;
        private readonly DateTime _date;
        private readonly CalendarItem _editing;   // 編輯模式：要修改的那個行程；新增時為 null
        private readonly List<Target> _targets = new List<Target>();
        private bool _busy;

        internal AddEventWindow(DateTime date, AccountRegistry accounts, LocalEventStore local, Settings settings, Action localChanged, CalendarItem editing = null)
        {
            InitializeComponent();

            _editing = editing;
            _date = date.Date;
            _accounts = accounts;
            _local = local;
            _settings = settings;
            _localChanged = localChanged;

            DateBox.SelectedDate = _date;
            DateBox.DisplayDate = _date;
            DateTime endDate = _date;
            if (_editing != null) endDate = _editing.AllDay ? _editing.LastDay : (_editing.End ?? _editing.Start ?? _date).Date;
            EndDateBox.SelectedDate = endDate;
            EndDateBox.DisplayDate = endDate;
            // 開始日期改到比結束日期晚時，結束日期跟著走（不會變成「結束比開始早」）
            DateBox.SelectedDateChanged += delegate
            {
                if (DateBox.SelectedDate.HasValue && (!EndDateBox.SelectedDate.HasValue || EndDateBox.SelectedDate.Value < DateBox.SelectedDate.Value))
                    EndDateBox.SelectedDate = DateBox.SelectedDate;
            };

            for (int minutes = 0; minutes < 24 * 60; minutes += 15)
            {
                string t = AddEventLogic.Format(TimeSpan.FromMinutes(minutes));
                StartBox.Items.Add(t);
                EndBox.Items.Add(t);
            }
            TimeSpan start = AddEventLogic.DefaultStart(_date, DateTime.Now);
            TimeSpan end = start + TimeSpan.FromHours(1);
            if (end >= TimeSpan.FromDays(1)) end = new TimeSpan(23, 59, 0);
            if (_editing != null)
            {
                // 帶入原本的內容
                Title = "編輯行程";
                AddButton.Content = "儲存";
                TitleBox.Text = _editing.Title;
                AllDayBox.IsChecked = _editing.AllDay;
                if (!_editing.AllDay && _editing.Start.HasValue && _editing.End.HasValue)
                {
                    start = _editing.Start.Value.TimeOfDay;
                    end = _editing.End.Value.TimeOfDay;
                }
            }
            StartBox.Text = AddEventLogic.Format(start);
            EndBox.Text = AddEventLogic.Format(end);

            BuildTargets();

            AllDayBox.Checked += delegate { UpdateUi(); };
            AllDayBox.Unchecked += delegate { UpdateUi(); };
            CalendarBox.SelectionChanged += delegate { UpdateUi(); };
            AddButton.Click += delegate { OnAdd(); };
            CancelButton.Click += delegate
            {
                CancelPending();   // 正在等授權的話，一起取消
                Close();
            };
            CopyUrlButton.Click += delegate { CopyAuthUrl(); };
            Closed += delegate { CancelPending(); };
            Loaded += delegate { WindowHelper.BringToFront(this); TitleBox.Focus(); };

            UpdateUi();
        }

        // 開發檢查畫面用：直接展開「開始日期」的選擇月曆
        internal void OpenDatePickerForTest()
        {
            DateBox.IsDropDownOpen = true;
        }

        // 可以選的日曆：有顯示、而且你有編輯權限的 Google 日曆（照你在「日曆」分頁排的順序），最後是「本機行程」
        private void BuildTargets()
        {
            if (_editing != null)
            {
                // 編輯：日曆固定，不能換
                var found = _editing.CalendarId == CalendarIds.LocalEvents ? null : _accounts.Resolve(_editing);
                _targets.Add(new Target
                {
                    Id = _editing.CalendarId,
                    Account = found == null ? null : found.Account,
                    RemoteCalendarId = found == null ? null : found.Calendar.RemoteId,
                    Label = found == null ? "本機行程（只存在這台電腦）" : found.Account.Name + "｜" + found.Calendar.Name
                });
                CalendarBox.Items.Add(_targets[0]);
                CalendarBox.SelectedIndex = 0;
                CalendarBox.IsEnabled = false;
                CalendarLabel.Text = "所在的日曆（不能更換）";
                return;
            }

            // 可以選的日曆：所有已登入帳號裡有顯示、而且你有編輯權限的日曆（照你在「日曆」分頁排的順序），最後是「本機行程」
            foreach (var t in _accounts.WritableTargets(CalendarOrder.Load()))
                _targets.Add(new Target { Id = t.Calendar.StoreId, Account = t.Account, RemoteCalendarId = t.Calendar.RemoteId, Label = t.Label });
            _targets.Add(new Target { Id = LocalTargetId, Label = "本機行程（只存在這台電腦）" });

            foreach (var t in _targets) CalendarBox.Items.Add(t);

            int selected = _targets.FindIndex(t => t.Id == _settings.LastAddCalendar);
            CalendarBox.SelectedIndex = selected >= 0 ? selected : 0;
        }

        // 正在等授權（瀏覽器那邊）的話，取消它
        private void CancelPending()
        {
            if (!_busy) return;
            var target = CalendarBox.SelectedItem as Target;
            if (target != null && target.Account != null) target.Account.CancelPendingAuthorization();
        }

        private Target SelectedTarget()
        {
            return CalendarBox.SelectedItem as Target ?? _targets[0];
        }

        private void UpdateUi()
        {
            bool allDay = AllDayBox.IsChecked == true;
            StartBox.Visibility = EndBox.Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;

            var target = SelectedTarget();
            if (target.Account == null)
            {
                NoticeText.Text = _editing != null
                    ? "只存在這台電腦，不會同步到任何地方。"
                    : "只存在這台電腦，不會同步到任何地方。有時間的行程預設在開始前 " + LocalEventStore.DefaultReminderMinutes + " 分鐘提醒。";
            }
            else if (!target.Account.HasWriteAccess)
            {
                NoticeText.Text = target.Account.WriteAuthNotice(_editing != null);
            }
            else if (_editing != null)
            {
                NoticeText.Text = "只會修改標題和時間，提醒、說明、參加者維持原樣。" + (_editing.Recurring ? "這是重複行程，只會修改這一次。" : "");
            }
            else
            {
                NoticeText.Text = target.Account.DefaultReminderNote;
            }
        }

        private void SetStatus(string text, bool error)
        {
            StatusText.Text = text;
            StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)) : Brushes.DimGray;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            TitleBox.IsEnabled = !busy;
            DateBox.IsEnabled = !busy;
            EndDateBox.IsEnabled = !busy;
            AllDayBox.IsEnabled = !busy;
            StartBox.IsEnabled = !busy;
            EndBox.IsEnabled = !busy;
            CalendarBox.IsEnabled = !busy && _editing == null;
            AddButton.IsEnabled = !busy;
            if (!busy) CopyUrlButton.Visibility = Visibility.Collapsed;
        }

        private void OnAdd()
        {
            bool allDay = AllDayBox.IsChecked == true;
            DateTime date = (DateBox.SelectedDate ?? _date).Date;
            DateTime endDate = (EndDateBox.SelectedDate ?? date).Date;
            TimeSpan start, end;
            string error = AddEventLogic.ValidateRange(TitleBox.Text, allDay, date, endDate, StartBox.Text, EndBox.Text, out start, out end);
            if (error != null)
            {
                SetStatus(error, true);
                return;
            }

            // 送出去的「結束」：有時間的行程是結束日期那天的結束時間；整天行程只看日期（最後一天，含）
            DateTime startAt = allDay ? date : date + start;
            DateTime endAt = allDay ? endDate : endDate + end;
            string title = TitleBox.Text.Trim();
            var target = SelectedTarget();

            if (_editing == null)
            {
                _settings.LastAddCalendar = target.Id;
                _settings.Save();
            }

            if (target.Account == null)
            {
                if (_editing != null) _local.Update(_editing.Id, date, allDay, start, end, title, endDate);
                else _local.Add(date, allDay, start, end, title, endDate);
                _localChanged();
                Close();
                return;
            }

            Action<string> progress = delegate(string message)
            {
                SetStatus(message, false);
                CopyUrlButton.Visibility = Visibility.Visible;   // 瀏覽器沒有自動開啟時，可以複製網址去貼
            };
            Action<string> finished = delegate(string failure)
            {
                SetBusy(false);
                if (failure == null) Close();
                else SetStatus(failure, true);
            };

            var draft = new EventDraft { Title = title, AllDay = allDay, Start = startAt, End = endAt };
            SetBusy(true);
            if (_editing != null)
            {
                SetStatus("正在儲存到 " + target.Account.Name + "…", false);
                target.Account.UpdateEvent(target.RemoteCalendarId, _editing.Id, draft, progress, finished);
                return;
            }
            SetStatus("正在新增到 " + target.Account.Name + "…", false);
            target.Account.AddEvent(target.RemoteCalendarId, draft, progress, finished);
        }

        private void CopyAuthUrl()
        {
            var current = CalendarBox.SelectedItem as Target;
            string url = current != null && current.Account != null ? current.Account.AuthUrl : null;
            if (url == null)
            {
                SetStatus("授權網址還沒準備好，請稍等一下再按。", false);
                return;
            }
            try { Clipboard.SetText(url); }
            catch (System.Runtime.InteropServices.ExternalException) { return; }   // 剪貼簿正被別的程式佔用
            SetStatus("授權網址已複製。請貼到瀏覽器的網址列開啟，同意後會自動回到這裡。", false);
        }
    }
}

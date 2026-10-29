using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace CalendarWidget
{
    // 颱風假通知：關注的縣市（或鄉鎮區）有停班停課，就跳出提醒框，直到按「好」為止。
    // 機制跟節日提醒一樣：提醒框一次只有一個、按「好」確認、按「N 分鐘後再問我」收起來，
    // 間隔與時段沿用「一般 → 提醒」的設定（時段外不打擾，時段開始時才跳出來）。
    // 不一樣的地方：新公告一出現就立刻跳出（在時段內），不用等下一次排定的提醒時間
    internal sealed class TyphoonReminder
    {
        private readonly Func<DateTime, List<TyphoonAlert>> _alerts;
        private readonly Settings _settings;
        private readonly AckStore _acks;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private ReminderWindow _window;
        private List<TyphoonAlert> _shown = new List<TyphoonAlert>();   // 目前提醒框裡列出的公告
        private bool _snoozed;   // 使用者按了「N 分鐘後再問我」，框收起來了

        // alerts：給今天的日期，回傳目前要通知的公告（已依關注的縣市篩選過）
        public TyphoonReminder(Func<DateTime, List<TyphoonAlert>> alerts, Settings settings, AckStore acks)
        {
            _alerts = alerts;
            _settings = settings;
            _acks = acks;
            _timer.Tick += delegate
            {
                Refresh(true);
                ScheduleNext();
            };
        }

        public void Start()
        {
            ScheduleNext();
        }

        // 設定改了、睡眠醒來時，重新對齊下一次提醒的時間。功能關閉時不留任何計時器
        public void ScheduleNext()
        {
            _timer.Stop();
            if (!_settings.TyphoonEnabled)
            {
                CloseWindow();
                return;
            }
            DateTime now = DateTime.Now;
            DateTime next = HolidayReminder.NextCheck(now, _settings.ReminderIntervalMinutes, _settings.ReminderStartHour, _settings.ReminderEndHour);
            _timer.Interval = next - now + TimeSpan.FromSeconds(1);
            _timer.Start();
        }

        // 讀到新資料時呼叫：有還沒確認的公告就立刻跳出，但只在提醒時段內；時段外等到下一次排定的時間
        public void OnDataChanged()
        {
            Refresh(false);
        }

        // 立刻檢查一次（不管是不是在提醒時段內）
        public void CheckNow()
        {
            Refresh(true);
        }

        // 提醒時段：開始（含）到結束（不含）；設定不合理時當作一整天
        internal static bool InWindow(DateTime now, int startHour, int endHour)
        {
            if (startHour < 0 || startHour > 23 || endHour < 1 || endHour > 24 || startHour >= endHour) return true;
            return now.Hour >= startHour && now.Hour < endHour;
        }

        // 還沒按過「好」的公告
        internal static List<TyphoonAlert> Pending(IEnumerable<TyphoonAlert> alerts, AckStore acks)
        {
            return alerts.Where(a => !acks.IsAcked(a.Date, a.AckTitle)).ToList();
        }

        private void Refresh(bool ignoreWindow)
        {
            if (!_settings.TyphoonEnabled)
            {
                CloseWindow();
                return;
            }

            DateTime now = DateTime.Now;
            var pending = Pending(_alerts(now.Date), _acks);
            if (pending.Count == 0)
            {
                CloseWindow();
                return;
            }
            if (!ignoreWindow && !InWindow(now, _settings.ReminderStartHour, _settings.ReminderEndHour)) return;
            // 使用者剛按了「稍後再問」，而且列出來的公告跟剛才一樣：等排定的時間再提醒，不要因為重新讀取資料就又跳出來
            if (!ignoreWindow && _snoozed && _window != null && pending.Select(a => a.AckTitle + a.Date.Ticks).OrderBy(k => k, StringComparer.Ordinal)
                .SequenceEqual(_shown.Select(a => a.AckTitle + a.Date.Ticks).OrderBy(k => k, StringComparer.Ordinal))) return;

            _shown = pending;
            _snoozed = false;
            if (_window == null)
            {
                _window = new ReminderWindow();
                _window.Acknowledged += delegate
                {
                    _acks.Ack(_shown.Select(a => new KeyValuePair<DateTime, string>(a.Date, a.AckTitle)));   // 按「好」：框裡的公告都記成已確認
                    CloseWindow();
                };
                _window.Snoozed += delegate
                {
                    _snoozed = true;
                    _window.Hide();
                };
            }

            DateTime next = HolidayReminder.NextCheck(now, _settings.ReminderIntervalMinutes, _settings.ReminderStartHour, _settings.ReminderEndHour);
            DateTime date = pending[0].Date;
            string[] weekdays = { "日", "一", "二", "三", "四", "五", "六" };
            _window.ShowOrUpdate(
                pending.Count == 1 ? "停班停課通知" : "有 " + pending.Count + " 則停班停課通知",
                date.ToString("M 月 d 日") + "（週" + weekdays[(int)date.DayOfWeek] + "）",
                pending.Select(a => new ReminderLine { Text = a.Line(now.Date), Color = "#FF6B7A" }).ToList(),
                "好",
                HolidayReminder.SnoozeText(now, next));
        }

        private void CloseWindow()
        {
            if (_window == null) return;
            var w = _window;
            _window = null;
            w.Close();
        }
    }
}

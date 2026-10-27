using System;
using System.Collections.Generic;
using System.Linq;

namespace CalendarWidget
{
    // 新增／修改行程時要送出的內容。End 的意思：
    // 有時間的行程＝結束的日期與時間；整天行程＝最後一天（含，只看日期）
    internal sealed class EventDraft
    {
        public string Title;
        public bool AllDay;
        public DateTime Start;
        public DateTime End;
    }

    // 帳號底下一個「可以寫入」的日曆
    internal sealed class AccountCalendar
    {
        public string StoreId;   // 月曆上用的日曆代號（CalendarItem.CalendarId，也是「日曆」分頁排順序用的），例如 "g:me@example.com"
        public string RemoteId;  // 該服務自己的日曆代號，送出請求時用
        public string Name;
    }

    // 所有「線上帳號」（Google、Microsoft、CalDAV…）對月曆小工具的共同介面：
    // 新增、修改、刪除行程的視窗都只認這個介面，不用知道是哪一家。
    // 讀取（抓行程進月曆）各家各自做，結果都放進 ItemStore，這裡只管寫入和授權。
    // 所有方法都在 UI 執行緒呼叫；網路動作各家自己丟到背景，完成時在 UI 執行緒回呼
    internal interface ICalendarAccount
    {
        string Name { get; }                 // 顯示用，例如 "Google"
        bool IsSignedIn { get; }
        bool HasWriteAccess { get; }         // 已經取得「新增／修改／刪除」的授權，不用再請使用者授權
        string AuthUrl { get; }              // 正在等授權時的網址（瀏覽器沒有自動開啟時給使用者複製），沒有就是 null
        string DefaultReminderNote { get; }  // 新增行程時顯示的提醒說明，例如「提醒會使用這個日曆的預設提醒。」
        event Action Changed;                // 這個帳號的行程有更新

        string WriteAuthNotice(bool editing);        // 還沒授權寫入時，要跟使用者說明的話
        List<AccountCalendar> WritableCalendars();   // 可以新增行程的日曆（有顯示、有編輯權限）
        AccountCalendar WritableCalendarOf(CalendarItem item);   // 這個行程屬於本帳號、且能修改／刪除的話，回傳它的日曆；否則 null

        // progress：進度訊息（例如「請在瀏覽器完成授權」）；done：完成時呼叫，參數是錯誤訊息，成功是 null
        void AddEvent(string remoteCalendarId, EventDraft draft, Action<string> progress, Action<string> done);
        void UpdateEvent(string remoteCalendarId, string eventId, EventDraft draft, Action<string> progress, Action<string> done);
        void DeleteEvent(string remoteCalendarId, string eventId, Action<string> progress, Action<string> done);

        void CancelPendingAuthorization();   // 正在等使用者授權的話，取消
        void RefreshDue();                   // 到了更新時間就更新
    }

    internal sealed class AccountItemRef
    {
        public ICalendarAccount Account;
        public AccountCalendar Calendar;
    }

    // 可以選的「放進哪個日曆」：某個帳號底下的日曆
    internal sealed class AccountTarget
    {
        public ICalendarAccount Account;
        public AccountCalendar Calendar;
        public string Label { get { return Account.Name + "｜" + Calendar.Name; } }
    }

    // 目前啟用的所有帳號
    internal sealed class AccountRegistry
    {
        private readonly List<ICalendarAccount> _accounts = new List<ICalendarAccount>();

        public event Action Changed;   // 任何一個帳號的行程有更新

        public IEnumerable<ICalendarAccount> All { get { return _accounts; } }

        public void Add(ICalendarAccount account)
        {
            _accounts.Add(account);
            account.Changed += delegate { var handler = Changed; if (handler != null) handler(); };
        }

        // 這個行程屬於哪個帳號、哪個日曆；不是線上帳號的行程（ICS、本機）、或沒有編輯權限，就回 null
        public AccountItemRef Resolve(CalendarItem item)
        {
            if (item == null || item.Id == null) return null;
            foreach (var account in _accounts)
            {
                var cal = account.WritableCalendarOf(item);
                if (cal != null) return new AccountItemRef { Account = account, Calendar = cal };
            }
            return null;
        }

        // 所有已登入帳號的可寫入日曆，照「日曆」分頁排的順序（order 是日曆代號清單）；沒排到的放後面，維持原本順序
        public List<AccountTarget> WritableTargets(IList<string> order)
        {
            var targets = new List<AccountTarget>();
            foreach (var account in _accounts.Where(a => a.IsSignedIn))
            {
                foreach (var cal in account.WritableCalendars()) targets.Add(new AccountTarget { Account = account, Calendar = cal });
            }
            Func<AccountTarget, int> rank = t =>
            {
                int i = order == null ? -1 : order.IndexOf(t.Calendar.StoreId);
                return i < 0 ? int.MaxValue : i;
            };
            return targets.Select((t, index) => new { t, index }).OrderBy(x => rank(x.t)).ThenBy(x => x.index).Select(x => x.t).ToList();
        }

        public void RefreshDue()
        {
            foreach (var account in _accounts) account.RefreshDue();
        }
    }
}

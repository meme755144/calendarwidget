using System;
using System.Collections.Generic;
using System.Linq;

namespace CalendarWidget
{
    // 項目種類：決定小圓點與詳細欄裡的顏色。之後各種資料來源（ICS、Google Tasks、本機待辦）都轉成同一種 CalendarItem
    internal enum ItemKind
    {
        Holiday,
        Event,
        GoogleTask,
        LocalTodo
    }

    internal sealed class CalendarItem
    {
        public string Id;           // 只有可以操作的項目（本機待辦、本機行程、Google 行程）才有
        public bool Recurring;      // Google 重複行程的其中一次（刪除時只刪這一次）
        public string CalendarId;   // 所屬日曆（ics:xxx、g:xxx、gtasks、local），用來依你調整的順序排列
        public DateTime Date;
        public string Title;
        public string TimeText;   // 有時間的行程才有，例如 "14:00"；整天事件為 null
        public DateTime? Start;   // 行程的開始時間（本機時間）；整天事件是那天的 00:00。待辦和節日沒有
        public DateTime? End;     // 行程的結束時間（本機時間）；整天事件是「最後一天的隔天」00:00（跟 Google 一樣）。編輯行程時用；不知道就是 null
        public bool AllDay;
        public int[] Reminders;   // 行程自己設定的提醒：開始前幾分鐘（只算會跳出通知的那種，不含寄信）；沒有就是 null
        public ItemKind Kind;
        public string Color;      // 圓點與清單前面的顏色，例如 "#6CB4FF"
        public bool Done;

        // 編輯視窗能不能改這個行程：要知道結束時間，而且整天事件只有「第一天」那一筆可以改
        // （跨多天的整天事件，後面幾天那幾筆沒有 Start，改了會讓人以為只改到那一天）
        public bool IsEditable
        {
            get
            {
                if (!End.HasValue) return false;
                if (AllDay) return Start.HasValue && End.Value > Date;
                return Start.HasValue && Start.Value.Date == Date && End.Value >= Start.Value;
            }
        }

        // 整天事件的最後一天（含）；其他情況就是 Date
        public DateTime LastDay
        {
            get { return AllDay && End.HasValue && End.Value > Date ? End.Value.AddDays(-1).Date : Date; }
        }
    }

    // 依「來源名稱」分開存放；某個來源重新抓取時，整批換掉自己那一份，不影響其他來源
    internal sealed class ItemStore
    {
        private static readonly CalendarItem[] None = new CalendarItem[0];
        private readonly Dictionary<string, List<CalendarItem>> _sources = new Dictionary<string, List<CalendarItem>>();
        private Dictionary<DateTime, List<CalendarItem>> _byDate = new Dictionary<DateTime, List<CalendarItem>>();
        private Dictionary<string, int> _order = new Dictionary<string, int>();

        // 日曆的先後順序（設定視窗「日曆」分頁調整的）。排在前面的日曆，圓點和清單都排在前面。
        // 沒有出現在順序裡的日曆（還沒設定過），依種類排在後面：節日、行程、Google 待辦、本機待辦
        public void SetOrder(IEnumerable<string> calendarIds)
        {
            var order = new Dictionary<string, int>();
            int i = 0;
            foreach (string id in calendarIds)
            {
                if (!order.ContainsKey(id)) order[id] = i++;
            }
            _order = order;
        }

        private int Rank(CalendarItem item)
        {
            int index;
            if (item.CalendarId != null && _order.TryGetValue(item.CalendarId, out index)) return index;
            return 1000 + (int)item.Kind;
        }

        public void SetSource(string name, IEnumerable<CalendarItem> items)
        {
            _sources[name] = items.ToList();
            Rebuild();
        }

        private void Rebuild()
        {
            var index = new Dictionary<DateTime, List<CalendarItem>>();
            foreach (var item in _sources.Values.SelectMany(list => list))
            {
                List<CalendarItem> day;
                if (!index.TryGetValue(item.Date.Date, out day))
                {
                    day = new List<CalendarItem>();
                    index[item.Date.Date] = day;
                }
                day.Add(item);
            }
            _byDate = index;
        }

        // 同一天的項目：先依日曆順序，同一個日曆內整天事件在前，再依時間、標題
        public IList<CalendarItem> For(DateTime date)
        {
            List<CalendarItem> list;
            if (!_byDate.TryGetValue(date.Date, out list)) return None;
            return list.OrderBy(Rank).ThenBy(i => i.TimeText ?? "").ThenBy(i => i.Title).ToList();
        }

        public void RemoveSource(string name)
        {
            if (_sources.Remove(name)) Rebuild();
        }

        // 示範資料：只在加上 --demo 參數時載入，用來確認版面
        public static void LoadDemo(ItemStore store)
        {
            var items = new List<CalendarItem>();
            DateTime today = DateTime.Today;
            DateTime first = new DateTime(today.Year, today.Month, 1);

            items.Add(new CalendarItem { Date = first.AddDays(2), Title = "示範節日", Kind = ItemKind.Holiday });
            items.Add(new CalendarItem { Date = first.AddDays(2), Title = "示範：10:00 週會", Kind = ItemKind.Event });
            items.Add(new CalendarItem { Date = first.AddDays(8), Title = "示範：Google 待辦", Kind = ItemKind.GoogleTask });

            items.Add(new CalendarItem { Date = today, Title = "示範：14:00 客戶來訪，地點在大會議室，這是一行比較長的文字用來確認換行", Kind = ItemKind.Event });
            items.Add(new CalendarItem { Date = today, Title = "示範：Google 待辦", Kind = ItemKind.GoogleTask });

            items.Add(new CalendarItem { Date = today.AddDays(1), Title = "示範：明天的節日", Kind = ItemKind.Holiday });
            for (int i = 1; i <= 7; i++)
                items.Add(new CalendarItem { Date = today.AddDays(2), Title = "示範：行程 " + i, Kind = ItemKind.Event });

            foreach (var item in items)
            {
                item.Color = item.Kind == ItemKind.Holiday ? "#FF6B7A" : item.Kind == ItemKind.GoogleTask ? "#FFD166" : "#6CB4FF";
                item.CalendarId = "demo:" + item.Kind;
            }
            // 示範用：8 分鐘後開始、帶有「10 分鐘前」提醒的行程，加上一個明天的整天行程（提醒是前一天 9:00）
            DateTime soon = DateTime.Now.AddMinutes(8);
            items.Add(new CalendarItem
            {
                Date = soon.Date,
                Title = "示範：客戶簡報",
                TimeText = soon.ToString("HH:mm"),
                Kind = ItemKind.Event,
                Start = new DateTime(soon.Year, soon.Month, soon.Day, soon.Hour, soon.Minute, 0),
                Reminders = new[] { 10, 60 }
            });
            items.Add(new CalendarItem
            {
                Date = today.AddDays(1),
                Title = "示範：明天的整天行程",
                Kind = ItemKind.Event,
                AllDay = true,
                Start = today.AddDays(1),
                Reminders = new[] { 900 }   // 前一天 09:00
            });

            foreach (var item in items)
            {
                if (item.Color == null) item.Color = item.Kind == ItemKind.Holiday ? "#FF6B7A" : item.Kind == ItemKind.GoogleTask ? "#FFD166" : "#6CB4FF";
                if (item.CalendarId == null) item.CalendarId = "demo:" + item.Kind;
            }
            store.SetSource("demo", items);
        }
    }
}

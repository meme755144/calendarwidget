using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CalendarWidget
{
    public partial class App : Application
    {
        private Mutex _mutex;
        private bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // --demo 載入示範資料，使用獨立的單一執行個體鎖，也不寫入任何資料，不會干擾平常在跑的那一個
            bool demo = Array.IndexOf(e.Args, "--demo") >= 0;
            Settings.ReadOnly = demo;

            string mutexName = "CalendarWidget.SingleInstance";
            if (demo) mutexName += ".Demo";
            else if (!string.IsNullOrEmpty(DataPaths.Override)) mutexName += ".Test";

            _mutex = new Mutex(true, mutexName, out _ownsMutex);
            if (!_ownsMutex)
            {
                Shutdown();
                return;
            }

            // 透明視窗本來就走軟體繪製；明確指定可避免載入 GPU 驅動相關模組
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            base.OnStartup(e);

            var store = new ItemStore();
            store.SetOrder(CalendarOrder.Load());
            if (demo) ItemStore.LoadDemo(store);
            var todos = LocalTodoStore.Load();

            // 示範模式可用 --select=N 預先選取「今天起第 N 天」，用來檢查各種顯示狀況
            int offset = 0;
            foreach (string arg in e.Args)
            {
                if (arg.StartsWith("--select=")) int.TryParse(arg.Substring("--select=".Length), out offset);
            }
            var sources = new SourceManager(store, Dispatcher);
            var google = new GoogleManager(store, Dispatcher);
            var localEvents = LocalEventStore.Load();
            var accounts = new AccountRegistry();
            accounts.Add(google);   // 之後新增的帳號類型（Microsoft、CalDAV…）也在這裡加進來
            var window = new MainWindow(store, todos, localEvents, sources, google, accounts, offset);
            window.Show();
            if (!demo)   // 示範模式不載入、也不抓取真實的來源
            {
                sources.Start();
                google.Start();
            }
            // 開發檢查畫面用：--show-tooltip=N 直接打開「今天起第 N 天」的懸停提示；--picker 直接進入選年月畫面
            foreach (string arg in e.Args)
            {
                int offset2;
                if (arg.StartsWith("--show-tooltip=") && int.TryParse(arg.Substring("--show-tooltip=".Length), out offset2))
                    window.Dispatcher.BeginInvoke(new Action(delegate { window.ShowDayTipForTest(offset2); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            if (Array.IndexOf(e.Args, "--picker") >= 0) window.ShowPickerForTest(false);
            if (Array.IndexOf(e.Args, "--picker=years") >= 0) window.ShowPickerForTest(true);

            // 開發檢查畫面用：--step=N 相當於滾輪（或箭頭）往後 N 格，負數往前（在選年月畫面則是換年、換頁）
            // --pick=N 在目前的選年份／選月份畫面「點」N（年份數字或 1~12 的月份）
            foreach (string arg in e.Args)
            {
                int steps, picked, addOffset;
                if (arg.StartsWith("--add-event=") && int.TryParse(arg.Substring("--add-event=".Length), out addOffset))
                    window.OpenAddEventForTest(addOffset, Array.IndexOf(e.Args, "--picker-open") >= 0);
                if (arg.StartsWith("--day-menu=") && int.TryParse(arg.Substring("--day-menu=".Length), out addOffset))
                {
                    int menuOffset = addOffset;
                    window.Dispatcher.BeginInvoke(new Action(delegate { window.OpenDayMenuForTest(menuOffset); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                if (arg.StartsWith("--edit-event=") && int.TryParse(arg.Substring("--edit-event=".Length), out picked))
                {
                    int editIndex = picked;
                    System.Threading.Tasks.Task.Delay(3000).ContinueWith(delegate { window.Dispatcher.BeginInvoke(new Action(delegate { window.OpenEditForTest(editIndex); })); });
                }
                if (arg.StartsWith("--ask-delete=") && int.TryParse(arg.Substring("--ask-delete=".Length), out picked))
                {
                    // Google 的資料是連線後才有，等幾秒再套用
                    int askIndex = picked;
                    System.Threading.Tasks.Task.Delay(3000).ContinueWith(delegate { window.Dispatcher.BeginInvoke(new Action(delegate { window.AskDeleteForTest(askIndex); })); });
                }
                if (arg.StartsWith("--step=") && int.TryParse(arg.Substring("--step=".Length), out steps))
                    window.StepForTest(steps);
                if (arg.StartsWith("--pick=") && int.TryParse(arg.Substring("--pick=".Length), out picked))
                    window.PickForTest(picked);
            }

            // 開發檢查畫面用：--remind-now 立刻做一次節日提醒檢查
            if (Array.IndexOf(e.Args, "--remind-now") >= 0) window.Reminder.CheckNow();

            // 開發檢查畫面用：--open-settings 或 --open-settings=N（N 為分頁編號，從 0 開始）
            foreach (string arg in e.Args)
            {
                if (!arg.StartsWith("--open-settings")) continue;
                int tab = 0;
                if (arg.StartsWith("--open-settings=")) int.TryParse(arg.Substring("--open-settings=".Length), out tab);
                window.OpenSettings(tab);
            }
            if (Array.IndexOf(e.Args, "--settings-end") >= 0) window.ScrollSettingsForTest();
            if (Array.IndexOf(e.Args, "--maker-preview") >= 0) window.MakerPreviewForTest();
            foreach (string arg in e.Args)
            {
                // --drag-calendar=從,到：在「日曆」分頁把第 從 列拖到第 到 列（列從 0 算）
                if (!arg.StartsWith("--drag-calendar=")) continue;
                string[] parts = arg.Substring("--drag-calendar=".Length).Split(',');
                int dragFrom, dragTo;
                if (parts.Length == 2 && int.TryParse(parts[0], out dragFrom) && int.TryParse(parts[1], out dragTo)) window.DragCalendarForTest(dragFrom, dragTo);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                if (_ownsMutex) _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}

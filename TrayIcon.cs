using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CalendarWidget
{
    // 直接呼叫 Windows API 的系統匣圖示，避免為了一個 NotifyIcon 載入 WinForms 與 System.Drawing
    internal sealed class TrayIcon : IDisposable
    {
        private const int WM_APP_TRAY = 0x8001;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int WM_NULL = 0x0000;

        private const int NIM_ADD = 0;
        private const int NIM_DELETE = 2;
        private const int NIF_MESSAGE = 1;
        private const int NIF_ICON = 2;
        private const int NIF_TIP = 4;

        private const uint MF_STRING = 0x0000;
        private const uint MF_CHECKED = 0x0008;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_NONOTIFY = 0x0080;

        private const int CmdToday = 1;
        private const int CmdSettings = 2;
        private const int CmdExit = 3;

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private readonly Action _onSettings;
        private readonly Action _onToday;
        private readonly Action _onExit;
        private readonly string _tip;
        private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        private HwndSource _source;
        private IntPtr _icon;
        private bool _added;

        public TrayIcon(string tip, Action onSettings, Action onToday, Action onExit)
        {
            _tip = tip;
            _onSettings = onSettings;
            _onToday = onToday;
            _onExit = onExit;

            _source = new HwndSource(new HwndSourceParameters("CalendarWidgetTray")
            {
                ParentWindow = HWND_MESSAGE,
                WindowStyle = 0
            });
            _source.AddHook(WndProc);

            _icon = ExtractIcon(IntPtr.Zero, Assembly.GetExecutingAssembly().Location, 0);
            Add();
        }

        private void Add()
        {
            var data = NewData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = WM_APP_TRAY;
            data.hIcon = _icon;
            data.szTip = _tip;
            _added = Shell_NotifyIcon(NIM_ADD, ref data);        }

        private NOTIFYICONDATA NewData()
        {
            var data = new NOTIFYICONDATA();
            data.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            data.hWnd = _source.Handle;
            data.uID = 1;
            return data;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_APP_TRAY)
            {
                int mouse = lParam.ToInt32() & 0xFFFF;
                if (mouse == WM_RBUTTONUP || mouse == WM_CONTEXTMENU)
                {
                    ShowMenu();
                    handled = true;
                }
            }
            else if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated)
            {
                // 檔案總管重新啟動後，系統匣會被清空，需要重新加入圖示
                Add();
            }
            return IntPtr.Zero;
        }

        private void ShowMenu()
        {
            IntPtr menu = CreatePopupMenu();
            try
            {
                AppendMenuW(menu, MF_STRING, (UIntPtr)CmdSettings, "設定…");
                AppendMenuW(menu, MF_STRING, (UIntPtr)CmdToday, "回到今天");
                AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenuW(menu, MF_STRING, (UIntPtr)CmdExit, "結束");

                POINT pt;
                GetCursorPos(out pt);
                SetForegroundWindow(_source.Handle);
                int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _source.Handle, IntPtr.Zero);
                PostMessage(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                switch (cmd)
                {
                    case CmdSettings: _onSettings(); break;
                    case CmdToday: _onToday(); break;
                    case CmdExit: _onExit(); break;
                }
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        public void Dispose()
        {
            if (_added)
            {
                var data = NewData();
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }
            if (_icon != IntPtr.Zero)
            {
                DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
            if (_source != null)
            {
                _source.Dispose();
                _source = null;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string file, int index);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenuW(IntPtr hMenu, uint flags, UIntPtr id, string text);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);
    }
}

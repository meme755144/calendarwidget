using System;
using System.Windows;

namespace CalendarWidget
{
    internal static class WindowHelper
    {
        // 月曆本身是不搶焦點、永遠在最底層的視窗，從它開出來的視窗常常會被別的程式蓋在後面。
        // 暫時設成最上層再取消，就能把視窗帶到最前面並取得焦點，之後仍是一般視窗
        public static void BringToFront(Window w)
        {
            if (w == null) return;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Show();
            w.Topmost = true;
            w.Activate();
            w.Topmost = false;
            w.Focus();
        }
    }
}

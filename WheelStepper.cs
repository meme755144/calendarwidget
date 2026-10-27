using System;

namespace CalendarWidget
{
    // 把滾輪訊息換算成「要往前或往後幾格」。
    // 一般滑鼠一格是 120；高解析滑鼠和觸控板會一次送很多更小的量。
    // 如果每次都要累積到 120 才換，這類裝置就會變成要滾兩格才有反應。
    // 所以：停了一下之後的第一個滾動，不管量多小都立刻換一格；
    // 接著連續的滾動才用累積量決定要不要再換，避免觸控板一下子衝過頭。
    internal sealed class WheelStepper
    {
        public const int Notch = 120;
        public const int IdleMs = 220;

        private int _direction;
        private int _accumulated;
        private int _lastTick;
        private bool _hasLast;

        // 回傳：負數 = 往前幾格，正數 = 往後幾格（往上滾 = 往前），0 = 這次不換
        public int Feed(int delta, int tickMs)
        {
            if (delta == 0) return 0;

            int direction = delta > 0 ? -1 : 1;
            bool idle = !_hasLast || unchecked(tickMs - _lastTick) > IdleMs;
            _hasLast = true;
            _lastTick = tickMs;

            if (idle || direction != _direction)
            {
                _direction = direction;
                _accumulated = 0;
                return direction;
            }

            _accumulated += Math.Abs(delta);
            int steps = _accumulated / Notch;
            _accumulated %= Notch;
            return direction * steps;
        }
    }
}

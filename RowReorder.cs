using System.Collections.Generic;

namespace CalendarWidget
{
    // 拖曳排序的規則（不碰畫面，方便單獨測試）
    internal static class RowReorder
    {
        // centers：目前每一列的垂直中心（依現在的順序，被拖的那一列在 current 位置）。
        // 回傳被拖的那一列放開後應該排在第幾個：指標比幾列「其他列」的中心還低，就排在那幾列後面。
        // 只拿其他列的中心當門檻，被拖的那列一搬動就不會來回跳
        public static int TargetIndex(IList<double> centers, int current, double pointerY)
        {
            int target = 0;
            for (int i = 0; i < centers.Count; i++)
            {
                if (i != current && centers[i] < pointerY) target++;
            }
            return target;
        }

        // 把 current 位置的項目搬到 target 位置
        public static void Move<T>(IList<T> list, int current, int target)
        {
            if (current == target || current < 0 || target < 0 || current >= list.Count || target >= list.Count) return;
            T item = list[current];
            list.RemoveAt(current);
            list.Insert(target, item);
        }
    }
}

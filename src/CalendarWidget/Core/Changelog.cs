using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CalendarWidget
{
    // 版本紀錄的一個版本。Lines 每一行已去掉前面的 Markdown 符號，Kind 說明是哪一種
    internal sealed class ChangelogEntry
    {
        public string Heading = "";                                  // 例如「0.9.0（2026-10-30）」
        public readonly List<ChangelogLine> Lines = new List<ChangelogLine>();
    }

    internal enum ChangelogLineKind { Section, Bullet, Text }

    internal sealed class ChangelogLine
    {
        public ChangelogLineKind Kind;
        public string Text = "";
    }

    // 讀 CHANGELOG.md（每個版本一個「## [版本] - 日期」標題，底下可以有「### 小標題」與「- 項目」）。
    // 第一個版本標題之前的說明文字略過
    internal static class Changelog
    {
        private static readonly Regex VersionHeading = new Regex(@"^\[?(?<v>[^\]\s]+)\]?\s*[-–—]\s*(?<d>.+)$");

        public static List<ChangelogEntry> Parse(string text)
        {
            var entries = new List<ChangelogEntry>();
            if (string.IsNullOrEmpty(text)) return entries;

            ChangelogEntry current = null;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("## "))
                {
                    current = new ChangelogEntry { Heading = FormatHeading(line.Substring(3).Trim()) };
                    entries.Add(current);
                }
                else if (current == null)
                {
                    continue;   // 第一個版本之前的說明
                }
                else if (line.StartsWith("### "))
                {
                    current.Lines.Add(new ChangelogLine { Kind = ChangelogLineKind.Section, Text = line.Substring(4).Trim() });
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    current.Lines.Add(new ChangelogLine { Kind = ChangelogLineKind.Bullet, Text = line.Substring(2).Trim() });
                }
                else
                {
                    current.Lines.Add(new ChangelogLine { Kind = ChangelogLineKind.Text, Text = line });
                }
            }
            return entries;
        }

        // 「[0.9.0] - 2026-10-30」→「0.9.0（2026-10-30）」；格式不符就原樣顯示
        private static string FormatHeading(string heading)
        {
            Match m = VersionHeading.Match(heading);
            return m.Success ? m.Groups["v"].Value + "（" + m.Groups["d"].Value.Trim() + "）" : heading;
        }
    }
}

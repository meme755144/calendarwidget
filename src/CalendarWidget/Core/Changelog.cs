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
                else
                {
                    current.Lines.Add(ClassifyLine(line));
                }
            }
            return entries;
        }

        // GitHub Release 的內文（Markdown，沒有版本標題）→ 一版的內容。這段文字來自網路，只當純文字顯示，
        // 而且限制行數與每行長度，避免超長的內容把畫面撐爆。任何層級的「#」標題都當小標題
        public static ChangelogEntry ParseNotes(string notes, string heading, int maxLines)
        {
            var entry = new ChangelogEntry { Heading = heading };
            if (string.IsNullOrEmpty(notes)) return entry;
            foreach (string raw in notes.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                ChangelogLine parsed = line.StartsWith("#") ? new ChangelogLine { Kind = ChangelogLineKind.Section, Text = Clean(line.TrimStart('#')) } : ClassifyLine(line);
                if (parsed.Text.Length == 0) continue;
                if (parsed.Text.Length > 300) parsed.Text = parsed.Text.Substring(0, 300) + "…";
                entry.Lines.Add(parsed);
                if (entry.Lines.Count >= maxLines) break;
            }
            return entry;
        }

        private static ChangelogLine ClassifyLine(string line)
        {
            if (line.StartsWith("### ")) return new ChangelogLine { Kind = ChangelogLineKind.Section, Text = Clean(line.Substring(4)) };
            if (line.StartsWith("- ") || line.StartsWith("* ")) return new ChangelogLine { Kind = ChangelogLineKind.Bullet, Text = Clean(line.Substring(2)) };
            return new ChangelogLine { Kind = ChangelogLineKind.Text, Text = Clean(line) };
        }

        // 畫面上不顯示 Markdown 的粗體與程式碼符號
        private static string Clean(string text)
        {
            return text.Replace("**", "").Replace("`", "").Trim();
        }

        // 「[0.9.0] - 2026-10-30」→「0.9.0（2026-10-30）」；格式不符就原樣顯示
        private static string FormatHeading(string heading)
        {
            Match m = VersionHeading.Match(heading);
            return m.Success ? m.Groups["v"].Value + "（" + m.Groups["d"].Value.Trim() + "）" : heading;
        }
    }
}

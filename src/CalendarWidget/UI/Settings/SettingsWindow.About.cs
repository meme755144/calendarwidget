using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CalendarWidget
{
    // 「關於」分頁：版本號、檢查更新、連結、作者與授權、免責聲明、版本紀錄。這一頁沒有要儲存的設定
    public partial class SettingsWindow
    {
        private const int ChangelogEntriesShown = 5;

        // 由主視窗設定，跟系統匣選單的「檢查更新」是同一個動作
        internal Action CheckUpdate;

        private void BuildAboutTab()
        {
            var host = AboutHost;

            host.Children.Add(new TextBlock { Text = "月曆小工具 CalendarWidget", FontSize = 20, FontWeight = FontWeights.SemiBold });
            host.Children.Add(GrayText("版本 " + AppInfo.VersionText + "（測試版）", new Thickness(0, 4, 0, 0)));

            var update = new Button { Content = "檢查更新", Padding = new Thickness(16, 5, 16, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            update.Click += delegate { var h = CheckUpdate; if (h != null) h(); };
            host.Children.Add(update);
            host.Children.Add(GrayText(
                "只有按下這個按鈕（或系統匣選單的「檢查更新」）才會連線到 GitHub 查詢；程式不會自己檢查，也不會自己下載或安裝。有新版時，由你決定要不要打開下載頁面。",
                new Thickness(0, 6, 0, 0)));

            AboutHeading(host, "連結");
            host.Children.Add(Links(
                new KeyValuePair<string, string>("專案首頁（原始碼）", AppInfo.RepoUrl),
                new KeyValuePair<string, string>("下載歷代版本", AppInfo.ReleasesUrl),
                new KeyValuePair<string, string>("回報問題或提出建議", AppInfo.IssuesUrl),
                new KeyValuePair<string, string>("隱私權政策", AppInfo.PrivacyUrl),
                new KeyValuePair<string, string>("服務條款", AppInfo.TermsUrl)));
            host.Children.Add(GrayText("遇到問題、想要新功能，請到「回報問題或提出建議」開一張新的議題（Issue）。", new Thickness(0, 6, 0, 0)));

            AboutHeading(host, "作者與授權");
            host.Children.Add(new TextBlock { Text = "作者：" + AppInfo.Owner, Margin = new Thickness(0, 0, 0, 4) });
            host.Children.Add(GrayText(
                "Copyright (c) 2026 " + AppInfo.Owner + "。本程式以 MIT 授權條款釋出，原始碼公開在 GitHub：可以自由使用、修改與散布，但要保留著作權與授權聲明，而且不附帶任何保證。",
                new Thickness(0)));

            AboutHeading(host, "免責聲明");
            host.Children.Add(GrayText(
                "本程式依「現狀」提供，不附帶任何明示或默示的保證。因使用或無法使用本程式所造成的任何損失（包括漏掉行程或提醒、資料錯誤或遺失），作者概不負責。重要的行程與時間，請另外確認。",
                new Thickness(0, 0, 0, 8)));
            host.Children.Add(GrayText(
                "放假日與節日資料來自公開的台灣行事曆整理資料（ruyut/TaiwanCalendar，經 jsDelivr 取得）；颱風假資料來自 taiwan-typhoon-days，整理自行政院人事行政總處的公開網頁。兩者都不是官方資料，可能延遲、遺漏或錯誤，實際以官方公告為準，請勿作為影響人身安全或財產決策的唯一依據。",
                new Thickness(0, 0, 0, 8)));
            host.Children.Add(GrayText(
                "本程式是獨立的個人專案，與 Google、行政院人事行政總處無關，也沒有獲得其授權或背書。Google 是 Google LLC 的商標。",
                new Thickness(0)));

            AboutHeading(host, "版本紀錄");
            var entries = Changelog.Parse(AppInfo.ReadChangelog());
            if (entries.Count == 0)
            {
                host.Children.Add(GrayText("找不到版本紀錄。", new Thickness(0)));
            }
            for (int i = 0; i < entries.Count && i < ChangelogEntriesShown; i++) AddChangelogEntry(host, entries[i]);
            host.Children.Add(Links(new KeyValuePair<string, string>("完整的版本紀錄", AppInfo.RepoUrl + "/blob/main/CHANGELOG.md")));
        }

        private static void AboutHeading(Panel host, string text)
        {
            host.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 6) });
        }

        private static void AddChangelogEntry(Panel host, ChangelogEntry entry)
        {
            host.Children.Add(new TextBlock { Text = entry.Heading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
            foreach (ChangelogLine line in entry.Lines)
            {
                switch (line.Kind)
                {
                    case ChangelogLineKind.Section:
                        host.Children.Add(new TextBlock { Text = line.Text, Foreground = Gray, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
                        break;
                    case ChangelogLineKind.Bullet:
                        host.Children.Add(new TextBlock { Text = "•  " + line.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 1, 0, 1) });
                        break;
                    default:
                        host.Children.Add(new TextBlock { Text = line.Text, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
                        break;
                }
            }
        }

        // 一排連結；點了用預設瀏覽器打開（AppInfo.OpenUrl 只會打開本專案自己的網址）
        private static TextBlock Links(params KeyValuePair<string, string>[] links)
        {
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) };
            for (int i = 0; i < links.Length; i++)
            {
                if (i > 0) block.Inlines.Add(new Run("　"));
                string url = links[i].Value;
                var link = new Hyperlink(new Run(links[i].Key)) { NavigateUri = new Uri(url), ToolTip = url };
                link.Click += delegate { AppInfo.OpenUrl(url); };
                block.Inlines.Add(link);
            }
            return block;
        }
    }
}

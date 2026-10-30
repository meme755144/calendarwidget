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
        private const int NewVersionNotesMaxLines = 60;

        // 由主視窗設定，跟系統匣選單的「檢查更新」查的是同一件事，但結果交給呼叫端（這一頁直接顯示在按鈕下面，不跳提醒框）
        internal Action<Action<UpdateResult>> CheckUpdate;

        private void BuildAboutTab()
        {
            var host = AboutHost;

            host.Children.Add(new TextBlock { Text = "月曆小工具 CalendarWidget", FontSize = 20, FontWeight = FontWeights.SemiBold });
            host.Children.Add(GrayText("版本 " + AppInfo.VersionText + "（測試版）", new Thickness(0, 4, 0, 0)));

            var update = new Button { Content = "檢查更新", Padding = new Thickness(16, 5, 16, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            var updateResult = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            var newVersionNotes = new StackPanel { Visibility = Visibility.Collapsed };   // 有新版時，新版的內容放在版本紀錄的最上面
            update.Click += delegate
            {
                var check = CheckUpdate;
                if (check == null) return;
                update.IsEnabled = false;
                update.Content = "檢查中…";
                updateResult.Visibility = Visibility.Collapsed;
                check(delegate(UpdateResult result)
                {
                    update.IsEnabled = true;
                    update.Content = "檢查更新";
                    ShowUpdateResult(updateResult, newVersionNotes, result);
                });
            };
            host.Children.Add(update);
            host.Children.Add(GrayText(
                "只有按下這個按鈕（或系統匣選單的「檢查更新」）才會連線到 GitHub 查詢；程式不會自己檢查，也不會自己下載或安裝。有新版時，由你決定要不要打開下載頁面。",
                new Thickness(0, 6, 0, 0)));
            host.Children.Add(updateResult);

            // 版本紀錄跟版本、更新是同一件事，緊接在後面
            AboutHeading(host, "版本紀錄");
            host.Children.Add(newVersionNotes);
            var entries = Changelog.Parse(AppInfo.ReadChangelog());
            if (entries.Count == 0)
            {
                host.Children.Add(GrayText("找不到版本紀錄。", new Thickness(0)));
            }
            for (int i = 0; i < entries.Count && i < ChangelogEntriesShown; i++) AddChangelogEntry(host, entries[i], i == 0);   // 只展開最新的一版
            host.Children.Add(Links(new KeyValuePair<string, string>("完整的版本紀錄", AppInfo.RepoUrl + "/blob/main/CHANGELOG.md")));

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
        }

        // 檢查更新的結果：就顯示在按鈕下面，使用者不用移開視線。
        // 有新版時，新版的內容（GitHub Release 的內文）放進版本紀錄的最上面（newVersionNotes），跟已安裝的各版排在一起
        private static void ShowUpdateResult(Panel panel, Panel newVersionNotes, UpdateResult result)
        {
            panel.Children.Clear();
            newVersionNotes.Children.Clear();
            newVersionNotes.Visibility = Visibility.Collapsed;
            string current = "目前的版本：" + AppInfo.VersionText;
            switch (result.Status)
            {
                case UpdateStatus.NewVersion:
                    panel.Children.Add(new TextBlock { Text = "有新版本 " + result.Version + "（" + current + "）", FontWeight = FontWeights.SemiBold });
                    panel.Children.Add(GrayText("新版的更新內容在下面「版本紀錄」的最上面。", new Thickness(0, 2, 0, 0)));

                    ChangelogEntry notes = Changelog.ParseNotes(result.Notes, "新版 " + result.Version + "（尚未安裝）", NewVersionNotesMaxLines);
                    if (notes.Lines.Count == 0) notes.Lines.Add(new ChangelogLine { Kind = ChangelogLineKind.Text, Text = "這個版本沒有附更新說明，詳情請看下載頁面。" });
                    AddChangelogEntry(newVersionNotes, notes, true);
                    newVersionNotes.Visibility = Visibility.Visible;

                    var go = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
                    go.Inlines.Add(LinkRun("前往下載頁面", result.Url));
                    panel.Children.Add(go);
                    break;
                case UpdateStatus.UpToDate:
                    panel.Children.Add(new TextBlock { Text = "目前已是最新版本（" + AppInfo.VersionText + "）。" });
                    break;
                case UpdateStatus.NoRelease:
                    panel.Children.Add(new TextBlock { Text = "GitHub 上還沒有發布任何版本。" });
                    break;
                default:
                    panel.Children.Add(new TextBlock { Text = "檢查更新失敗：" + result.Message, Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), TextWrapping = TextWrapping.Wrap });
                    break;
            }
            panel.Visibility = Visibility.Visible;
        }

        private static Hyperlink LinkRun(string text, string url)
        {
            var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), ToolTip = url };
            link.Click += delegate { AppInfo.OpenUrl(url); };
            return link;
        }

        private static void AboutHeading(Panel host, string text)
        {
            host.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 6) });
        }

        // 版本號當標題，點一下展開／摺疊底下的內容
        private static void AddChangelogEntry(Panel host, ChangelogEntry entry, bool expanded)
        {
            var body = new StackPanel { Margin = new Thickness(14, 0, 0, 4), Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
            foreach (ChangelogLine line in entry.Lines)
            {
                switch (line.Kind)
                {
                    case ChangelogLineKind.Section:
                        body.Children.Add(new TextBlock { Text = line.Text, Foreground = Gray, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
                        break;
                    case ChangelogLineKind.Bullet:
                        body.Children.Add(new TextBlock { Text = "•  " + line.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 1, 0, 1) });
                        break;
                    default:
                        body.Children.Add(new TextBlock { Text = line.Text, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
                        break;
                }
            }

            var title = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, ToolTip = "點一下展開或摺疊" };
            Action refresh = delegate { title.Text = (body.Visibility == Visibility.Visible ? "▾  " : "▸  ") + entry.Heading; };
            title.MouseLeftButtonUp += delegate
            {
                body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                refresh();
            };
            title.MouseEnter += delegate { title.TextDecorations = TextDecorations.Underline; };
            title.MouseLeave += delegate { title.TextDecorations = null; };
            refresh();

            host.Children.Add(title);
            host.Children.Add(body);
        }

        // 一排連結；點了用預設瀏覽器打開（AppInfo.OpenUrl 只會打開本專案自己的網址）
        private static TextBlock Links(params KeyValuePair<string, string>[] links)
        {
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) };
            for (int i = 0; i < links.Length; i++)
            {
                if (i > 0) block.Inlines.Add(new Run("　"));
                block.Inlines.Add(LinkRun(links[i].Key, links[i].Value));
            }
            return block;
        }
    }
}

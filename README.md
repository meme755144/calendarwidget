# 月曆小工具（CalendarWidget）

Windows 桌面月曆小工具：C# WPF（.NET Framework 4.8），不需要安裝其他東西。

功能：月曆、ICS 來源、Google 日曆與待辦（讀取、新增、編輯、刪除行程）、本機待辦與行程、節日與行程提醒、依條件產生台灣節日 ICS。

## 專案結構

```
CalendarWidget.sln              解決方案（主程式與全部測試）
src/CalendarWidget/             小工具本體
  Core/                         資料模型、設定、儲存（Models、Settings、Storage、Json）
  Accounts/                     線上帳號的共同介面（Accounts.cs：ICalendarAccount、AccountRegistry）
    Google/                     Google 帳號：授權、API、管理員、設定儲存
  Calendars/                    行程來源：ICS 來源與解析、台灣節日資料與 ICS 產生
  Reminders/                    節日提醒、行程提醒與提醒視窗
  UI/                           主視窗、新增／編輯行程視窗與相關邏輯
    Settings/                   設定視窗（一般、日曆、ICS 來源、Google、ICS 產生）
  Platform/                     Windows 系統相關（系統匣圖示、Win32 呼叫）
tests/                          測試專案、假伺服器、測試用資料；./tests/run-all.ps1 一次跑完
docs/                           Google OAuth 品牌用的網頁（首頁、隱私權政策、服務條款、標誌），用 GitHub Pages 發布
site-root/                      放在 GitHub 使用者網站根目錄的簡單首頁（給 Search Console 驗證根網址用）
```

要新增一種線上帳號（例如 Microsoft、CalDAV）：在 `Accounts/` 底下新增一個資料夾，寫一個實作 `ICalendarAccount` 的管理類別，在 `App.xaml.cs` 用 `accounts.Add(...)` 註冊，再加一個設定分頁。新增、編輯、刪除行程的視窗不用改。

## 建置

```
dotnet build CalendarWidget.sln -c Release
```

只建小工具：`dotnet build src/CalendarWidget -c Release`。輸出在倉庫根目錄的 `bin\Release\net48\CalendarWidget.exe`。

### Google 用戶端（選用）

在 `src/CalendarWidget/` 放一個 `google-client.json`（Google Cloud 的「桌面應用程式」OAuth 用戶端，格式同下載的 JSON），建置時會包進程式；沒有這個檔也能建置，使用者就要在設定裡自己填用戶端 ID 與密鑰。這個檔含密鑰，已被 `.gitignore` 排除，不要提交。

## 測試

```
./tests/run-all.ps1
```

需要 .NET SDK 與 Node.js（假的 Google、ICS、節日資料伺服器）。測試用的資料夾與記錄檔放在暫存資料夾，不會動到你的設定。測試專案是把需要的原始碼檔直接連結進來（不載入 WPF），所以搬動或新增原始碼檔時，要一起更新 `tests/*/*.csproj` 裡的路徑。

## 資料位置

- `%APPDATA%\CalendarWidget\`：設定、待辦、本機行程、日曆清單
- `%LOCALAPPDATA%\CalendarWidget\`：加密的網址與 Google 憑證、快取
- 環境變數 `CALENDARWIDGET_DATA` 可改成別的資料夾（測試用）

## 節日資料

「ICS 產生」使用行政院人事行政總處的「政府行政機關辦公日曆表」（政府資料開放授權條款第 1 版），經 [ruyut/TaiwanCalendar](https://github.com/ruyut/TaiwanCalendar) 整理成 JSON。

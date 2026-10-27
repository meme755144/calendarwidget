# 月曆小工具（CalendarWidget）

Windows 桌面月曆小工具：C# WPF（.NET Framework 4.8），不需要安裝其他東西。

功能：月曆、ICS 來源、Google 日曆與待辦（讀取、新增、編輯、刪除行程）、本機待辦與行程、節日與行程提醒、依條件產生台灣節日 ICS。

## 建置

```
dotnet build -c Release
```

輸出在 `bin\Release\net48\CalendarWidget.exe`。

### Google 用戶端（選用）

在專案資料夾放一個 `google-client.json`（Google Cloud 的「桌面應用程式」OAuth 用戶端，格式同下載的 JSON），建置時會包進程式；沒有這個檔也能建置，使用者就要在設定裡自己填用戶端 ID 與密鑰。這個檔含密鑰，已被 `.gitignore` 排除，不要提交。

## 測試

```
./tests/run-all.ps1
```

需要 .NET SDK 與 Node.js（假的 Google、ICS、節日資料伺服器）。測試用的資料夾與記錄檔放在暫存資料夾，不會動到你的設定。

## 資料位置

- `%APPDATA%\CalendarWidget\`：設定、待辦、本機行程、日曆清單
- `%LOCALAPPDATA%\CalendarWidget\`：加密的網址與 Google 憑證、快取
- 環境變數 `CALENDARWIDGET_DATA` 可改成別的資料夾（測試用）

## 節日資料

「ICS 產生」使用行政院人事行政總處的「政府行政機關辦公日曆表」（政府資料開放授權條款第 1 版），經 [ruyut/TaiwanCalendar](https://github.com/ruyut/TaiwanCalendar) 整理成 JSON。

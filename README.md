# 月曆小工具（CalendarWidget）

Windows 桌面月曆小工具：C# WPF（.NET Framework 4.8），不需要安裝其他東西。

功能：月曆、ICS 來源、Google 日曆與待辦（讀取、新增、編輯、刪除行程）、本機待辦與行程、節日與行程提醒、依條件產生台灣節日 ICS、平日放假日標紅字、颱風假通知（預設關閉）。

> 目前是 **0.9.0 測試版**：功能大致齊全，但有幾項還沒在真實環境完整驗證，詳見 [CHANGELOG.md](CHANGELOG.md) 的「已知限制」。

## 下載與安裝

1. 到 [Releases](https://github.com/meme755144/calendarwidget/releases) 下載最新版的 `CalendarWidget-x.y.z.zip`，解壓縮到任何資料夾（不需要安裝程式）。
2. 執行 `CalendarWidget.exe`，系統匣會出現小圖示，右鍵可以開設定、回到今天、檢查更新、結束。

**需求：** Windows 10 / 11，.NET Framework 4.8（Windows 10 1903 之後內建，更舊的系統請先安裝）。

**第一次執行可能出現 SmartScreen 警告。** 這個程式沒有數位簽章（簽章要花錢、還要驗證身分，目前先不做），Windows 會顯示「Windows 已保護您的電腦」。點「其他資訊」→「仍要執行」即可。想確認檔案沒有被更動，可以比對 SHA256（Releases 頁面每個版本都有寫）：

```
Get-FileHash .\CalendarWidget-0.9.0.zip -Algorithm SHA256
```

**更新：** 系統匣圖示按右鍵 →「檢查更新」（或設定 →「關於」）。只有你按了才會連線到 GitHub 查詢；有新版會提示，由你決定要不要打開下載頁面。程式不會自己檢查，也不會自己下載或安裝。

**移除：** 關閉程式，刪除資料夾。如果開過「開機自動啟動」，先到設定取消勾選。程式存放的資料在 `%APPDATA%\CalendarWidget\` 與 `%LOCALAPPDATA%\CalendarWidget\`，要清乾淨就一併刪除。

## 回報問題與建議

遇到問題、想要新功能，請到 [Issues](https://github.com/meme755144/calendarwidget/issues) 開一張新的議題。程式的「設定 → 關於」也有連結。

## 授權與免責聲明

- 程式碼以 [MIT License](LICENSE) 授權，可以自由使用、修改與散布，但要保留著作權與授權聲明。
- 本程式依「現狀」提供，不附帶任何保證；因使用或無法使用所造成的損失（包括漏掉行程或提醒、資料錯誤或遺失），作者概不負責。
- 放假日與颱風假資料都不是官方資料，可能延遲、遺漏或錯誤，實際以官方公告為準，請勿作為影響人身安全或財產決策的唯一依據。
- 本程式是獨立的個人專案，與 Google、行政院人事行政總處無關，也沒有獲得其授權或背書。Google 是 Google LLC 的商標。
- 隱私：資料都存在你自己的電腦，作者沒有伺服器、不蒐集任何資料。詳見[隱私權政策](https://meme755144.github.io/calendarwidget/privacy.html)與[服務條款](https://meme755144.github.io/calendarwidget/terms.html)。
- Releases 裡的版本內建了作者申請的 Google OAuth 用戶端（桌面應用程式類型），用來登入 Google；原始碼裡沒有這個檔案（`.gitignore` 已排除）。自己從原始碼建置的版本沒有內建用戶端，要在設定裡填自己的。

## 專案結構

```
CalendarWidget.sln              解決方案（主程式與全部測試）
CHANGELOG.md                    版本紀錄（會包進程式，顯示在「關於」頁）
RELEASING.md                    發版步驟（給維護者）
LICENSE                         MIT 授權
src/CalendarWidget/             小工具本體
  Core/                         資料模型、設定、儲存、版本資訊（Models、Settings、Storage、Json、AppInfo、Changelog）
  Accounts/                     線上帳號的共同介面（Accounts.cs：ICalendarAccount、AccountRegistry）
    Google/                     Google 帳號：授權、API、管理員、設定儲存
  Calendars/                    行程來源：ICS 來源與解析、台灣節日資料與 ICS 產生
  Reminders/                    節日提醒、行程提醒、颱風假提醒與提醒視窗
  Typhoon/                      颱風假：讀取停班停課資料、依關注的縣市與鄉鎮區篩選（資料由另一個專案 taiwan-typhoon-days 定時整理）
  Updates/                      檢查更新：查詢 GitHub 最新發行版本、比對版本號
  UI/                           主視窗、新增／編輯行程視窗與相關邏輯
    Settings/                   設定視窗（一般、日曆、ICS 來源、Google、ICS 產生、颱風假、關於）
  Platform/                     Windows 系統相關（系統匣圖示、Win32 呼叫）
tests/                          測試專案、假伺服器、測試用資料；./tests/run-all.ps1 一次跑完
docs/                           用 GitHub Pages 發布的網頁（首頁、隱私權政策、服務條款、標誌、Google 驗證檔），Google OAuth 品牌驗證要用
```

要新增一種線上帳號（例如 Microsoft、CalDAV）：在 `Accounts/` 底下新增一個資料夾，寫一個實作 `ICalendarAccount` 的管理類別，在 `App.xaml.cs` 用 `accounts.Add(...)` 註冊，再加一個設定分頁。新增、編輯、刪除行程的視窗不用改。

## 建置

```
dotnet build CalendarWidget.sln -c Release
```

只建小工具：`dotnet build src/CalendarWidget -c Release`。輸出在倉庫根目錄的 `bin\Release\net48\CalendarWidget.exe`。版本號只在 `src/CalendarWidget/CalendarWidget.csproj` 的 `<Version>` 設定。

### Google 用戶端（選用）

在 `src/CalendarWidget/` 放一個 `google-client.json`（Google Cloud 的「桌面應用程式」OAuth 用戶端，格式同下載的 JSON），建置時會包進程式；沒有這個檔也能建置，使用者就要在設定裡自己填用戶端 ID 與密鑰。這個檔含密鑰，已被 `.gitignore` 排除，不要提交。

## 測試

```
./tests/run-all.ps1
```

需要 .NET SDK 與 Node.js（假的 Google、ICS、節日資料、停班停課資料、GitHub 發行版本伺服器）。測試用的資料夾與記錄檔放在暫存資料夾，不會動到你的設定。測試專案是把需要的原始碼檔直接連結進來（不載入 WPF），所以搬動或新增原始碼檔時，要一起更新 `tests/*/*.csproj` 裡的路徑。

## 資料位置

- `%APPDATA%\CalendarWidget\`：設定、待辦、本機行程、日曆清單
- `%LOCALAPPDATA%\CalendarWidget\`：加密的網址與 Google 憑證、快取（含 `holidays\` 放假日資料、`typhoon-latest.json` 上次讀到的停班停課資料）
- 環境變數 `CALENDARWIDGET_DATA` 可改成別的資料夾（測試用）

## 節日資料

「ICS 產生」與月曆上的「放假日標示」（平日的補假、連假標紅字；下載後快取在 `%LOCALAPPDATA%\CalendarWidget\holidays\`，每週更新）使用行政院人事行政總處的「政府行政機關辦公日曆表」（政府資料開放授權條款第 1 版），經 [ruyut/TaiwanCalendar](https://github.com/ruyut/TaiwanCalendar) 整理成 JSON。

## 颱風假通知

設定 → 颱風假：打開之後，小工具會定時讀取停班停課資料，你關注的縣市停班停課時跳出提醒框，日期標紅字，點那天會在標題下面列出是哪裡停班停課。

- **預設關閉。** 關閉時不連網、不佔資源；打開後開機（或睡眠醒來）立刻讀一次，之後每 10 分鐘讀一次。
- **資料來源是另一個獨立的專案** [taiwan-typhoon-days](https://github.com/meme755144/taiwan-typhoon-days)：它用 GitHub Actions 定時整理人事行政總處的「天然災害停止上班及上課情形」網頁，輸出 JSON。小工具只讀那份 JSON，不會自己去爬政府網站；資料網址寫在程式裡，不在設定畫面上（換成別的來源，格式不一樣就解析不了），只有開發測試時才會直接改 settings.ini 的 `TyphoonUrl`。資料不是官方的，可能延遲或遺漏，實際停班停課以官方公告為準。
- **縣市與鄉鎮區：** 縣市用按鈕點選，選了的縣市整個縣市停班或停課時通知。每個已選的縣市底下可以另外勾選要通知的鄉鎮區（全台 368 個，名稱來自 [donma/TaiwanAddressCityAreaRoadChineseEnglishJSON](https://github.com/donma/TaiwanAddressCityAreaRoadChineseEnglishJSON)，與另一份常被引用的 [taiwan_districts.json](https://gist.github.com/abc873693/2804e64324eaaf26515281710e1792df) 逐筆比對內容相同，扣掉不是鄉鎮市區的釣魚臺、東沙、南沙後數量與官方一致）；沒勾任何區就不通知鄉鎮區。公告寫的是區裡的某個地方（例如「秀林鄉大天祥地區」「桃源區拉芙蘭里」）時，算那個區（秀林鄉、桃源區）。單一學校的停課不通知。
- **提醒框**跟節日提醒一樣：按「好」確認，或選「N 分鐘後再問我」；間隔與時段沿用「一般 → 提醒」的設定。新公告出現時（在提醒時段內）立刻跳出，不用等下一次排定的時間；時段外等到時段開始。確認紀錄在 `typhoon-acked.txt`。
- 看不懂的公告格式、資料來源被擋或太久沒更新，都不會默默略過：看不懂的公告會跳出「請自行查看」，資料超過 8 小時沒更新會在設定畫面提示。

# 月曆小工具（CalendarWidget）

Windows 桌面月曆小工具：C# WPF（.NET Framework 4.8），不需要安裝其他東西。

功能：月曆、ICS 來源、Google 日曆與待辦（讀取、新增、編輯、刪除行程）、本機待辦與行程、節日與行程提醒、依條件產生台灣節日 ICS、平日放假日標紅字、颱風假通知（預設關閉）。

## 專案結構

```
CalendarWidget.sln              解決方案（主程式與全部測試）
src/CalendarWidget/             小工具本體
  Core/                         資料模型、設定、儲存（Models、Settings、Storage、Json）
  Accounts/                     線上帳號的共同介面（Accounts.cs：ICalendarAccount、AccountRegistry）
    Google/                     Google 帳號：授權、API、管理員、設定儲存
  Calendars/                    行程來源：ICS 來源與解析、台灣節日資料與 ICS 產生
  Reminders/                    節日提醒、行程提醒、颱風假提醒與提醒視窗
  Typhoon/                      颱風假：讀取停班停課資料、依關注的縣市與鄉鎮區篩選（資料由另一個專案 taiwan-typhoon-days 定時整理）
  UI/                           主視窗、新增／編輯行程視窗與相關邏輯
    Settings/                   設定視窗（一般、日曆、ICS 來源、Google、ICS 產生、颱風假）
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

需要 .NET SDK 與 Node.js（假的 Google、ICS、節日資料、停班停課資料伺服器）。測試用的資料夾與記錄檔放在暫存資料夾，不會動到你的設定。測試專案是把需要的原始碼檔直接連結進來（不載入 WPF），所以搬動或新增原始碼檔時，要一起更新 `tests/*/*.csproj` 裡的路徑。

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


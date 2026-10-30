# 開發筆記

給想自己建置、修改這個專案的人看。使用說明請看[根目錄的 README](../README.md)，發版步驟請看 [RELEASING.md](../RELEASING.md)。

## 建置

需要 .NET SDK（能建 `net48` 的任何版本）與 Windows。

```
dotnet build CalendarWidget.sln -c Release
```

只建小工具：`dotnet build src/CalendarWidget -c Release`。輸出在倉庫根目錄的 `bin\Release\net48\`（`CalendarWidget.exe` 加 `CalendarWidget.exe.config`，缺 `.config` 程式跑不起來）。版本號只在 `src/CalendarWidget/CalendarWidget.csproj` 的 `<Version>` 設定。

### Google 用戶端（選用）

在 `src/CalendarWidget/` 放一個 `google-client.json`（Google Cloud 的「桌面應用程式」OAuth 用戶端，格式同下載的 JSON），建置時會包進程式；沒有這個檔也能建置，使用者就要在設定裡自己填用戶端 ID 與密鑰。這個檔含密鑰，已被 `.gitignore` 排除，**不要提交**，也不要把內容貼到任何地方。

## 測試

```
./tests/run-all.ps1
```

需要 .NET SDK 與 Node.js（假的 Google、ICS、節日資料、停班停課資料、GitHub 發行版本伺服器）。測試用的資料夾與記錄檔放在暫存資料夾，不會動到你的設定。

| 測試專案 | 測什麼 |
| --- | --- |
| `evtest` | 行程提醒的規則、本機行程、清單排序（RowReorder） |
| `todotest` | 本機待辦 |
| `remtest` | 節日提醒與確認紀錄 |
| `ordertest` | 日曆順序 |
| `icstest` | ICS 解析：時間與時區、重複規則、提醒、壞資料 |
| `srcmgrtest` | ICS 來源管理、載入範圍、設定檔 |
| `holtest` | 放假日資料與 ICS 產生 |
| `typhoontest` | 颱風假資料解析、縣市與鄉鎮區篩選、提醒 |
| `updatetest` | 檢查更新、版本紀錄解析、網址白名單，以及 csproj 版本要等於 CHANGELOG 最新一筆 |
| `googletest` | Google 登入流程（PKCE、state、取消、逾時）、API 呼叫、新增／修改／刪除行程 |
| `mgrtest` | Google 管理器：抓取、快取、寫入、帳號介面 |

測試專案是把需要的原始碼檔直接連結進來（不載入 WPF），所以搬動或新增原始碼檔時，要一起更新 `tests/*/*.csproj` 裡的路徑。介面本身（`UI/` 底下）沒有自動化測試，是用下面的開發參數加截圖檢查。

測試會用到的環境變數：

| 變數 | 用途 |
| --- | --- |
| `CALENDARWIDGET_DATA` | 把設定、快取、憑證全部改放到這個資料夾（測試一定要設，否則會動到你平常的資料）；設了之後單一執行個體鎖也會換成測試專用的 |
| `CALENDARWIDGET_GOOGLE_AUTH` / `_TOKEN` / `_REVOKE` / `_CALENDAR` / `_TASKS` | 把 Google 的各個網址換成假伺服器（只有設了 `CALENDARWIDGET_DATA` 才有效） |
| `CALENDARWIDGET_UPDATE_URL` | 把「檢查更新」查詢的網址換成假伺服器（同樣只有設了 `CALENDARWIDGET_DATA` 才有效） |

### 開發參數

啟動 `CalendarWidget.exe` 時可以加這些參數，用來直接檢查某個畫面（WPF 不吃模擬的滑鼠訊息，所以用參數而不是自動點擊）：

`--demo`（載入示範資料、不寫入任何資料）、`--picker`／`--picker=years`、`--step=N`、`--pick=N`、`--select=N`、`--show-tooltip=N`、`--day-menu=N`、`--add-event=N`、`--edit-event=K`、`--ask-delete=K`、`--remind-now`、`--open-settings`／`--open-settings=N`（N 是分頁編號，從 0 算）、`--settings-end`、`--typhoon-expand`、`--show-widget`（啟動後把月曆叫到最前面，等同雙按系統匣圖示）、`--maker-preview`、`--drag-calendar=從,到`、`--check-update`。

## 專案結構

```
CalendarWidget.sln              解決方案（主程式與全部測試）
CHANGELOG.md                    版本紀錄（會包進程式，顯示在「關於」頁）
RELEASING.md                    發版步驟（給維護者）
LICENSE                         MIT 授權
src/CalendarWidget/             小工具本體
  Core/                         資料模型、設定、儲存、版本資訊（Models、Settings、Storage、AtomicFile、Json、AppInfo、Changelog、ErrorLog）
  Accounts/                     線上帳號的共同介面（Accounts.cs：ICalendarAccount、AccountRegistry）
    Google/                     Google 帳號：授權、API、管理員、設定儲存
  Calendars/                    行程來源：ICS 來源與解析、台灣節日資料與 ICS 產生、放假日標示
  Reminders/                    節日提醒、行程提醒、颱風假提醒與提醒視窗
  Typhoon/                      颱風假：讀取停班停課資料、依關注的縣市與鄉鎮區篩選
  Updates/                      檢查更新：查詢 GitHub 最新發行版本、比對版本號
  UI/                           主視窗、新增／編輯行程視窗與相關邏輯
    Settings/                   設定視窗（一般、日曆、ICS 來源、Google、ICS 產生、颱風假、關於）
  Platform/                     Windows 系統相關（系統匣圖示、Win32 呼叫）
tests/                          測試專案、假伺服器、測試用資料；./tests/run-all.ps1 一次跑完
docs/                           用 GitHub Pages 發布的網頁（首頁、隱私權政策、服務條款、標誌、Google 驗證檔），以及這份開發筆記
```

### 新增一種線上帳號

例如 Microsoft、CalDAV：在 `Accounts/` 底下新增一個資料夾，寫一個實作 `ICalendarAccount` 的管理類別，在 `App.xaml.cs` 用 `accounts.Add(...)` 註冊，再加一個設定分頁。新增、編輯、刪除行程的視窗不用改。

## 設計原則

- **月曆的寬度只由日期格子決定**：寬度 = 7 欄 ×（格子寬 + 左右邊界 2），格子寬 = 36 × 設定的「月曆寬度」。下方清單（`DetailPanel`）與選年月畫面（`MonthPicker`）的寬度明確設成同一個值，裡面的文字用「…」截掉或換行，不能靠內容撐寬，所以點到有很長名稱的日期時月曆的寬度不會改變。新增畫面元素時，不要在裡面用固定的 `MaxWidth`（寬度改了就對不上），讓它吃容器剩下的寬度。

- **資源要低**：沒有輪詢。提醒只排「下一個要醒來的時間」一個計時器；跨日用一個午夜計時器；外部來源各有自己的更新間隔；閒置時 CPU 是 0。沒有載入 WinForms 與 System.Drawing，系統匣用 Win32 直接呼叫。啟動後與重畫後會把用不到的記憶體頁交還系統（`TrimMemory`），工作管理員看到的用量會小很多。
- **執行緒規則**：所有網路與檔案讀寫在背景執行緒，結果用 `Dispatcher.BeginInvoke` 切回 UI 執行緒才碰 `ItemStore` 與畫面。同一個來源同時只抓一個；抓的期間設定被改掉，結果要丟掉或重抓（見 `SourceManager`、`GoogleManager` 的世代與重抓旗標）。
- **不能弄壞使用者的資料**：存檔一律用 `AtomicFile`（先寫暫存檔再替換）；外部資料（ICS、颱風 JSON）一律當成不可信，單一事件壞掉只略過那一個，下載有大小與時間上限。
- **外部資料不能啟動任何東西**：`AppInfo.OpenUrl` 只會打開本專案自己的網址（白名單）；檢查更新不下載、不執行；ICS 的 `file:` 只接受本機磁碟。
- **隱私**：不蒐集任何資料，沒有伺服器。憑證與 ICS 網址用 DPAPI 加密。程式會連到的網址一覽：

  | 網址 | 什麼時候 |
  | --- | --- |
  | `accounts.google.com`、`oauth2.googleapis.com`、`www.googleapis.com`、`tasks.googleapis.com` | 你連接 Google 帳號之後 |
  | 你輸入的 ICS 網址 | 依各來源的更新間隔 |
  | `cdn.jsdelivr.net`（放假日資料） | 開著「放假日標示」或使用「ICS 產生」時 |
  | `raw.githubusercontent.com`（颱風假資料） | 打開颱風假通知之後 |
  | `api.github.com` | 只有你按「檢查更新」時 |

## 資料位置

- `%APPDATA%\CalendarWidget\`：設定、待辦、本機行程、日曆清單與順序、各種確認紀錄。可以備份。
- `%LOCALAPPDATA%\CalendarWidget\`：加密的網址與 Google 憑證、快取（`holidays\` 放假日資料、`typhoon-latest.json` 上次讀到的停班停課資料、各來源的行程快取）、`error.log`（未預期的錯誤記錄，超過 256 KB 會從頭寫）。壞了可以刪，會重新抓。

## 已知問題與待改進

審查程式碼時發現、但還沒處理的項目（影響都不大，列在這裡免得忘記）：

- Google 增量授權（第一次新增行程時多授權一次）時，如果瀏覽器登入的是另一個 Google 帳號，程式不會發現帳號換了；之後的呼叫會用新帳號的憑證，但日曆清單還是舊帳號的。登入網址已帶 `login_hint`，要選到別的帳號才會發生。
- Google 的寫入錯誤沒有細分原因：被限流（429）或沒有編輯權限，都會顯示成「你可能沒有編輯權限」。
- 任何一個 Google 日曆失敗，下一輪（10 分鐘後）會把所有日曆重抓；Google 與 ICS 的更新都是完整重抓，沒有用 ETag 或增量同步。
- ICS 的 `FREQ=HOURLY/MINUTELY/SECONDLY` 只顯示第一次；整天事件只有 `DURATION` 沒有 `DTEND` 時只顯示一天；不在對照表裡的 IANA 時區名稱會被當成電腦的時區。
- 線上刪除進行中，如果畫面剛好被其他更新重畫，進度與失敗訊息可能看不到；已經開著新增／編輯視窗時，在別的日期按右鍵新增會只是把舊視窗拉到前面。
- 從睡眠恢復時會立刻補抓，網路通常還沒連上，失敗後最多約 10 分鐘才會重試。
- 沒有監聽螢幕設定變更（拔掉外接螢幕後，視窗可能留在看不到的位置）。
- 提醒框開著時每 30 秒重建一次內容。

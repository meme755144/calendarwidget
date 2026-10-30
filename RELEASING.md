# 發版步驟

給維護者看的。檢查更新用的是 GitHub 的「最新發行版本」，所以**要發一個正式的 Release**，使用者才抓得到。

## 發版前

1. 工作區要乾淨，`./tests/run-all.ps1` 全部通過。
2. 改 `src/CalendarWidget/CalendarWidget.csproj` 的 `<Version>`，並在 `CHANGELOG.md` 最上面加上新版本的段落（標題格式 `## [x.y.z] - YYYY-MM-DD`）。兩邊的版本號必須一致，`updatetest` 會檢查，忘了改會測試失敗。
3. commit 並 push。

## 建置與打包

1. 要發給別人的版本，先把 `google-client.json` 放在 `src/CalendarWidget/`（建置時才會內建 Google 用戶端；這個檔不會進 git）。
2. 建置：

   ```
   dotnet build src/CalendarWidget -c Release
   ```

   輸出在 `bin\Release\net48\`，需要的是 `CalendarWidget.exe` 與 `CalendarWidget.exe.config` 兩個檔（缺 `.config` 程式跑不起來）。
3. 打包成 `CalendarWidget-x.y.z.zip`（兩個檔放在 zip 的最上層）。
4. 算 SHA256，記下來，要貼在 Release 說明裡：

   ```
   Get-FileHash .\CalendarWidget-x.y.z.zip -Algorithm SHA256
   ```
5. 簡單確認：解壓縮後執行，開「設定 → 關於」看版本號，「設定 → Google 帳號」不應該要求填用戶端 ID（代表有內建）。

## 在 GitHub 發佈

1. 打 tag 並 push：`git tag vx.y.z` → `git push origin vx.y.z`。
2. GitHub → Releases → Draft a new release：選剛才的 tag，標題 `vx.y.z`，內文貼上 CHANGELOG 該版本的內容（不含 `## [x.y.z]` 那行標題），再加一行 SHA256；附上 zip。舊版使用者按「檢查更新」時，程式會把這段內文顯示成「版本紀錄」最上面的「新版 x.y.z（尚未安裝）」，所以內文就是他們看到的更新內容，請寫給使用者看。
3. **不要勾「Set as a pre-release」**，要勾「Set as the latest release」。檢查更新用的是 `releases/latest`，這個 API 看不到 pre-release。
4. 發佈後，用舊版程式按「檢查更新」，確認會提示新版；新版自己按則應該顯示「目前已是最新版本」。

## 注意

- 沒有數位簽章，所以每個版本都要附 SHA256；README 已經說明 SmartScreen 警告怎麼處理。
- 發佈出去的 Release 不要刪掉或換檔案，使用者的 SHA256 比對會失敗。要修正就發新的版本號。
- 程式不會自己檢查更新，使用者要按「檢查更新」才會查詢。

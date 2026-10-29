# 跑全部測試：./tests/run-all.ps1
# 需要 .NET SDK 與 Node.js（假的 Google / ICS / 節日資料伺服器）。測試用的資料夾與記錄檔都放在暫存資料夾，不會動到你的設定
param([string]$S = $PSScriptRoot)
$W = Join-Path $env:TEMP 'calendarwidget-tests'
New-Item -ItemType Directory $W -Force | Out-Null
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Get-NetTCPConnection -LocalPort 18765, 18766 -State Listen -ErrorAction SilentlyContinue | % { Stop-Process -Id $_.OwningProcess -Force }
$ics = Start-Process node -ArgumentList "`"$S\icsserver.mjs`"", "`"$W\ics.log`"" -PassThru -WindowStyle Hidden
foreach ($t in 'evtest', 'todotest', 'remtest', 'ordertest', 'icstest', 'srcmgrtest') {
  $d = "$W\data_$t"; if (Test-Path $d) { Remove-Item $d -Recurse -Force }; New-Item -ItemType Directory $d | Out-Null
  $env:CALENDARWIDGET_DATA = $d
  Push-Location "$S\$t"; $b = dotnet build -c Release 2>&1 | Out-String; Pop-Location
  $errs = $b -split "`n" | ? { $_ -match ' error ' } | select -First 3
  if ($errs) { "$t BUILD ERRORS:"; $errs; continue }
  $exe = Get-ChildItem $S\$t\bin -Recurse -Filter "$t.exe" | select -First 1
  $o = & $exe.FullName 2>&1 | % { "$_" }
  "$t  PASS=" + ($o | ? { $_ -match '^PASS' }).Count + " FAIL=" + ($o | ? { $_ -match '^FAIL|CRASH' }).Count
  $o | ? { $_ -match '^FAIL|CRASH' } | select -First 5
}
Stop-Process -Id $ics.Id -Force -ErrorAction SilentlyContinue

# 節日 ICS 產生：需要放著各年份 JSON 的假伺服器
Get-NetTCPConnection -LocalPort 18767 -State Listen -ErrorAction SilentlyContinue | % { Stop-Process -Id $_.OwningProcess -Force }
$hf = Start-Process node -ArgumentList "`"$S\holfake.mjs`"", "`"$S\data`"" -PassThru -WindowStyle Hidden
Start-Sleep 1
$t = 'holtest'
$d = "$W\data_$t"; if (Test-Path $d) { Remove-Item $d -Recurse -Force }; New-Item -ItemType Directory $d | Out-Null
$env:CALENDARWIDGET_DATA = $d
Push-Location "$S\$t"; $b = dotnet build -c Release 2>&1 | Out-String; Pop-Location
$errs = $b -split "`n" | ? { $_ -match ' error ' } | select -First 3
if ($errs) { "$t BUILD ERRORS:"; $errs } else {
  $exe = Get-ChildItem "$S\$t\bin" -Recurse -Filter "$t.exe" | select -First 1
  $o = & $exe.FullName "$S\data" 2>&1 | % { "$_" }
  "$t  PASS=" + ($o | ? { $_ -match '^PASS' }).Count + " FAIL=" + ($o | ? { $_ -match '^FAIL|CRASH' }).Count
  $o | ? { $_ -match '^FAIL|CRASH' } | select -First 5
}
Stop-Process -Id $hf.Id -Force -ErrorAction SilentlyContinue

# 颱風假：需要假的停班停課資料伺服器
Get-NetTCPConnection -LocalPort 18768 -State Listen -ErrorAction SilentlyContinue | % { Stop-Process -Id $_.OwningProcess -Force }
$tf = Start-Process node -ArgumentList "`"$S\typhoonfake.mjs`"" -PassThru -WindowStyle Hidden
Start-Sleep 1
$t = 'typhoontest'
$d = "$W\data_$t"; if (Test-Path $d) { Remove-Item $d -Recurse -Force }; New-Item -ItemType Directory $d | Out-Null
$env:CALENDARWIDGET_DATA = $d
Push-Location "$S\$t"; $b = dotnet build -c Release 2>&1 | Out-String; Pop-Location
$errs = $b -split "`n" | ? { $_ -match ' error ' } | select -First 3
if ($errs) { "$t BUILD ERRORS:"; $errs } else {
  $exe = Get-ChildItem "$S\$t\bin" -Recurse -Filter "$t.exe" | select -First 1
  $o = & $exe.FullName "$S\data" 2>&1 | % { "$_" }
  "$t  PASS=" + ($o | ? { $_ -match '^PASS' }).Count + " FAIL=" + ($o | ? { $_ -match '^FAIL|CRASH' }).Count
  $o | ? { $_ -match '^FAIL|CRASH' } | select -First 5
}
Stop-Process -Id $tf.Id -Force -ErrorAction SilentlyContinue
$f = 'http://127.0.0.1:18766'
$env:CALENDARWIDGET_GOOGLE_AUTH = "$f/auth"; $env:CALENDARWIDGET_GOOGLE_TOKEN = "$f/token"; $env:CALENDARWIDGET_GOOGLE_REVOKE = "$f/revoke"; $env:CALENDARWIDGET_GOOGLE_CALENDAR = "$f/calendar/v3"; $env:CALENDARWIDGET_GOOGLE_TASKS = "$f/tasks/v1"
foreach ($t in 'googletest', 'mgrtest') {
  $log = "$W\google_$t.log"; Remove-Item $log -ErrorAction SilentlyContinue
  $g = Start-Process node -ArgumentList "`"$S\googlefake.mjs`"", "`"$log`"" -PassThru -WindowStyle Hidden
  Start-Sleep 2
  $env:CALENDARWIDGET_DATA = "$W\data_$t"
  Push-Location "$S\$t"; $b = dotnet build -c Release 2>&1 | Out-String; Pop-Location
  $errs = $b -split "`n" | ? { $_ -match ' error ' } | select -First 3
  if ($errs) { "$t BUILD ERRORS:"; $errs; Stop-Process -Id $g.Id -Force; continue }
  $exe = Get-ChildItem $S\$t\bin -Recurse -Filter "$t.exe" | select -First 1
  $o = & $exe.FullName $log 2>&1 | % { "$_" }
  "$t  PASS=" + ($o | ? { $_ -match '^PASS' }).Count + " FAIL=" + ($o | ? { $_ -match '^FAIL|CRASH' }).Count
  $o | ? { $_ -match '^FAIL|CRASH' } | select -First 5
  Stop-Process -Id $g.Id -Force -ErrorAction SilentlyContinue
}
Get-ChildItem Env:CALENDARWIDGET_* | % { Remove-Item "Env:$($_.Name)" }

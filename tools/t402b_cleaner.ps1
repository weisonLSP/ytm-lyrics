# TASK-402 Part D verification: cleaner_rules_path hot-load (acceptance 6)
# Flow:
#   1. app starts with default rules (cleaner_rules_path="")
#   2. push A: "Test Song" -> default rules clean -> preloaded cache hit
#   3. auto-edit saves cleaner_rules_path=test_rules.json (added suffix "TestSuffixX")
#      -> log: [TitleCleaner] LoadRules + [App] cleaner rules hot-loaded
#   4. push B: "Some Song TestSuffixX" -> NEW rules strip the suffix
#      -> log: [Engine] GetLyricsAsync: track='Some Song' (suffix gone proves new rules active)
$ErrorActionPreference = 'Stop'
$root = 'E:\Codex\OH-WorkSpace\ytm-lyrics'
$exe = Join-Path $root 'src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\YTM.Lyrics.exe'
$samples = Join-Path $root 'tools\samples'
$outDir = Join-Path $root 'tools\t402_shots'
$cacheDb = Join-Path $outDir 't402b_lyrics.db'
$logDir = Join-Path $outDir 'logs_b'
$settingsPath = Join-Path $outDir 'settings_b.json'
$rulesPath = Join-Path $outDir 'test_rules.json'

if (-not (Test-Path $exe)) { Write-Error "Desktop exe not built: $exe"; exit 1 }
if (-not (Test-Path $rulesPath)) { Write-Error "test rules not found: $rulesPath"; exit 1 }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Get-ChildItem $logDir -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path $cacheDb) { Remove-Item $cacheDb -Force }
if (Test-Path $settingsPath) { Remove-Item $settingsPath -Force }

$wsPort = 17902
$newPort = 17977  # port fixed by SettingsWindow.RunForensicAutoEdit
$appArgs = @(
  '--cache-db', $cacheDb,
  '--log-dir', $logDir,
  '--settings-path', $settingsPath,
  '--ws-port', "$wsPort",
  '--mock-engine', 'manual',
  '--mock-sample', (Join-Path $samples 't205_qrc.txt'),
  '--track-title', 'Test Song', '--track-artist', 'Test Artist', '--track-duration', '10',
  '--preload-cache', "qrc;$(Join-Path $samples 't205_qrc.txt')",
  '--open-settings',
  '--settings-shot', "$outDir\settings_tab1_service.png;1500",
  '--settings-auto-edit',
  '--settings-cleaner-path', $rulesPath,
  '--quit-after', '18000'
)
$joined = ($appArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
Write-Host '=== start app (cleaner hot-load verification) ==='
Write-Host $joined
$p = Start-Process -FilePath $exe -ArgumentList $joined -PassThru

# push A: default-rules track (preloaded cache hit, key matches --track-title)
Start-Sleep -Seconds 2
try {
  Push-Location $root
  node tools/t402_push.js --port=$wsPort --title="Test Song" --artist="Test Artist" --duration=10 2>&1 | ForEach-Object { Write-Host $_ }
  Pop-Location
} catch {
  Pop-Location
  Write-Host "[t402b] push A ended: $($_.Exception.Message)"
}

# wait for auto-edit save (t+6s) which hot-loads the custom rules
Start-Sleep -Seconds 7

# push B: track that ONLY the custom rules can clean ("TestSuffixX" suffix);
# connect the NEW port (auto-edit changed ws_port to 17977 -> listener restarted)
try {
  Push-Location $root
  node tools/t402_push.js --port=$newPort --title="Some Song TestSuffixX" --artist="New Artist" --duration=12 2>&1 | ForEach-Object { Write-Host $_ }
  Pop-Location
} catch {
  Pop-Location
  Write-Host "[t402b] push B ended: $($_.Exception.Message)"
}

if (-not $p.WaitForExit(15000)) {
  $p.Kill(); Write-Host 'app had to be killed'
} else {
  Write-Host "app exited cleanly, code=$($p.ExitCode)"
}

Write-Host ''
Write-Host '=== settings.json (cleaner_rules_path persisted) ==='
if (Test-Path $settingsPath) {
  Get-Content $settingsPath -Encoding UTF8 | Select-String 'cleaner_rules_path'
} else {
  Write-Host 'settings.json not found'
}

Write-Host ''
Write-Host '=== app.log: cleaner hot-load chain ==='
$log = Join-Path $logDir 'app.log'
if (Test-Path $log) {
  $content = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8)
  $content -split "`r?`n" | Where-Object {
    $_ -match 'GetLyricsAsync|LoadRules|hot-loaded|search start|result returned|cache hit|SettingsWindow.*saving|settings applied|StartLoad'
  } | ForEach-Object { Write-Host $_ }
} else {
  Write-Host 'log not found'
}

# TASK-402 settings forensic orchestration (sandbox: preloaded data + fake source route)
# Covers acceptance 1-6 + 即时生效分发表核心路径:
#   1. settings window opens with 3 tabs (shots)
#   2. auto-edit 3 items (ws_port / desktop_opacity / main_lyrics_font_size + source priority) & save
#      -> log: settings applied / ApplySettings opacity=0.5 / UpdateSettings font=40 / engine rebuilt
#   3. ws_port change -> RestartWebSocketListener -> probe listener up on new port
#   4. push probe B on new port -> machine rebuilt -> TrackChanged -> new engine search (priority log)
#   5. settings.json persisted with new values (re-open would show last saved values)
# Prereq: Desktop built; real engine + ForensicSourceSearcher fake source (sandbox has no network)
$ErrorActionPreference = 'Stop'
$root = 'E:\Codex\OH-WorkSpace\ytm-lyrics'
$exe = Join-Path $root 'src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\YTM.Lyrics.exe'
$samples = Join-Path $root 'tools\samples'
$outDir = Join-Path $root 'tools\t402_shots'
$cacheDb = Join-Path $outDir 't402_lyrics.db'
$logDir = Join-Path $outDir 'logs'
$settingsPath = Join-Path $outDir 'settings.json'

if (-not (Test-Path $exe)) { Write-Error "Desktop exe not built: $exe"; exit 1 }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Get-ChildItem $outDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $logDir -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path $cacheDb) { Remove-Item $cacheDb -Force }
if (Test-Path $settingsPath) { Remove-Item $settingsPath -Force }

$wsPort = 17901
$newPort = 17977
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
  '--settings-shot', "$outDir\settings_tab2_sources.png;3000",
  '--settings-shot', "$outDir\settings_tab3_desktop.png;4500",
  '--settings-auto-edit',
  '--quit-after', '20000'
)
$joined = ($appArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
Write-Host '=== start app ==='
Write-Host $joined
$p = Start-Process -FilePath $exe -ArgumentList $joined -PassThru

# probe A: connect old port, push track A (Test Song -> preloaded cache hit)
Start-Sleep -Seconds 2
try {
  Push-Location $root
  node tools/t402_push.js --port=$wsPort --title="Test Song" --artist="Test Artist" --duration=10 2>&1 | ForEach-Object { Write-Host $_ }
  Pop-Location
} catch {
  Pop-Location
  Write-Host "[t402] push A ended: $($_.Exception.Message) (ignore if app restarted listener)"
}

# wait for auto-edit save (t+6s from settings window loaded) + listener restart
Start-Sleep -Seconds 7

# probe B: connect NEW port (ws_port changed to 17977), push new track -> TrackChanged on rebuilt machine
try {
  Push-Location $root
  node tools/t402_push.js --port=$newPort --title="After Restart Track" --artist="New Artist" --duration=12 2>&1 | ForEach-Object { Write-Host $_ }
  Pop-Location
} catch {
  Pop-Location
  Write-Host "[t402] push B ended: $($_.Exception.Message)"
}

if (-not $p.WaitForExit(15000)) {
  $p.Kill(); Write-Host 'app had to be killed'
} else {
  Write-Host "app exited cleanly, code=$($p.ExitCode)"
}

Write-Host ''
Write-Host '=== settings screenshots ==='
Get-ChildItem $outDir -Filter 'settings_*.png' | Sort-Object Name | ForEach-Object { Write-Host "$($_.Name)  $($_.Length)B" }

Write-Host ''
Write-Host '=== settings.json (persisted) ==='
if (Test-Path $settingsPath) {
  Get-Content $settingsPath -Encoding UTF8
} else {
  Write-Host 'settings.json not found'
}

Write-Host ''
Write-Host '=== app.log: TASK-402 chain key lines ==='
$log = Join-Path $logDir 'app.log'
if (Test-Path $log) {
  $content = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8)
  $content -split "`r?`n" | Where-Object {
    $_ -match 'settings window opened|SettingsWindow|settings applied|RestartWebSocketListener|probe listener up|engine rebuilt|engine replaced|UpdateSettings|ApplySettings|playback replaced|StartLoad|search start|source not registered|result returned|cache hit|ApplyLyricsResult|hotkey|default_view'
  } | ForEach-Object { Write-Host $_ }
} else {
  Write-Host 'log not found'
}

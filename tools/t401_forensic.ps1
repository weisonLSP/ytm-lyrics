# TASK-401 F9 forensic orchestration (sandbox: preloaded data + fake source route)
# Covers acceptance 1-5:
#   1. negative cache None -> manual search panel appears (shot + log)
#   2. --f9-auto auto search -> candidate list (log SearchCandidatesAsync + shot)
#   3. auto select candidate & bind -> lyrics shown + "user bound" mark (log bind store cache + shot)
#   4. push track B then track A replay -> cache hit userBound=True, no auto search (log)
#   5. --f9-auto auto unbind -> re-trigger auto search (log unbind + search start)
# Prereq: Desktop built; real engine + ForensicSourceSearcher fake source (sandbox has no network)
$ErrorActionPreference = 'Stop'
$root = 'E:\Codex\OH-WorkSpace\ytm-lyrics'
$exe = Join-Path $root 'src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\YTM.Lyrics.exe'
$samples = Join-Path $root 'tools\samples'
$outDir = Join-Path $root 'tools\t401_shots'
$cacheDb = Join-Path $outDir 't401_lyrics.db'
$logDir = Join-Path $outDir 'logs'

if (-not (Test-Path $exe)) { Write-Error "Desktop exe not built: $exe"; exit 1 }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Get-ChildItem $outDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $logDir -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path $cacheDb) { Remove-Item $cacheDb -Force }

$wsPort = 17901
$appArgs = @(
  '--cache-db', $cacheDb,
  '--log-dir', $logDir,
  '--ws-port', "$wsPort",
  '--mock-engine', 'manual',
  '--mock-sample', (Join-Path $samples 't205_qrc.txt'),
  '--track-title', 'Test Song', '--track-artist', 'Test Artist', '--track-duration', '10',
  '--preload-negative',
  '--f9-auto', 'Test Song',
  '--shot', "$outDir\f9_panel_candidates.png;3500",
  '--shot', "$outDir\f9_bind_state.png;8000",
  '--shot', "$outDir\f9_replay_hit.png;12000",
  '--quit-after', '16500'
)
$joined = ($appArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
Write-Host '=== start app ==='
Write-Host $joined
$p = Start-Process -FilePath $exe -ArgumentList $joined -PassThru

Start-Sleep -Seconds 2
try {
  Push-Location $root
  node tools/t401_push.js --port=$wsPort 2>&1 | ForEach-Object { Write-Host $_ }
  Pop-Location
} catch {
  Pop-Location
  Write-Host "[t401] push ended: $($_.Exception.Message) (ignore if app quit first)"
}

if (-not $p.WaitForExit(20000)) {
  $p.Kill(); Write-Host 'app had to be killed'
} else {
  Write-Host "app exited cleanly, code=$($p.ExitCode)"
}

Write-Host '=== shots ==='
Get-ChildItem $outDir -Filter *.png | Sort-Object Name | ForEach-Object { Write-Host "$($_.Name)  $($_.Length)B" }

Write-Host '=== app.log: F9 chain key lines ==='
$log = Join-Path $logDir 'app.log'
if (Test-Path $log) {
  $content = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8)
  $content -split "`r?`n" | Where-Object { $_ -match 'f9-forensic|manual search|SearchCandidates|bind |unbind|cache hit|ApplyLyricsResult|BindLyrics|SearchCandidatesAsync|result returned|search start' } | ForEach-Object { Write-Host $_ }
} else {
  Write-Host 'log not found'
}

# TASK-FIX-SCROLL scroll forensic runner (self-use, not product code)
# Reproduce main-window lyric scroll failure: 20 lines / 40s long lyrics,
# check whether ScrollViewer follows the highlighted current line to viewport 40%.
# Prereq: Desktop built (baseline/fixed rounds overwrite the exe)
# Usage: powershell -File tools/t205_scroll.ps1 baseline
$Tag = if ($args.Count -gt 0) { $args[0] } else { 'scroll' }
$ErrorActionPreference = 'Stop'
$root = 'E:\Code\OH-WorkSpace\ytm-lyrics'
$exe = Join-Path $root 'src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\YTM.Lyrics.Desktop.exe'
$samples = Join-Path $root 'tools\samples'
$outDir = Join-Path $root ("tools\t205_scroll_" + $Tag)
$cacheDb = Join-Path $outDir 'scroll.db'
$log = Join-Path $outDir 'app.log'
$qrcPath = Join-Path $samples 't205_qrc_long.txt'

if (-not (Test-Path $exe)) { Write-Error "Desktop exe not built: $exe"; exit 1 }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $cacheDb) { Remove-Item $cacheDb -Force }
if (Test-Path $log) { Remove-Item $log -Force }

# Frame sequence: pos 0.5, 2.5, ..., 40.5 (21 frames, 1s apart, first frame ~2.1s after launch)
# Line times 0/2/4/.../38s -> shot target lines:
#   @3000  ~ pos2.5   line 2
#   @13000 ~ pos12.5  line 7
#   @23000 ~ pos22.5  line 12
#   @39000 ~ pos38.5  line 20 (last line, clamp-to-bottom check)
$appArgs = @(
  '--cache-db', $cacheDb,
  '--log-dir', $outDir,
  '--ws-port', '17899',
  '--track-title', 'Test Song',
  '--track-artist', 'Test Artist',
  '--track-duration', '40',
  '--preload-cache', ("qrc;" + $qrcPath),
  '--shot', ($outDir + '\scroll_3000ms.png;3000'),
  '--shot', ($outDir + '\scroll_13000ms.png;13000'),
  '--shot', ($outDir + '\scroll_23000ms.png;23000'),
  '--shot', ($outDir + '\scroll_39000ms.png;39000'),
  '--quit-after', '41000'
)
$joined = ($appArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
Write-Host ("=== run scroll_" + $Tag + " ===")
$p = Start-Process -FilePath $exe -ArgumentList $joined -PassThru

Start-Sleep -Seconds 2
try {
  Push-Location $root
  node tools/t205_scroll_push.js --port=17899 2>&1 | ForEach-Object { Write-Host $_ }
  Pop-Location
} catch {
  Pop-Location
  Write-Host ("[scroll] push ended: " + $_.Exception.Message + " (ignore if app quit first)")
}

if (-not $p.WaitForExit(43000)) {
  $p.Kill(); Write-Host "app had to be killed"
} else {
  Write-Host ("app exited cleanly, code=" + $p.ExitCode)
}
if (Test-Path $log) { Write-Host ('app.log at: ' + $log) }

Write-Host '=== shots ==='
Get-ChildItem $outDir -Filter *.png | Sort-Object Name | ForEach-Object { Write-Host ($_.Name + "  " + $_.Length + "B") }
Write-Host '=== app.log tail ==='
if (Test-Path ($outDir + '\scroll-app.log')) { Get-Content ($outDir + '\scroll-app.log') -Tail 12 }

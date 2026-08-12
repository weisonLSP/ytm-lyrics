# TASK-302 取证编排（沙箱预置数据路线，桌面歌词窗口渲染）
# 场景覆盖：
#   1. s1_dual_anim：双行模式 + 换行动画（默认 settings）：当前行高亮放大 + 下一行渐隐 +
#      行推进动画（抓动画中间帧 / 稳定态 / 末行无双行回单行）
#   2. s2_single：单行模式（--settings-path 预写 desktop_show_two_lines=false）：当前行居中无下一行
#   3. s3_no_lyrics：负缓存命中 → 暂无歌词占位（minimal）
#   4. s4_switch：切歌竞态（先 A 后 B）：gen 递增 + 旧加载取消 + 无词走 minimal
# 前置：Desktop 已构建。桌面窗口默认不 Show（main 视图）；首帧 TrackChanged → 加载完成 →
#   ShowLyricsPanel 首次自动 Show（IsLoaded=false）→ --desktop-shot timer（从窗口 Loaded 起算）
$ErrorActionPreference = 'Stop'
$root = 'E:\Code\OH-WorkSpace\ytm-lyrics'
$exe = Join-Path $root 'src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\YTM.Lyrics.Desktop.exe'
$samples = Join-Path $root 'tools\samples'
$outDir = Join-Path $root 'tools\t302_shots'
$cacheDb = Join-Path $outDir 't302_lyrics.db'
$logDir = Join-Path $outDir 'logs'

if (-not (Test-Path $exe)) { Write-Error "Desktop exe not built: $exe"; exit 1 }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Get-ChildItem $outDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path $cacheDb) { Remove-Item $cacheDb -Force }

# s2 单行模式 settings（desktop_show_two_lines=false，其余默认）
$settingsSingle = Join-Path $outDir 'settings_single.json'
'{"desktop_show_two_lines": false}' | Set-Content -Path $settingsSingle -Encoding UTF8

function Run-App {
  param(
    [string]$Tag,
    [string[]]$ExtraArgs,
    [string[]]$PushSpecs,     # 一组 @("--seq=a --count=7", ...)，顺序执行（切歌竞态用）
    [int]$QuitAfter,
    [int]$PushDelay = 2,
    [int]$WsPort = 17899
  )
  Write-Host "=== run $Tag ==="
  # 每轮清空 app.log（TextWriterTraceListener 追加模式，避免各场景日志混合）
  $log = Join-Path $logDir 'app.log'
  if (Test-Path $log) { Remove-Item $log -Force }
  $appArgs = @('--cache-db', $cacheDb, '--log-dir', $logDir, '--ws-port', "$WsPort")
  $appArgs += $ExtraArgs
  $appArgs += '--quit-after'; $appArgs += "$QuitAfter"
  $joined = ($appArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
  $p = Start-Process -FilePath $exe -ArgumentList $joined -PassThru

  Start-Sleep -Seconds $PushDelay
  try {
    Push-Location $root
    foreach ($spec in $PushSpecs) {
      $pushParts = $spec -split ' '
      node tools/t205_push.js @pushParts --port=$WsPort 2>&1 | ForEach-Object { Write-Host $_ }
    }
    Pop-Location
  } catch {
    Pop-Location
    Write-Host "[t302] push ended: $($_.Exception.Message) (ignore if app quit first)"
  }

  if (-not $p.WaitForExit($QuitAfter + 20000)) {
    $p.Kill(); Write-Host "app had to be killed"
  } else {
    Write-Host "app exited cleanly, code=$($p.ExitCode)"
  }
  if (Test-Path $log) { Copy-Item $log (Join-Path $outDir "$Tag-app.log") -Force }
}

# ---- 场景 1：双行 + 换行动画。行时间 0/2/4/6/8s；帧 pos 0.5/2.5/4.5/6.5/8.5/10.5/12（1s 间隔）。
# 窗口 Loaded ≈ 首帧(t≈2.2s)+加载(缓存命中≈50ms)。desktop-shot delay 相对 Loaded：
#   @300  稳定态 第1句（切歌首帧瞬切）
#   @1200 行推进 ~0.25s 后 → 动画中间帧（Y 偏移 + 半透明）
#   @1300 动画结束稳定态（第2句 + 第3句 双行）
#   @2200 动画中间帧（第3句推进）
#   @6200 动画中间帧（第5句推进，末行：无双行回单行居中）
#   @8500 末行后停留第5句
Run-App -Tag 's1_dual_anim' `
  -ExtraArgs @('--track-title','Test Song','--track-artist','Test Artist','--track-duration','10',
               '--preload-cache',"qrc;$(Join-Path $samples 't205_qrc.txt')",
               '--desktop-shot',"$outDir\s1_300ms.png;300",
               '--desktop-shot',"$outDir\s1_1200ms.png;1200",
               '--desktop-shot',"$outDir\s1_1300ms.png;1300",
               '--desktop-shot',"$outDir\s1_2200ms.png;2200",
               '--desktop-shot',"$outDir\s1_6200ms.png;6200",
               '--desktop-shot',"$outDir\s1_8500ms.png;8500") `
  -PushSpecs @('--seq=a --count=7') -QuitAfter 12500

# ---- 场景 2：单行模式（settings 覆盖 desktop_show_two_lines=false），动画仍开（单行也动画当前行）
Run-App -Tag 's2_single' `
  -ExtraArgs @('--settings-path',$settingsSingle,
               '--track-title','Test Song','--track-artist','Test Artist','--track-duration','10',
               '--preload-cache',"qrc;$(Join-Path $samples 't205_qrc.txt')",
               '--desktop-shot',"$outDir\s2_300ms.png;300",
               '--desktop-shot',"$outDir\s2_1500ms.png;1500") `
  -PushSpecs @('--seq=a --count=4') -QuitAfter 7500

# ---- 场景 3：暂无歌词（负缓存命中，minimal 占位）
Run-App -Tag 's3_no_lyrics' `
  -ExtraArgs @('--track-title','Instrumental Piece','--track-artist','No Artist','--track-duration','8',
               '--preload-negative',
               '--desktop-shot',"$outDir\s3_800ms.png;800") `
  -PushSpecs @('--seq=b --count=2') -QuitAfter 5500

# ---- 场景 4：切歌竞态。先 A（有词，缓存命中）后 B（负缓存无词）：
#   日志应见 StartLoad gen=1 (Test Song) → ApplyLyricsResult lines=5 → 窗口 Show
#   → StartLoad gen=2 (Instrumental) → 无词 → minimal 占位
Run-App -Tag 's4_switch' `
  -ExtraArgs @('--track-title','Test Song','--track-artist','Test Artist','--track-duration','10',
               '--preload-cache',"qrc;$(Join-Path $samples 't205_qrc.txt')",
               '--preload-negative') `
  -PushSpecs @('--seq=a --count=1','--seq=b --count=2') -QuitAfter 7000

Write-Host '=== shots ==='
Get-ChildItem $outDir -Filter *.png | Sort-Object Name | ForEach-Object { Write-Host "$($_.Name)  $($_.Length)B" }

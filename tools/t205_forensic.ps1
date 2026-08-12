# TASK-205 取证编排（沙箱预置数据路线）
# 场景覆盖：
#   1. 歌词显示 + 当前行跟随外推进度推进（真实引擎 + 直灌 QRC 缓存命中）
#   2. 翻译行显示（直灌带翻译 QRC）
#   3. 来源显示（底部来源条：来源/缓存/置信度）——并入场景 1 截图
#   4. 低置信提示（--mock-engine=low 注入）
#   5. 暂无歌词（--preload-negative 负缓存命中，PRD S3）
# 前置：已构建 Desktop（src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\YTM.Lyrics.Desktop.exe）
$ErrorActionPreference = 'Stop'
$root = 'E:\Code\OH-WorkSpace\ytm-lyrics'
$exe = Join-Path $root 'src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\YTM.Lyrics.Desktop.exe'
$samples = Join-Path $root 'tools\samples'
$outDir = Join-Path $root 'tools\t205_shots'
$cacheDb = Join-Path $outDir 't205_lyrics.db'
$log = Join-Path $root 'src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\app.log'

if (-not (Test-Path $exe)) { Write-Error "Desktop exe not built: $exe"; exit 1 }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Get-ChildItem $outDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $outDir -Filter *.log -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path $cacheDb) { Remove-Item $cacheDb -Force }
if (Test-Path $log) { Remove-Item $log -Force }

function Run-App {
  param(
    [string]$Tag,
    [string[]]$ExtraArgs,     # --shot 等取证参数（不含 --cache-db / --quit-after）
    [string]$PushArgs,        # t205_push.js 参数，如 "--seq=a --count=7"
    [int]$QuitAfter,          # app 自动退出毫秒
    [int]$PushDelay = 2,      # app 启动后延迟多少秒启动 push（帧 1s 间隔对齐截图）
    [int]$WsPort = 17899      # 取证隔离端口（本机用户浏览器探针占用默认 17865，隔离避免干扰）
  )
  Write-Host "=== run $Tag ==="
  # 每轮清空 app.log（TextWriterTraceListener 追加模式，避免各轮日志混合）
  if (Test-Path $log) { Remove-Item $log -Force }
  $appArgs = @()
  $appArgs += '--cache-db'; $appArgs += $cacheDb
  $appArgs += '--ws-port'; $appArgs += "$WsPort"
  $appArgs += $ExtraArgs
  $appArgs += '--quit-after'; $appArgs += "$QuitAfter"
  # Start-Process 数组拼接会把含空格元素拆成多参数 → 手动引号包裹（如 "Test Song"）
  $joined = ($appArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
  $p = Start-Process -FilePath $exe -ArgumentList $joined -PassThru

  Start-Sleep -Seconds $PushDelay
  try {
    Push-Location $root
    $pushParts = $PushArgs -split ' '
    node tools/t205_push.js @pushParts --port=$WsPort 2>&1 | ForEach-Object { Write-Host $_ }
    Pop-Location
  } catch {
    Pop-Location
    Write-Host "[t205] push ended: $($_.Exception.Message) (ignore if app quit first)"
  }

  if (-not $p.WaitForExit($QuitAfter + 20000)) {
    $p.Kill(); Write-Host "app had to be killed"
  } else {
    Write-Host "app exited cleanly, code=$($p.ExitCode)"
  }
  if (Test-Path $log) { Copy-Item $log "$outDir\$Tag-app.log" -Force }
}

# ---- 场景 1+3：歌词显示 + 当前行跟随推进 + 来源条（真实引擎缓存命中） ----
# 行时间 0/2/4/6/8s；push 帧 pos 0.5/2.5/4.5/6.5/8.5/10.5/12.0（帧 1s 间隔，首帧 ~2.1s 到达）
# shot@3000≈pos2.5 第2行 / @5000≈pos5.4 第3行 / @7000≈pos9.4 第5行 / @12000 末行后停留末行
Run-App -Tag 's1_advance' `
  -ExtraArgs @('--track-title','Test Song','--track-artist','Test Artist','--track-duration','10',
               '--preload-cache',"qrc;$(Join-Path $samples 't205_qrc.txt')",
               '--shot',"$outDir\s1_advance_3000ms.png;3000",
               '--shot',"$outDir\s1_advance_5000ms.png;5000",
               '--shot',"$outDir\s1_advance_7000ms.png;7000",
               '--shot',"$outDir\s1_advance_12000ms.png;12000") `
  -PushArgs '--seq=a --count=7' -QuitAfter 13500

# ---- 场景 2：翻译行显示（直灌带翻译 QRC，行 0/4/8s，翻译并入各前行） ----
# shot@5000≈pos5.4 第2行+翻译 / @9000≈pos9.4 第3行+翻译
Run-App -Tag 's2_translation' `
  -ExtraArgs @('--track-title','Test Song','--track-artist','Test Artist','--track-duration','10',
               '--preload-cache',"qrc;$(Join-Path $samples 't205_qrc_translated.txt')",
               '--shot',"$outDir\s2_translation_5000ms.png;5000",
               '--shot',"$outDir\s2_translation_9000ms.png;9000") `
  -PushArgs '--seq=a --count=4' -QuitAfter 10500

# ---- 场景 5：暂无歌词（负缓存命中，PRD S3） ----
Run-App -Tag 's5_no_lyrics' `
  -ExtraArgs @('--track-title','Instrumental Piece','--track-artist','No Artist','--track-duration','8',
               '--preload-negative',
               '--shot',"$outDir\s5_no_lyrics_3000ms.png;3000") `
  -PushArgs '--seq=b --count=3' -QuitAfter 6500

# ---- 场景 4：低置信提示（mock 引擎注入，沙箱内唯一拿不到 Low 的真实通道） ----
# mock 不查缓存；push 帧 pos 0.5/2.5/4.5 → shot@5000≈pos5.4 第3行
Run-App -Tag 's4_low_confidence' `
  -ExtraArgs @('--mock-engine','low','--mock-sample',(Join-Path $samples 't205_qrc.txt'),
               '--shot',"$outDir\s4_low_confidence_5000ms.png;5000") `
  -PushArgs '--seq=a --count=3' -QuitAfter 6500

Write-Host '=== shots ==='
Get-ChildItem $outDir -Filter *.png | Sort-Object Name | ForEach-Object { Write-Host "$($_.Name)  $($_.Length)B" }
Write-Host '=== app.log lines (last run) ==='
if (Test-Path "$outDir\s4_low_confidence-app.log") { Get-Content "$outDir\s4_low_confidence-app.log" -Tail 15 }

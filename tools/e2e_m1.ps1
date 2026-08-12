# TASK-104 M1 端到端取证（沙箱可执行）
# 流程：启动 Desktop app（--shot 密集截图 500ms 档）→ sleep 3s → node e2e_node.js --real 推帧
#       → e2e 退出后探针断线（ProviderLost）→ 截图离线态 → 优雅关闭 app（验证 OnExit Dispose）
$ErrorActionPreference = 'Continue'
$root = 'E:\Code\OH-WorkSpace\ytm-lyrics'
$exe = Join-Path $root 'src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\YTM.Lyrics.Desktop.exe'
$outDir = Join-Path $root 'tools\m1_shots'
$log = Join-Path $root 'src\YTM.Lyrics.Desktop\bin\Debug\net8.0-windows\app.log'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Get-ChildItem $outDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path $log) { Remove-Item $log -Force }

# 截图时间点（delay 相对窗口 Loaded）：500ms 档覆盖 播放中/暂停冻结/切歌B/切歌C/离线
$args = @()
for ($d = 1500; $d -le 15500; $d += 500) {
  $args += '--shot'; $args += "$outDir\m1_$($d)ms.png;$d"
}
# 自动退出（走 OnExit -> Dispose，验证退出路径）
$args += '--quit-after'; $args += '17500'

Write-Host '=== start Desktop app ==='
$p = Start-Process -FilePath $exe -ArgumentList $args -PassThru
Start-Sleep -Seconds 3

Write-Host '=== run e2e_node.js --real (推帧 9s) ==='
Push-Location $root
node tools/e2e_node.js --real 2>&1 | Select-Object -Last 3
Pop-Location

Write-Host '=== wait for auto-quit (OnExit -> Dispose) ==='
if (-not $p.WaitForExit(30000)) { $p.Kill(); Write-Host 'app had to be killed' } else { Write-Host "app exited cleanly, code=$($p.ExitCode)" }

$shots = Get-ChildItem $outDir -Filter *.png | Sort-Object Name
Write-Host "=== shots ($($shots.Count)) ==="
$shots | ForEach-Object { Write-Host "$($_.Name)  $($_.Length)B" }
Write-Host '=== app.log (tail) ==='
if (Test-Path $log) { Get-Content $log -Tail 20 }

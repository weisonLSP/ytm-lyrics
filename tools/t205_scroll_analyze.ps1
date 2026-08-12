# TASK-FIX-SCROLL pixel analyzer (self-use, not product code)
# Quantify where the highlighted (active) lyric line sits inside the lyrics viewport.
# Rule (UI_SPEC 2.1): active line center should sit at ~40% of viewport height.
# Active text color #F2F2F8 (near white), normal lyric text #C9C9D6 (gray).
# Usage: powershell -File tools/t205_scroll_analyze.ps1 <png> [<png> ...]
param([Parameter(Mandatory=$true)][string[]]$Paths)
Add-Type -AssemblyName System.Drawing

function Analyze($path) {
  $bmp = New-Object System.Drawing.Bitmap($path)
  try {
    $w = $bmp.Width; $h = $bmp.Height
    # per-row pixel counts
    $whiteRows = New-Object 'int[]' $h   # near-white (active line candidates)
    $grayRows  = New-Object 'int[]' $h   # gray lyric text (normal lines)
    for ($y = 0; $y -lt $h; $y++) {
      $wr = 0; $gr = 0
      $scanMaxX = $w - 80   # exclude right-edge scrollbar area (thumb/track near-white, x>=600)
      for ($x = 0; $x -lt $scanMaxX; $x++) {
        $c = $bmp.GetPixel($x, $y)
        $lum = 0.299*$c.R + 0.587*$c.G + 0.114*$c.B
        if ($c.R -gt 235 -and $c.G -gt 235 -and $c.B -gt 235) { $wr++ }
        elseif ($lum -gt 195 -and $lum -lt 232) { $gr++ }  # lyric gray #C9C9D6(~210), excludes artist #B4B4C2(~182)
      }
      $whiteRows[$y] = $wr; $grayRows[$y] = $gr
    }
    # lyric text region = rows with meaningful gray count (excludes title/artist above panel)
    $minY = -1; $maxY = -1
    for ($y = 0; $y -lt $h; $y++) { if ($grayRows[$y] -ge 20) { if ($minY -lt 0) { $minY = $y }; $maxY = $y } }
    if ($minY -lt 0) { Write-Host "$path : no lyric text found"; return }
    # active line rows = near-white runs inside the lyrics panel area only (y > panelTop), pick the vertical span
    $panelTop = [Math]::Max($minY - 200, 300)   # lyrics panel sits in lower window area; exclude title rows above
    $aMin = -1; $aMax = -1
    for ($y = $panelTop; $y -le $maxY; $y++) {
      if ($whiteRows[$y] -ge 10) { if ($aMin -lt 0) { $aMin = $y }; $aMax = $y }
    }
    if ($aMin -lt 0) { Write-Host "$path : no active line found"; return }
    $center = ($aMin + $aMax) / 2.0
    $span = $maxY - $minY
    $ratio = if ($span -gt 0) { ($center - $minY) / $span } else { -1 }
    Write-Host ("{0}  active_y=[{1}..{2}] center={3:F0}  viewport_y=[{4}..{5}]  ratio={6:P0}" -f `
      (Split-Path $path -Leaf), $aMin, $aMax, $center, $minY, $maxY, $ratio)
  } finally { $bmp.Dispose() }
}

foreach ($p in $Paths) { Analyze $p }

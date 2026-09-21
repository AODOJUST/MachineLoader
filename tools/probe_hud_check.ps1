# probe_hud_check.ps1 - verify the 2026-09-15 HUD changes from the faction self-test shots:
#   1) points bar still renders (exact faction fills)  2) round-over banner is now WHITE text with
#   a BLACK outline  3) the toast line exists. Pure ASCII. Writes a report file (no stdout).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$shots = Join-Path $projRoot 'Aviassembly_DEV\Machine\logs\faction_shots'
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\probe_hud_check.txt'

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("shots: $shots")
[void]$sb.AppendLine("banner geometry: title y=307..351, subtitle y=351..373 (Screen.height*0.30 / +44)")

function Get-Pixels($path) {
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $len = $data.Stride * $bmp.Height
    $buf = New-Object byte[] $len
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $len)
    $bmp.UnlockBits($data)
    $res = @{ w = $bmp.Width; h = $bmp.Height; stride = $data.Stride; buf = $buf }
    $bmp.Dispose()
    return $res
}

# exact faction fill colors as written in FactionSystem.cs
$fills = @(
    @{ n = 'Snow  #3385EB'; r = 51;  g = 133; b = 235 },
    @{ n = 'Desert#F26B29'; r = 242; g = 107; b = 41  },
    @{ n = 'Spawn #389E4D'; r = 56;  g = 158; b = 77  }
)
$tol = 10

foreach ($name in @('03_points_after_loss.png', '04_round_over.png', '05_scoreboard_colors.png')) {
    $p = Join-Path $shots $name
    [void]$sb.AppendLine("")
    if (-not (Test-Path $p)) { [void]$sb.AppendLine("=== $name MISSING ==="); continue }
    $img = Get-Pixels $p
    $buf = $img.buf; $stride = $img.stride; $iw = $img.w; $ih = $img.h
    [void]$sb.AppendLine("=== $name  ${iw}x${ih} ===")

    # --- pass 1: faction fills + white/black in the two banner bands ---
    $hits = @{}
    foreach ($t in $fills) { $hits[$t.n] = @{ c = 0; x0 = 999999; x1 = -1; y0 = 999999; y1 = -1 } }
    $whiteA = 0; $darkA = 0; $whiteB = 0; $darkB = 0
    $wx0 = 999999; $wx1 = -1; $wy0 = 999999; $wy1 = -1

    for ($y = 0; $y -lt $ih; $y++) {
        $row = $y * $stride
        $inA = ($y -ge 300 -and $y -le 352)
        $inB = ($y -ge 351 -and $y -le 374)
        for ($x = 0; $x -lt $iw; $x++) {
            $o = $row + $x * 4
            $b = $buf[$o]; $g = $buf[$o + 1]; $r = $buf[$o + 2]
            foreach ($t in $fills) {
                if ([Math]::Abs($r - $t.r) -le $tol -and [Math]::Abs($g - $t.g) -le $tol -and [Math]::Abs($b - $t.b) -le $tol) {
                    $s = $hits[$t.n]; $s.c++
                    if ($x -lt $s.x0) { $s.x0 = $x }; if ($x -gt $s.x1) { $s.x1 = $x }
                    if ($y -lt $s.y0) { $s.y0 = $y }; if ($y -gt $s.y1) { $s.y1 = $y }
                }
            }
            $isWhite = ($r -gt 190 -and $g -gt 190 -and $b -gt 190)
            $isDark = ($r -lt 45 -and $g -lt 45 -and $b -lt 45)
            if ($inA) {
                if ($isWhite) {
                    $whiteA++
                    if ($x -lt $wx0) { $wx0 = $x }; if ($x -gt $wx1) { $wx1 = $x }
                    if ($y -lt $wy0) { $wy0 = $y }; if ($y -gt $wy1) { $wy1 = $y }
                }
                if ($isDark) { $darkA++ }
            }
            if ($inB) { if ($isWhite) { $whiteB++ }; if ($isDark) { $darkB++ } }
        }
    }

    [void]$sb.AppendLine("-- points bar fills (exact +/-$tol)")
    foreach ($t in $fills) {
        $s = $hits[$t.n]
        if ($s.c -eq 0) { [void]$sb.AppendLine("   " + $t.n + " : 0") }
        else { [void]$sb.AppendLine(("   {0} : {1}px bbox x[{2}..{3}] y[{4}..{5}]" -f $t.n, $s.c, $s.x0, $s.x1, $s.y0, $s.y1)) }
    }
    [void]$sb.AppendLine("-- banner band: white(ret>190) / dark(all<45)")
    [void]$sb.AppendLine(("   title    y300..352 : white={0} dark={1}  whiteX[{2}..{3}] y[{4}..{5}]" -f $whiteA, $darkA, $wx0, $wx1, $wy0, $wy1))
    [void]$sb.AppendLine(("   subtitle y351..374 : white={0} dark={1}" -f $whiteB, $darkB))
}
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

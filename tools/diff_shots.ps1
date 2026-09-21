# diff_shots.ps1 - pixel-diff two self-test screenshots and print an ASCII occupancy map of
# where they differ. Used to prove the Alt scoreboard window actually renders.
# Pure ASCII only. Writes a report file (no stdout).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$shots = Join-Path $projRoot 'Aviassembly_DEV\Machine\logs\faction_shots'
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\diff_shots.txt'

$a = Join-Path $shots '03_points_after_loss.png'
$b = Join-Path $shots '05_scoreboard_colors.png'

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("A = 03_points_after_loss.png  (scoreboard closed)")
[void]$sb.AppendLine("B = 05_scoreboard_colors.png  (scoreboard open)")
[void]$sb.AppendLine("")

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

$ia = Get-Pixels $a
$ib = Get-Pixels $b
$w = [Math]::Min($ia.w, $ib.w); $h = [Math]::Min($ia.h, $ib.h)
$sa = $ia.stride; $sb2 = $ib.stride
$ba = $ia.buf; $bb = $ib.buf

$gridW = 64; $gridH = 32
$cellW = [Math]::Ceiling($w / $gridW); $cellH = [Math]::Ceiling($h / $gridH)
$cells = New-Object 'int[,]' $gridW, $gridH
$x0 = 999999; $x1 = -1; $y0 = 999999; $y1 = -1; $n = 0

for ($y = 0; $y -lt $h; $y++) {
    $ra = $y * $sa; $rb = $y * $sb2
    $gy = [int]($y / $cellH); if ($gy -ge $gridH) { $gy = $gridH - 1 }
    for ($px = 0; $px -lt $w; $px++) {
        $oa = $ra + $px * 4; $ob = $rb + $px * 4
        $d = [Math]::Abs($ba[$oa] - $bb[$ob]) + [Math]::Abs($ba[$oa + 1] - $bb[$ob + 1]) + [Math]::Abs($ba[$oa + 2] - $bb[$ob + 2])
        if ($d -gt 30) {
            $n++
            $gx = [int]($px / $cellW); if ($gx -ge $gridW) { $gx = $gridW - 1 }
            $cells[$gx, $gy] = $cells[$gx, $gy] + 1
            if ($px -lt $x0) { $x0 = $px }; if ($px -gt $x1) { $x1 = $px }
            if ($y -lt $y0) { $y0 = $y }; if ($y -gt $y1) { $y1 = $y }
        }
    }
}

[void]$sb.AppendLine("differing pixels (>30 sum-abs): $n")
[void]$sb.AppendLine("bbox x[$x0..$x1] y[$y0..$y1]   (image ${w}x${h})")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("occupancy map: 1 cell = ${cellW}x${cellH}px, chars = . : + # @ by density")
[void]$sb.AppendLine("     " + ("0123456789" * 7).Substring(0, $gridW))
for ($gy = 0; $gy -lt $gridH; $gy++) {
    $line = ""
    for ($gx = 0; $gx -lt $gridW; $gx++) {
        $c = $cells[$gx, $gy]
        $tot = $cellW * $cellH
        $pct = 100.0 * $c / $tot
        if ($pct -lt 3) { $ch = "." }
        elseif ($pct -lt 15) { $ch = ":" }
        elseif ($pct -lt 40) { $ch = "+" }
        elseif ($pct -lt 75) { $ch = "#" }
        else { $ch = "@" }
        $line += $ch
    }
    [void]$sb.AppendLine(("{0,3}  {1}" -f ($gy * $cellH), $line))
}

[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

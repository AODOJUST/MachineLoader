# Round-over banner check: bold WHITE glyphs with a BLACK outline.
# Method: in the banner band, classify each pixel W (all channels >190) / K (all <45),
# then run-length the row with the most W pixels. Outlined text = many W<->K transitions.
# Output -> bin\probe_banner.txt
param([string]$Shot = "05_round_over.png")
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$shots = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV\Machine\logs\faction_shots"
$out = Join-Path $root "bin\probe_banner_$([System.IO.Path]::GetFileNameWithoutExtension($Shot)).txt"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

foreach ($shot in @($Shot)) {
    $file = Join-Path $shots $shot
    if (-not (Test-Path $file)) { Say ("MISSING " + $file); continue }
    $bmp = New-Object System.Drawing.Bitmap($file)
    $W = $bmp.Width; $H = $bmp.Height
    $y0 = [int]($H * 0.30) - 4
    $y1 = $y0 + 74
    Say ""
    Say ("== " + $shot + "  " + $W + "x" + $H + "  band y[" + $y0 + ".." + $y1 + "]")

    $bestRow = -1; $bestW = -1; $totW = 0; $totK = 0
    for ($y = $y0; $y -le $y1 -and $y -lt $H; $y++) {
        $cw = 0; $ck = 0
        for ($x = 0; $x -lt $W; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.R -gt 190 -and $c.G -gt 190 -and $c.B -gt 190) { $cw++ }
            elseif ($c.R -lt 45 -and $c.G -lt 45 -and $c.B -lt 45) { $ck++ }
        }
        $totW += $cw; $totK += $ck
        if ($cw -gt $bestW) { $bestW = $cw; $bestRow = $y }
    }
    Say ("white(>190) total=" + $totW + "   black(<45) total=" + $totK)
    Say ("busiest row y=" + $bestRow + " white=" + $bestW)

    if ($bestRow -ge 0 -and $bestW -gt 0) {
        # run-length encode that row as W / K / . (other)
        $prev = "."; $run = 0; $trans = 0; $segs = @()
        for ($x = 0; $x -lt $W; $x++) {
            $c = $bmp.GetPixel($x, $bestRow)
            $cls = "."
            if ($c.R -gt 190 -and $c.G -gt 190 -and $c.B -gt 190) { $cls = "W" }
            elseif ($c.R -lt 45 -and $c.G -lt 45 -and $c.B -lt 45) { $cls = "K" }
            if ($cls -ne $prev) {
                if ($prev -ne "." -and $run -ge 2) { $segs += ($prev + $run); $trans++ }
                $prev = $cls; $run = 1
            } else { $run++ }
        }
        if ($prev -ne "." -and $run -ge 2) { $segs += ($prev + $run); $trans++ }
        Say ("W/K transitions on that row = " + $trans)
        $line = ""
        $n = 0
        foreach ($s in $segs) { if ($n -lt 40) { $line += $s + " " }; $n++ }
        Say ("pattern: " + $line)
    }
    $bmp.Dispose()
}
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

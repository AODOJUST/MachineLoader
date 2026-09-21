# Look for colors ONLY the new scoreboard layout produces:
#   separators  = GUI.color (0.70,0.72,0.77) -> 179,184,196  drawn as 428x1 horizontal lines
#   mode buttons= (0.85,0.86,0.89)           -> 217,219,227  opaque filled rects
# The mod renders straight 8-bit color (verified earlier: 0.46*255=117.3 -> 0x75 exact).
# Output -> bin\probe_scoreboard.txt
param(
    [string]$Shot = "03_scoreboard_territory.png",
    [int]$Tol = 3
)
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$shots = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV\Machine\logs\faction_shots"
$out = Join-Path $root "bin\probe_scoreboard.txt"
$file = Join-Path $shots $Shot

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

if (-not (Test-Path $file)) { Say ("MISSING " + $file); [System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))); exit }
$bmp = New-Object System.Drawing.Bitmap($file)
$W = $bmp.Width; $H = $bmp.Height
Say ("file=" + $Shot + "  " + $W + "x" + $H)

# ---- collect runs for one target color ----
function ScanColor($name, $tr, $tg, $tb, $minRun) {
    Say ""
    Say ("== " + $name + "  rgb=(" + $tr + "," + $tg + "," + $tb + ") tol=" + $Tol)
    $total = 0
    $runs = @()
    $minX = 999999; $maxX = -1; $minY = 999999; $maxY = -1
    for ($y = 0; $y -lt $H; $y++) {
        $run = 0; $start = -1
        for ($x = 0; $x -lt $W; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $hit = ([Math]::Abs($c.R - $tr) -le $Tol -and [Math]::Abs($c.G - $tg) -le $Tol -and [Math]::Abs($c.B - $tb) -le $Tol)
            if ($hit) {
                $total++
                if ($run -eq 0) { $start = $x }
                $run++
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            } else {
                if ($run -ge $minRun) { $runs += ("y=" + $y + " x=" + $start + ".." + ($x - 1) + " len=" + $run) }
                $run = 0
            }
        }
        if ($run -ge $minRun) { $runs += ("y=" + $y + " x=" + $start + ".." + ($W - 1) + " len=" + $run) }
    }
    Say ("total=" + $total)
    if ($total -gt 0) { Say ("bbox x[" + $minX + ".." + $maxX + "] y[" + $minY + ".." + $maxY + "]") }
    Say ("runs(>=" + $minRun + "px)=" + $runs.Count)
    $n = 0
    foreach ($r in $runs) { if ($n -lt 12) { Say ("  " + $r) }; $n++ }
    return @{ total = $total; runs = $runs.Count; minX = $minX; maxX = $maxX; minY = $minY; maxY = $maxY }
}

$sep = ScanColor "separator lines" 179 184 196 200
$btn = ScanColor "mode button fill" 217 219 227 40
$grn = ScanColor "spawn green" 56 158 77 20
$org = ScanColor "desert orange" 242 107 41 20
$blu = ScanColor "snow blue" 51 133 235 20

$bmp.Dispose()
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

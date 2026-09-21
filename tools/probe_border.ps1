# probe_border.ps1 - print the exact pixels of the points-bar frame lines in the self-test shot,
# to confirm the 1px gray border renders (fill already proven at 308x9px).
# Pure ASCII only. Writes a report file (no stdout).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$shot = Join-Path $projRoot 'Aviassembly_DEV\Machine\logs\faction_shots\03_points_after_loss.png'
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\probe_border.txt'

$sb = New-Object System.Text.StringBuilder
$bmp = [System.Drawing.Bitmap]::FromFile($shot)
[void]$sb.AppendLine("shot: $shot  " + $bmp.Width + "x" + $bmp.Height)
[void]$sb.AppendLine("code: bx=662 bw=310 bh=11  rowY = 862 / 881 / 900  -> border rows at ry and ry+10")
[void]$sb.AppendLine("")

function HexOf($bmp, $x, $y) {
    if ($x -lt 0 -or $y -lt 0 -or $x -ge $bmp.Width -or $y -ge $bmp.Height) { return "----" }
    $c = $bmp.GetPixel($x, $y)
    return ("{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B)
}

foreach ($by in @(862, 881, 900)) {
    [void]$sb.AppendLine("=== bar row  top-y=$by  (bar spans y=$by..$($by+10), x=662..971) ===")
    $top = @()
    for ($px = 662; $px -le 971; $px += 22) { $top += (HexOf $bmp $px $by) }
    [void]$sb.AppendLine("  y=$by        top border : " + ($top -join " "))
    $bot = @()
    for ($px = 662; $px -le 971; $px += 22) { $bot += (HexOf $bmp $px ($by + 10)) }
    [void]$sb.AppendLine("  y=$($by+10)   bottom brdr: " + ($bot -join " "))
    $mid = @()
    for ($px = 662; $px -le 971; $px += 22) { $mid += (HexOf $bmp $px ($by + 5)) }
    [void]$sb.AppendLine("  y=$($by+5)   fill mid   : " + ($mid -join " "))
    $lf = @()
    for ($py = $by; $py -le $by + 10; $py++) { $lf += (HexOf $bmp 662 $py) }
    [void]$sb.AppendLine("  x=662 left   border    : " + ($lf -join " "))
    $rt = @()
    for ($py = $by; $py -le $by + 10; $py++) { $rt += (HexOf $bmp 971 $py) }
    [void]$sb.AppendLine("  x=971 right  border    : " + ($rt -join " "))
    [void]$sb.AppendLine("  outside (just above bar): " + ((HexOf $bmp 700 ($by - 1)) + " " + (HexOf $bmp 900 ($by - 1)) + " " + (HexOf $bmp 700 ($by - 3))))
    [void]$sb.AppendLine("")
}
$bmp.Dispose()
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

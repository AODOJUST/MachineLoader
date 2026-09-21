# verify_hud_changes.ps1 - decisive checks for the 2026-09-15 FactionSystem HUD changes:
#   A) points bar: shot 03 should show Desert mid-FLASH (fill lerped toward white), not the raw color
#   B) round-over banner: white glyphs + black outline -> many dark<->white transitions along the
#      glyph rows, and a printable run-length "picture" of those rows.
# Pure ASCII. Writes a report file (no stdout).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$shots = Join-Path $projRoot 'Aviassembly_DEV\Machine\logs\faction_shots'
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\verify_hud_changes.txt'

$sb = New-Object System.Text.StringBuilder

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

# ---------- A) flash check on 03_points_after_loss.png ----------
$p03 = Join-Path $shots '03_points_after_loss.png'
$img = Get-Pixels $p03
$buf = $img.buf; $stride = $img.stride; $iw = $img.w; $ih = $img.h
[void]$sb.AppendLine("=== A) flash highlight on 03_points_after_loss.png ===")
[void]$sb.AppendLine("code: flash = 1 - dt/2.5s ; fill = lerp(fc, white, flash*0.45) ; border = lerp(gray, white, flash)")
[void]$sb.AppendLine("expected at dt=1.74s -> flash=0.30 -> Desert fill ~ F47F46, border ~ 9FA2AA")

$probes = @(
    @{ n = 'Desert raw   F26B29'; r = 242; g = 107; b = 41;  tol = 10 },
    @{ n = 'Desert flash F47F46'; r = 244; g = 127; b = 70;  tol = 14 },
    @{ n = 'border flash 9FA2AA'; r = 159; g = 162; b = 170; tol = 12 }
)
foreach ($t in $probes) {
    $c = 0; $x0 = 999999; $x1 = -1; $y0 = 999999; $y1 = -1
    for ($y = 0; $y -lt $ih; $y++) {
        $row = $y * $stride
        for ($x = 0; $x -lt $iw; $x++) {
            $o = $row + $x * 4
            if ([Math]::Abs($buf[$o + 2] - $t.r) -le $t.tol -and [Math]::Abs($buf[$o + 1] - $t.g) -le $t.tol -and [Math]::Abs($buf[$o] - $t.b) -le $t.tol) {
                $c++
                if ($x -lt $x0) { $x0 = $x }; if ($x -gt $x1) { $x1 = $x }
                if ($y -lt $y0) { $y0 = $y }; if ($y -gt $y1) { $y1 = $y }
            }
        }
    }
    if ($c -eq 0) { [void]$sb.AppendLine(("   {0} : 0" -f $t.n)) }
    else { [void]$sb.AppendLine(("   {0} : {1}px bbox x[{2}..{3}] y[{4}..{5}]" -f $t.n, $c, $x0, $x1, $y0, $y1)) }
}

# ---------- B) banner glyph rows: dark<->white transitions + run-length picture ----------
[void]$sb.AppendLine("")
[void]$sb.AppendLine("=== B) round-over banner rows (x 560..1040) ===")
[void]$sb.AppendLine("legend: W=white(all>190)  K=dark(all<45)  .=other ; runs printed as <char><count>")
foreach ($n in @('03_points_after_loss.png', '04_round_over.png', '05_scoreboard_colors.png')) {
    $pp = Join-Path $shots $n
    $im2 = Get-Pixels $pp
    $b2 = $im2.buf; $s2 = $im2.stride
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("-- $n")
    foreach ($yy in @(318, 325, 332)) {
        $cls = New-Object System.Text.StringBuilder
        for ($x = 560; $x -le 1040; $x++) {
            $o = $yy * $s2 + $x * 4
            $r = $b2[$o + 2]; $g = $b2[$o + 1]; $bb = $b2[$o]
            if ($r -gt 190 -and $g -gt 190 -and $bb -gt 190) { [void]$cls.Append('W') }
            elseif ($r -lt 45 -and $g -lt 45 -and $bb -lt 45) { [void]$cls.Append('K') }
            else { [void]$cls.Append('.') }
        }
        $str = $cls.ToString()
        # transitions between the three classes
        $tr = 0
        for ($i = 1; $i -lt $str.Length; $i++) { if ($str[$i] -ne $str[$i - 1]) { $tr++ } }
        # run-length, only runs >= 2 printed to keep it readable
        $runs = New-Object System.Text.StringBuilder
        $i2 = 0; $shown = 0
        while ($i2 -lt $str.Length -and $shown -lt 34) {
            $ch = $str[$i2]; $j = $i2
            while ($j -lt $str.Length -and $str[$j] -eq $ch) { $j++ }
            $len = $j - $i2
            if ($ch -ne '.') { [void]$runs.Append($ch).Append($len).Append(' ') ; $shown++ }
            $i2 = $j
        }
        [void]$sb.AppendLine(("   y={0}  transitions={1}  runs: {2}" -f $yy, $tr, $runs.ToString().Trim()))
    }
}
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

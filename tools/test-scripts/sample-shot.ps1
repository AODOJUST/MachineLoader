# Sample pixels from the GVision test screenshots to quantify the vignette.
# ASCII-only. Results -> sample_log.txt
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$dir = "C:\Users\16857\AppData\Local\Temp\gvision_shots"
$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

Add-Type -AssemblyName System.Drawing

$files = @(
    @{ Tag = "G09"; File = "gvision_G09_a_115552.png" },
    @{ Tag = "G11"; File = "gvision_G11_a_115555.png" },
    @{ Tag = "G13"; File = "gvision_G13_a_115557.png" },
    @{ Tag = "G15"; File = "gvision_G15_a_115600.png" },
    @{ Tag = "G17"; File = "gvision_G17_a_115602.png" },
    @{ Tag = "G19"; File = "gvision_G19_a_115605.png" },
    @{ Tag = "G21"; File = "gvision_G21_a_115607.png" },
    @{ Tag = "G23"; File = "gvision_G23_a_115610.png" },
    @{ Tag = "G25"; File = "gvision_G25_a_115612.png" }
)

Say "tag  center  topmid  leftmid  corner  midr  (0-255 luminance avg)"

foreach ($f in $files) {
    $p = Join-Path $dir $f.File
    if (-not (Test-Path $p)) { Say ($f.Tag + " MISSING"); continue }
    $bmp = [System.Drawing.Bitmap]::FromFile($p)
    $w = $bmp.Width; $h = $bmp.Height
    $cx = [int]($w / 2); $cy = [int]($h / 2)
    $pts = @(
        @($cx, $cy),
        @($cx, [int]($h * 0.05)),
        @([int]($w * 0.03), $cy),
        @([int]($w * 0.03), [int]($h * 0.05)),
        @([int]($w * 0.25), [int]($h * 0.25))
    )
    $vals = @()
    foreach ($pt in $pts) {
        $c = $bmp.GetPixel($pt[0], $pt[1])
        $vals += [int](($c.R * 0.299) + ($c.G * 0.587) + ($c.B * 0.114))
    }
    Say ($f.Tag + "  " + $vals[0] + "  " + $vals[1] + "  " + $vals[2] + "  " + $vals[3] + "  " + $vals[4] + "   (" + $w + "x" + $h + ")")
    $bmp.Dispose()
}

[System.IO.File]::WriteAllText((Join-Path $root "sample_log.txt"), $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

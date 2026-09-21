# Grab the current screen so we can verify the game still renders after camera changes.
# ASCII-only (PowerShell 5.1 mis-decodes non-ASCII .ps1 without BOM).
$ErrorActionPreference = "Continue"
$outPath = "D:\豆包的下载\Machine_Dev\opti_shot.png"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, (New-Object System.Drawing.Point(0, 0)), $bounds.Size)
    $g.Dispose()
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)

    # quick brightness sample: is the picture mostly black?
    $step = 40
    $sum = 0
    $n = 0
    for ($y = 0; $y -lt $bmp.Height; $y += $step) {
        for ($x = 0; $x -lt $bmp.Width; $x += $step) {
            $p = $bmp.GetPixel($x, $y)
            $sum += ($p.R + $p.G + $p.B) / 3
            $n++
        }
    }
    $bmp.Dispose()
    $fi = Get-Item $outPath
    Say ("saved " + $outPath + " " + $fi.Length + " bytes")
    Say ("avgBrightness=" + [math]::Round($sum / $n, 1) + " samples=" + $n)
} catch {
    Say ("capture failed: " + $_.Exception.Message)
}

[System.IO.File]::WriteAllText("D:\豆包的下载\Machine_Dev\opti_shot_log.txt", $sb.ToString())

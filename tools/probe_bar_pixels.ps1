# probe_bar_pixels.ps1 - find the FactionSystem points-bar colors in the self-test screenshots.
# Whole-frame color search (LockBits, no geometry assumptions). Pure ASCII. Writes a report file.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # D:\...\<root>
$shots = Join-Path $projRoot 'Aviassembly_DEV\Machine\logs\faction_shots'
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\probe_bar_pixels.txt'

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("shots dir: $shots")
if (-not (Test-Path $shots)) {
    [void]$sb.AppendLine("MISSING DIR")
    [System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    exit
}

# faction bar fill colors, exactly as written in FactionSystem.cs
$targets = @(
    @{ n = 'Snow    fill #3385EB'; r = 51;  g = 133; b = 235 },
    @{ n = 'Desert  fill #F26B29'; r = 242; g = 107; b = 41  },
    @{ n = 'Spawn   fill #389E4D'; r = 56;  g = 158; b = 77  },
    @{ n = 'gray border #757A85';  r = 117; g = 122; b = 133 }
)
$tol = 10

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

foreach ($f in (Get-ChildItem $shots -Filter *.png | Sort-Object Name)) {
    $img = Get-Pixels $f.FullName
    $buf = $img.buf; $stride = $img.stride; $iw = $img.w; $ih = $img.h
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("=== " + $f.Name + "  " + $iw + "x" + $ih + " ===")

    $stats = @{}
    foreach ($t in $targets) {
        $stats[$t.n] = @{ count = 0; x0 = 999999; x1 = -1; y0 = 999999; y1 = -1 }
    }
    $hist = @{}

    for ($y = 0; $y -lt $ih; $y++) {
        $row = $y * $stride
        for ($px = 0; $px -lt $iw; $px++) {
            $o = $row + $px * 4
            $b = $buf[$o]; $g = $buf[$o + 1]; $r = $buf[$o + 2]
            $key = ($r / 8) * 1024 + ($g / 8) * 32 + ($b / 8)
            if ($hist.ContainsKey($key)) { $hist[$key] = $hist[$key] + 1 } else { $hist[$key] = 1 }
            foreach ($t in $targets) {
                if ([Math]::Abs($r - $t.r) -le $tol -and [Math]::Abs($g - $t.g) -le $tol -and [Math]::Abs($b - $t.b) -le $tol) {
                    $s = $stats[$t.n]
                    $s.count = $s.count + 1
                    if ($px -lt $s.x0) { $s.x0 = $px }
                    if ($px -gt $s.x1) { $s.x1 = $px }
                    if ($y -lt $s.y0) { $s.y0 = $y }
                    if ($y -gt $s.y1) { $s.y1 = $y }
                }
            }
        }
    }

    [void]$sb.AppendLine("-- target color hits (tol +/-$tol)")
    foreach ($t in $targets) {
        $s = $stats[$t.n]
        if ($s.count -eq 0) { [void]$sb.AppendLine("   " + $t.n + " : 0") }
        else { [void]$sb.AppendLine(("   {0} : {1}px  bbox x[{2}..{3}] y[{4}..{5}]" -f $t.n, $s.count, $s.x0, $s.x1, $s.y0, $s.y1)) }
    }

    [void]$sb.AppendLine("-- top 12 quantized colors (r/8,g/8,b/8)")
    $top = $hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 12
    foreach ($e in $top) {
        $k = [int]$e.Key
        $rr = [int]($k / 1024) * 8; $k2 = $k % 1024
        $gg = [int]($k2 / 32) * 8; $bb = ($k2 % 32) * 8
        [void]$sb.AppendLine(("   #{0:X2}{1:X2}{2:X2}  {3}px" -f $rr, $gg, $bb, $e.Value))
    }
}

[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

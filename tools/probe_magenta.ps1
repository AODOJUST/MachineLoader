# probe_magenta.ps1 - scan AAM self-test screenshots for the Unity "missing material" magenta.
# Whole-frame scan via LockBits. MUST stay pure ASCII (no non-ASCII comments: PS 5.1 reads a
# BOM-less file as ANSI and a multi-byte char at end of line can swallow the next line).
$out = [System.IO.Path]::Combine($PSScriptRoot, '..\bin\probe_magenta.txt')
$sb = New-Object System.Text.StringBuilder
try {
    Add-Type -AssemblyName System.Drawing
    $tools = $PSScriptRoot
    $dev = [System.IO.Path]::GetDirectoryName($tools)
    $root = [System.IO.Path]::GetDirectoryName($dev)
    $shots = [System.IO.Path]::Combine($root, 'Aviassembly_DEV\mods\MachineAAM')
    [void]$sb.AppendLine("psroot : $tools")
    [void]$sb.AppendLine("shots  : $shots  exists=" + (Test-Path -LiteralPath $shots))

    $tol = 60
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

    foreach ($f in (Get-ChildItem -LiteralPath $shots -Filter *.png | Sort-Object Name)) {
        $img = Get-Pixels $f.FullName
        $b = $img.buf; $stride = $img.stride
        $mg = 0; $pu = 0
        $minx = 999999; $maxx = -1; $miny = 999999; $maxy = -1
        $hist = @{}
        for ($y = 0; $y -lt $img.h; $y += 2) {
            $row = $y * $stride
            for ($x = 0; $x -lt $img.w; $x += 2) {
                $i = $row + $x * 4
                $bl = $b[$i]; $g = $b[$i + 1]; $r = $b[$i + 2]
                if ([Math]::Abs($r - 255) -le $tol -and $g -le $tol -and [Math]::Abs($bl - 255) -le $tol) {
                    $mg++
                    if ($x -lt $minx) { $minx = $x }; if ($x -gt $maxx) { $maxx = $x }
                    if ($y -lt $miny) { $miny = $y }; if ($y -gt $maxy) { $maxy = $y }
                }
                if ($r -gt 140 -and $bl -gt 140 -and $g -lt 90) { $pu++ }
                if (($r + $g + $bl) -gt 90) {
                    $k = "$r,$g,$bl"
                    if ($hist.ContainsKey($k)) { $hist[$k] = $hist[$k] + 1 } else { $hist[$k] = 1 }
                }
            }
        }
        $bbox = "none"
        if ($mg -gt 0) { $bbox = "x $minx..$maxx y $miny..$maxy" }
        $top = ($hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 5 | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join "  "
        [void]$sb.AppendLine(("{0} {1}x{2} magenta={3} purple={4} [{5}]" -f $f.Name, $img.w, $img.h, $mg, $pu, $bbox))
        [void]$sb.AppendLine("    $top")
    }
} catch {
    [void]$sb.AppendLine("EXCEPTION: " + $_.Exception.ToString())
}
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

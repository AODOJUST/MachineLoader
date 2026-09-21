param([string]$Path)
$ErrorActionPreference = "Stop"
$bytes = [System.IO.File]::ReadAllBytes($Path)
function Find-Seq([byte[]]$b, [byte[]]$pat) {
    $hits = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -le $b.Length - $pat.Length; $i++) {
        $m = $true
        for ($j = 0; $j -lt $pat.Length; $j++) { if ($b[$i+$j] -ne $pat[$j]) { $m = $false; break } }
        if ($m) { $hits.Add($i) }
    }
    return $hits
}
function Show([int]$h, [int]$before, [int]$after) {
    $start = [Math]::Max(0, $h - $before)
    $end = [Math]::Min($bytes.Length, $h + $after)
    $sb = New-Object System.Text.StringBuilder
    for ($k = $start; $k -lt $end; $k++) {
        $b = $bytes[$k]
        if ($b -ge 32 -and $b -lt 127) { [void]$sb.Append([char]$b) } else { [void]$sb.Append(".") }
    }
    return ("@" + $h + " : " + $sb.ToString())
}
$pat1 = [System.Text.Encoding]::ASCII.GetBytes("Injected")
$hits1 = Find-Seq $bytes $pat1
Write-Output ("Injected hits=" + $hits1.Count)
foreach ($h in $hits1) { Write-Output (Show $h 100 250) }
Write-Output "=== Initialize near Machine ==="
$pat2 = [System.Text.Encoding]::ASCII.GetBytes("Initialize")
$hits2 = Find-Seq $bytes $pat2
Write-Output ("Initialize hits=" + $hits2.Count)
foreach ($h in $hits2) { Write-Output (Show $h 40 120) }

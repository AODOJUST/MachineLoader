param([string]$Path)
$ErrorActionPreference = "Stop"
$bytes = [System.IO.File]::ReadAllBytes($Path)
# 搜索 ASCII "Machine" 序列
$pattern = [System.Text.Encoding]::ASCII.GetBytes("Machine")
$hits = New-Object System.Collections.Generic.List[int]
for ($i = 0; $i -le $bytes.Length - $pattern.Length; $i++) {
    $match = $true
    for ($j = 0; $j -lt $pattern.Length; $j++) {
        if ($bytes[$i+$j] -ne $pattern[$j]) { $match = $false; break }
    }
    if ($match) { $hits.Add($i) }
}
Write-Output ("hits=" + $hits.Count)
$shown = 0
foreach ($h in $hits) {
    if ($shown -ge 15) { break }
    # 提取该位置前后各 80 字节的可打印 ASCII
    $start = [Math]::Max(0, $h - 80)
    $end = [Math]::Min($bytes.Length, $h + 200)
    $sb = New-Object System.Text.StringBuilder
    for ($k = $start; $k -lt $end; $k++) {
        $b = $bytes[$k]
        if ($b -ge 32 -and $b -lt 127) { [void]$sb.Append([char]$b) } else { [void]$sb.Append(".") }
    }
    Write-Output ("@" + $h + " : " + $sb.ToString())
    $shown++
}

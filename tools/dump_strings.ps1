$ErrorActionPreference = "SilentlyContinue"
# 读补丁版 dll 字节，找 "Machine" 相关字符串的上下文
$bytes = [System.IO.File]::ReadAllBytes("D:\steam\steamapps\common\Aviassembly\Aviassembly_Data\Managed\Assembly-CSharp.dll")
# 用 ASCII 扫描可读字符串
$sb = New-Object System.Text.StringBuilder
$strings = New-Object System.Collections.Generic.List[string]
foreach ($b in $bytes) {
    if ($b -ge 32 -and $b -lt 127) { [void]$sb.Append([char]$b) }
    else {
        if ($sb.Length -ge 4) { $strings.Add($sb.ToString()) }
        [void]$sb.Clear()
    }
}
Write-Output "=== Machine-related strings in patched dll ==="
$strings | Where-Object { $_ -match "Machine|Bootstrap|RuntimeInitialize" } | Select-Object -First 40

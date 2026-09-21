$ErrorActionPreference = "SilentlyContinue"
$bytes = [System.IO.File]::ReadAllBytes("D:\steam\steamapps\common\Aviassembly\Aviassembly_Data\Managed\Assembly-CSharp.dll")
$text = [System.Text.Encoding]::Unicode.GetString($bytes)
# 找 Machine 相关片段
$idx = $text.IndexOf("Machine")
$count = 0
while ($idx -ge 0 -and $count -lt 30) {
    $start = [Math]::Max(0, $idx - 60)
    $len = [Math]::Min(200, $text.Length - $start)
    $snippet = $text.Substring($start, $len) -replace "[\r\n]", " "
    Write-Output ("@" + $idx + ": ..." + $snippet + "...")
    $idx = $text.IndexOf("Machine", $idx + 1)
    $count++
}

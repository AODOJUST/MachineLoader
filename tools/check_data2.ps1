$ErrorActionPreference = "SilentlyContinue"
$devData = "D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data"
$origData = "D:\steam\steamapps\common\Aviassembly\Aviassembly_Data"
Write-Output "=== dev Data files (top) ==="
try {
    $files = [System.IO.Directory]::GetFiles($devData)
    Write-Output ("count=" + $files.Count)
    $files | ForEach-Object { Write-Output ([System.IO.Path]::GetFileName($_)) }
} catch { Write-Output ("ERR: " + $_.Exception.Message) }
Write-Output "=== dev Data dirs ==="
try {
    $dirs = [System.IO.Directory]::GetDirectories($devData)
    Write-Output ("count=" + $dirs.Count)
    $dirs | ForEach-Object { Write-Output ([System.IO.Path]::GetFileName($_)) }
} catch { Write-Output ("ERR: " + $_.Exception.Message) }
Write-Output "=== orig Data files (top, first 20) ==="
try {
    $of = [System.IO.Directory]::GetFiles($origData)
    Write-Output ("count=" + $of.Count)
    $of | Select-Object -First 20 | ForEach-Object { Write-Output ([System.IO.Path]::GetFileName($_)) }
} catch { Write-Output ("ERR: " + $_.Exception.Message) }

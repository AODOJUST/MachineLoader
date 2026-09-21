$ErrorActionPreference = "SilentlyContinue"
$devDir = "D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data\Managed"
$origDir = "D:\steam\steamapps\common\Aviassembly\Aviassembly_Data\Managed"
$devFiles = Get-ChildItem $devDir -File | Select-Object -ExpandProperty Name
$origFiles = Get-ChildItem $origDir -File | Select-Object -ExpandProperty Name
Write-Output "=== only in DEV ==="
$devFiles | Where-Object { $_ -notin $origFiles }
Write-Output "=== only in ORIG ==="
$origFiles | Where-Object { $_ -notin $devFiles }
Write-Output "=== same name different size ==="
$devMap = @{}
Get-ChildItem $devDir -File | ForEach-Object { $devMap[$_.Name] = $_.Length }
Get-ChildItem $origDir -File | ForEach-Object {
    if ($devMap.ContainsKey($_.Name) -and $devMap[$_.Name] -ne $_.Length) {
        Write-Output ($_.Name + " dev=" + $devMap[$_.Name] + " orig=" + $_.Length)
    }
}
Write-Output "=== counts ==="
Write-Output ("dev=" + $devFiles.Count + " orig=" + $origFiles.Count)

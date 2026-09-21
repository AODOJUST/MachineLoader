$ErrorActionPreference = "SilentlyContinue"
$devData = "D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data"
$origData = "D:\steam\steamapps\common\Aviassembly\Aviassembly_Data"
$dItem = Get-Item $devData
Write-Output ("dev Data Attributes: " + $dItem.Attributes)
Write-Output ("dev Data LinkType: " + $dItem.LinkType)
$oItem = Get-Item $origData
Write-Output ("orig Data Attributes: " + $oItem.Attributes)
Write-Output "--- dev Data top-level ---"
Get-ChildItem $devData | Select-Object Name, Length, Attributes | Format-Table -AutoSize

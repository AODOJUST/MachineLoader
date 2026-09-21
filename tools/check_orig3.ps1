$ErrorActionPreference = "SilentlyContinue"
"---DEV root---"
Get-ChildItem "D:\豆包的下载\Aviassembly_DEV" | Select-Object Name, Length | Format-Table -AutoSize
"---ORIG root---"
Get-ChildItem "D:\steam\steamapps\common\Aviassembly" | Select-Object Name, Length | Format-Table -AutoSize
"---orig game exe check---"
Test-Path "D:\steam\steamapps\common\Aviassembly\Aviassembly.exe"

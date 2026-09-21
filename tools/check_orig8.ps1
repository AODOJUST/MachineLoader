$ErrorActionPreference = "SilentlyContinue"
$devExe = "D:\豆包的下载\Aviassembly_DEV\Aviassembly.exe"
$origExe = "D:\steam\steamapps\common\Aviassembly\Aviassembly.exe"
$devUP = "D:\豆包的下载\Aviassembly_DEV\UnityPlayer.dll"
$origUP = "D:\steam\steamapps\common\Aviassembly\UnityPlayer.dll"
$devMono = "D:\豆包的下载\Aviassembly_DEV\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll"
$origMono = "D:\steam\steamapps\common\Aviassembly\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll"
Write-Output ("exe: " + (Get-FileHash $devExe -Algorithm MD5).Hash + " vs " + (Get-FileHash $origExe -Algorithm MD5).Hash)
Write-Output ("up:  " + (Get-FileHash $devUP -Algorithm MD5).Hash + " vs " + (Get-FileHash $origUP -Algorithm MD5).Hash)
Write-Output ("mono:" + (Get-FileHash $devMono -Algorithm MD5).Hash + " vs " + (Get-FileHash $origMono -Algorithm MD5).Hash)

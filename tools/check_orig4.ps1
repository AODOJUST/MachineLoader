$ErrorActionPreference = "SilentlyContinue"
"---all Assembly-CSharp in orig---"
Get-ChildItem "D:\steam\steamapps\common\Aviassembly" -Recurse -Filter "Assembly-CSharp*.dll" | Select-Object FullName, Length, LastWriteTime
"---proc assembly---"
Get-Process -Name Aviassembly | ForEach-Object {
    $_.Modules | Where-Object { $_.ModuleName -like "Assembly-CSharp*" } | Select-Object ModuleName, FileName
}
"---player log machine grep---"
$pl = "$env:USERPROFILE\AppData\LocalLow\Aviassembly\Aviassembly\Player.log"
if (Test-Path $pl) {
    Get-Content $pl | Select-String -Pattern "Machine|Bootstrap|Exception" | Select-Object -Last 10
}
"---output_log---"
$ol = "$env:USERPROFILE\AppData\LocalLow\Aviassembly\Aviassembly\output_log.txt"
if (Test-Path $ol) { Get-Item $ol | Select-Object FullName, LastWriteTime }

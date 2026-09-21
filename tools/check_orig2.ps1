$ErrorActionPreference = "SilentlyContinue"
$playerLog = "$env:USERPROFILE\AppData\LocalLow\Aviassembly\Aviassembly\Player.log"
"---player.log---"
if (Test-Path $playerLog) {
    Get-Content $playerLog -Tail 40
} else {
    "no player.log"
}
"---exe hash---"
certutil -hashfile "D:\steam\steamapps\common\Aviassembly\Aviassembly.exe" MD5
certutil -hashfile "D:\豆包的下载\Aviassembly_DEV\Aviassembly.exe" MD5
"---dev machine log head---"
if (Test-Path "D:\豆包的下载\Aviassembly_DEV\Machine\logs\Machine.log") {
    Get-Content "D:\豆包的下载\Aviassembly_DEV\Machine\logs\Machine.log" -TotalCount 6
}

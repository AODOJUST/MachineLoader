#Requires -Version 5.1
# 同步 FactionSystem.dll 到所有安装/发布副本，并生成 md5 对账报告。
# 规则：游戏运行时不部署；覆盖前校验源文件存在。
param(
    [string]$Src = "$PSScriptRoot\..\bin\mods\FactionSystem.dll",
    [string]$Report = "$PSScriptRoot\..\_deploy_report_faction.txt"
)
$ErrorActionPreference = 'Stop'
$src = Resolve-Path $Src -ErrorAction Stop

# 检查游戏是否运行
$games = Get-Process | Where-Object { $_.ProcessName -match 'Aviassembly' }
if ($games) {
    Write-Output "ERROR: Aviassembly is running. Abort deploy."
    exit 7
}

# 目标列表：根级镜像 + code/ 副本
$targets = @(
    @{Path="D:\豆包的下载\Aviassembly_DEV\mods\FactionSystem\code\FactionSystem.dll"; Root=$false},
    @{Path="D:\豆包的下载\Aviassembly_DEV\mods\FactionSystem\FactionSystem.dll"; Root=$true},
    @{Path="D:\steam\steamapps\common\Aviassembly\mods\FactionSystem\code\FactionSystem.dll"; Root=$false},
    @{Path="D:\豆包的下载\Machine_Dev\dist\mods\FactionSystem\code\FactionSystem.dll"; Root=$false},
    @{Path="D:\豆包的下载\Machine_Dev\dist_upload\mods\FactionSystem\code\FactionSystem.dll"; Root=$false},
    @{Path="D:\豆包的下载\MachineLoader_work\mods\FactionSystem\code\FactionSystem.dll"; Root=$false},
    @{Path="D:\豆包的下载\MachineLoader_github\MachineLoader-main\mods\FactionSystem\code\FactionSystem.dll"; Root=$false}
)

$md5 = [System.Security.Cryptography.MD5]::Create()
function Get-Md5($p) {
    $fs = [System.IO.File]::OpenRead($p)
    try { return [BitConverter]::ToString($md5.ComputeHash($fs)).Replace('-','').ToLower() }
    finally { $fs.Dispose() }
}
$srcHash = Get-Md5 $src
$srcBytes = (Get-Item $src).Length
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("FactionSystem deploy report  $stamp")
$lines.Add("source: $src  bytes=$srcBytes  md5=$srcHash")

$ok = 0
foreach ($t in $targets) {
    $p = $t.Path
    $dir = [System.IO.Path]::GetDirectoryName($p)
    if (!(Test-Path $dir)) {
        $lines.Add("MISSING DIR  $p")
        continue
    }
    try {
        [System.IO.File]::Copy($src, $p, $true)
        $h = Get-Md5 $p
        $b = (Get-Item $p).Length
        $match = if ($h -eq $srcHash) { 'OK' } else { 'MISMATCH' }
        $lines.Add("$match  bytes=$b  md5=$h  -> $p")
        if ($match -eq 'OK') { $ok++ }
    }
    catch {
        $lines.Add("FAIL  $_  -> $p")
    }
}
$lines.Add("summary: $ok / $($targets.Count) targets OK")
[System.IO.File]::WriteAllLines($Report, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Output "deployed $ok / $($targets.Count)"

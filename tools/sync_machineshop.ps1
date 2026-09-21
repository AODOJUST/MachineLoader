$ErrorActionPreference = "Continue"
$tools = $PSScriptRoot                       # ...\Machine_Dev\tools
$md    = Split-Path $tools -Parent           # ...\Machine_Dev
$ws    = Split-Path $md -Parent              # ...\D:\ (parent of Machine_Dev = workspace root)
$src   = Join-Path (Join-Path (Join-Path $ws "Aviassembly_DEV") "mods\MachineShop\code") "MachineShop.dll"
$log   = Join-Path $md "_ms_sync_report.txt"
[System.IO.File]::WriteAllText((Join-Path $md "_ms_sync_start.txt"), "started " + (Get-Date).ToString("HH:mm:ss"))
$sb = New-Object System.Text.StringBuilder
function Say($s){ [void]$sb.AppendLine([string]$s) }
try {
    if (-not (Test-Path $src)) { Say ("SRC MISSING " + $src); [System.IO.File]::WriteAllText($log, $sb.ToString()); exit 3 }
    $srcHash = (Get-FileHash $src -Algorithm MD5).Hash
    Say ("src size=" + (Get-Item $src).Length + " md5=" + $srcHash)
    $dests = @(
        (Join-Path (Join-Path (Join-Path $ws "MachineLoader_work") "mods\MachineShop") "code\MachineShop.dll"),
        (Join-Path (Join-Path (Join-Path (Join-Path $ws "MachineLoader_github") "MachineLoader-main") "mods\MachineShop") "code\MachineShop.dll"),
        (Join-Path (Join-Path (Join-Path "D:\steam\steamapps\common\Aviassembly" "mods") "MachineShop") "code\MachineShop.dll")
    )
    foreach ($d in $dests) {
        $dir = Split-Path $d
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null | Out-Null }
        try {
            Copy-Item $src $d -Force
            $h = (Get-FileHash $d -Algorithm MD5).Hash
            $ok = ($h -eq $srcHash)
            $tag = "OK   "
            if (-not $ok) { $tag = "BAD  " }
            Say ($tag + $d + " md5=" + $h.Substring(0,8) + " match=" + $ok)
        } catch {
            Say ("FAIL " + $d + ": " + $_.Exception.Message)
        }
    }
} catch {
    Say ("FATAL " + $_.Exception.Message)
}
[System.IO.File]::WriteAllText($log, $sb.ToString())

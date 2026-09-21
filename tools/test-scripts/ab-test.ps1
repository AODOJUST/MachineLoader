# A/B session probe: set the mod disable list, launch the game, hold it, kill it, extract the new log slice.
# One PowerShell call must do launch + wait + kill + read, because the game is reaped as soon as the
# launching shell call returns. ASCII-only. Results -> ab_<Tag>.txt / ab_<Tag>_kept.txt / ab_<Tag>_slice.txt
param(
    [string]$Tag = "run",
    [string]$Disable = "",
    [int]$HoldSec = 180
)

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$machineLog = Join-Path $gameRoot "Machine\logs\Machine.log"
$cfgPath = Join-Path $gameRoot "Machine\config.json"
$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 3

$cfgDir = Split-Path $cfgPath -Parent
if (-not (Test-Path $cfgDir)) { [void](New-Item -ItemType Directory -Path $cfgDir -Force) }
if ([string]::IsNullOrWhiteSpace($Disable)) {
    $json = '{"disabled":[]}'
} else {
    $items = @(($Disable -split ",") | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
    $quoted = ($items | ForEach-Object { '"' + $_ + '"' }) -join ","
    $json = '{"disabled":[' + $quoted + ']}'
}
[System.IO.File]::WriteAllText($cfgPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Say ("config = " + $json)

if (-not (Test-Path $machineLog)) { Say "no Machine.log"; [System.IO.File]::WriteAllText((Join-Path $root ("ab_" + $Tag + ".txt")), $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))); return }
$pre = (Get-Item $machineLog).Length
Say ("pre log size = " + $pre)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $gameRoot
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
try { $p = [System.Diagnostics.Process]::Start($psi); Say ("started pid = " + $p.Id) }
catch { Say ("launch FAILED: " + $_.Exception.Message) }

$steps = [int]($HoldSec / 15)
if ($steps -lt 1) { $steps = 1 }
for ($i = 0; $i -lt $steps; $i++) {
    Start-Sleep -Seconds 15
    $n = (Get-Process Aviassembly* -ErrorAction SilentlyContinue | Measure-Object).Count
    try { $ls = (Get-Item $machineLog).Length } catch { $ls = -1 }
    Say ("t=" + (($i + 1) * 15) + "s procs=" + $n + " logsize=" + $ls)
    if ($n -eq 0) { break }
}

Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 4

try {
    $len = (Get-Item $machineLog).Length
    if ($len -gt $pre) {
        $bytes = New-Object byte[] ($len - $pre)
        $fs = [System.IO.File]::OpenRead($machineLog)
        try { [void]$fs.Seek($pre, [System.IO.SeekOrigin]::Begin); [void]$fs.Read($bytes, 0, $bytes.Length) } finally { $fs.Close() }
        $txt = [System.Text.Encoding]::UTF8.GetString($bytes)
        [System.IO.File]::WriteAllText((Join-Path $root ("ab_" + $Tag + "_slice.txt")), $txt, (New-Object System.Text.UTF8Encoding($false)))
        $keep = New-Object System.Text.StringBuilder
        foreach ($ln in ($txt -split "`r?`n")) {
            if ($ln -match "OptiMod:|code mod loaded|Radar: shop|Radar: SHOP|scene=|Machine v") { [void]$keep.AppendLine($ln) }
        }
        [System.IO.File]::WriteAllText((Join-Path $root ("ab_" + $Tag + "_kept.txt")), $keep.ToString(), (New-Object System.Text.UTF8Encoding($false)))
        Say ("slice bytes = " + $bytes.Length)
    } else { Say "log did not grow" }
} catch { Say ("extract failed: " + $_.Exception.Message) }

[System.IO.File]::WriteAllText((Join-Path $root ("ab_" + $Tag + ".txt")), $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

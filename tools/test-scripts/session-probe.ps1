# One-shot session probe: deploy OptiMod, launch the game, hold it, then extract the new log slice.
# The sandbox reaps the game as soon as the launching shell call returns, so launch + wait + collect
# must all happen inside ONE call. ASCII-only. Results -> session_probe.txt
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$machineLog = Join-Path $gameRoot "Machine\logs\Machine.log"
$out = Join-Path $root "session_probe.txt"
$holdSec = 200

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

# --- 0) stop any running instance ---
Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 3
Say ("procs after kill = " + ((Get-Process Aviassembly* -ErrorAction SilentlyContinue | Measure-Object).Count))

# --- 1) deploy ---
$src = Join-Path $root "bin\mods\OptiMod.dll"
$pairs = @(
    @{ S = $src; D = (Join-Path $gameRoot "mods\OptiMod\code\OptiMod.dll") },
    @{ S = (Join-Path $gameRoot "mods\OptiMod\opti_config.json"); D = (Join-Path $root "bin\mods_staging\OptiMod\opti_config.json") },
    @{ S = (Join-Path $gameRoot "mods\OptiMod\mod.json"); D = (Join-Path $root "bin\mods_staging\OptiMod\mod.json") },
    @{ S = $src; D = (Join-Path $root "bin\mods_staging\OptiMod\code\OptiMod.dll") }
)
foreach ($c in $pairs) {
    try { Copy-Item $c.S $c.D -Force -ErrorAction Stop; $fi = Get-Item $c.D; Say ("deploy OK " + $fi.FullName + " " + $fi.Length + " " + $fi.LastWriteTime) }
    catch { Say ("deploy FAIL " + $c.D + " :: " + $_.Exception.Message) }
}

# --- 2) launch ---
$pre = (Get-Item $machineLog).Length
Say ("pre log size = " + $pre)
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $gameRoot
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$p = $null
try { $p = [System.Diagnostics.Process]::Start($psi); Say ("started pid = " + $p.Id) }
catch { Say ("launch FAILED: " + $_.Exception.Message) }

# --- 3) hold ---
for ($i = 0; $i -lt 13; $i++) {
    Start-Sleep -Seconds 15
    $n = (Get-Process Aviassembly* -ErrorAction SilentlyContinue | Measure-Object).Count
    Say ("t=" + (($i + 1) * 15) + "s procs=" + $n + " logsize=" + (Get-Item $machineLog).Length)
    if ($n -eq 0) { break }
}

# --- 4) stop the game so the log handle is released, then extract the new slice ---
Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 4
try {
    $len = (Get-Item $machineLog).Length
    if ($len -gt $pre) {
        $bytes = New-Object byte[] ($len - $pre)
        $fs = [System.IO.File]::OpenRead($machineLog)
        try { [void]$fs.Seek($pre, [System.IO.SeekOrigin]::Begin); [void]$fs.Read($bytes, 0, $bytes.Length) } finally { $fs.Close() }
        $txt = [System.Text.Encoding]::UTF8.GetString($bytes)
        $keep = New-Object System.Text.StringBuilder
        foreach ($ln in ($txt -split "`r?`n")) {
            if ($ln -match "OptiMod|scene exited|scene loaded|Machine v1.0.0 boot|Faction|AAM: AI aircraft spawned|BUILD|installer") { [void]$keep.AppendLine($ln) }
        }
        [System.IO.File]::WriteAllText((Join-Path $root "session_slice.txt"), $keep.ToString(), (New-Object System.Text.UTF8Encoding($false)))
        Say ("slice written, raw bytes = " + $bytes.Length)
    } else { Say "log did not grow" }
} catch { Say ("extract failed: " + $_.Exception.Message) }

[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

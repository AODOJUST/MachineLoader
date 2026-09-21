# Launch the game, run the MachineAAM self-test, collect the log slice + screenshots, then stop it.
# - Waits up to 180s for any already-running Aviassembly instance to exit (other devs share this install).
# - Only inspects log lines appended after launch.
# ASCII-only; paths derived from $PSScriptRoot. Output -> launch_aam.txt
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$log = Join-Path $gameRoot "Machine\logs\Machine.log"
$shots = Join-Path $gameRoot "mods\MachineAAM"
$outPath = Join-Path $root "launch_aam.txt"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }
function Flush { [System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))) }

function Read-FromOffset([string]$path, [long]$offset) {
    if (-not (Test-Path $path)) { return "" }
    try {
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        if ($fs.Length -lt $offset) { $offset = 0 }
        [void]$fs.Seek($offset, [System.IO.SeekOrigin]::Begin)
        $sr = New-Object System.IO.StreamReader($fs)
        $txt = $sr.ReadToEnd()
        $sr.Close(); $fs.Close()
        return $txt
    } catch { return "" }
}

$start = Get-Date
Say ("now = " + $start.ToString("HH:mm:ss"))

# wait for other instances to go away
$waitDeadline = (Get-Date).AddSeconds(180)
while ($true) {
    $existing = Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue
    if (-not $existing) { break }
    if ((Get-Date) -gt $waitDeadline) {
        Say ("ABORT: Aviassembly still running (pid " + ($existing | ForEach-Object { $_.Id }) + ") after 180s")
        Flush; exit 1
    }
    Start-Sleep -Seconds 5
}
Say "pre-existing instance cleared"

$logLen0 = 0
if (Test-Path $log) { $logLen0 = (Get-Item $log).Length }
Say ("log start offset = " + $logLen0)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $gameRoot
$psi.UseShellExecute = $true
try {
    $p = [System.Diagnostics.Process]::Start($psi)
    Say ("started pid = " + $p.Id)
} catch {
    Say ("launch failed: " + $_.Exception.Message)
    Flush; exit 1
}

$deadline = (Get-Date).AddSeconds(300)
$done = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $elapsed = [int]((Get-Date) - $p.StartTime).TotalSeconds
    $new = Read-FromOffset $log $logLen0
    $has = ($new -ne $null) -and $new.Contains("AAM SELFTEST: done")
    Say ("t=" + $elapsed + "s newLog=" + $new.Length + " aamSelftestDone=" + $has)
    Flush
    if ($has) { $done = $true; break }
    if ($p.HasExited) { Say ("game exited early, code=" + $p.ExitCode); break }
}
Say ("aamSelftestDone=" + $done)

$new = Read-FromOffset $log $logLen0
[System.IO.File]::WriteAllText((Join-Path $root "aam_run_log.txt"), $new, (New-Object System.Text.UTF8Encoding($false)))
Say ("log slice written, length=" + $new.Length)

Get-ChildItem $shots -Filter *.png -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt $start } |
    Sort-Object Name | ForEach-Object { Say ("newshot " + $_.Name + " " + $_.LastWriteTime.ToString("HH:mm:ss") + " " + $_.Length + " bytes") }

$alive = -not $p.HasExited
Say ("game alive before stop = " + $alive)
if ($alive) {
    try { $p.Kill(); Start-Sleep -Seconds 3; Say "game stopped" } catch { Say ("stop failed: " + $_.Exception.Message) }
}
Flush

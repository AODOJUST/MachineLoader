param(
    [int]$DeadlineSec = 240,
    [string]$Tag = "shop"
)
# MachineShop self-test runner. Same shape as run-aam-test.ps1 but waits for the
# MachineShop self-test marker and collects screenshots from Machine/logs/machineshop_shots.
# ASCII-only; all paths derived from $PSScriptRoot.

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$log = Join-Path $gameRoot "Machine\logs\Machine.log"
$shots = Join-Path $gameRoot "Machine\logs\machineshop_shots"
$outPath = Join-Path $root ("launch_" + $Tag + "_log.txt")
$runLog = Join-Path $root ("shop_run_log_" + $Tag + ".txt")

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }
function Flush { try { [System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))) } catch { } }

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
Say ("now = " + $start.ToString("HH:mm:ss") + " deadline=" + $DeadlineSec + "s tag=" + $Tag)
Flush

$waitDeadline = (Get-Date).AddSeconds(240)
while ($true) {
    $existing = Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue
    if (-not $existing) { break }
    if ((Get-Date) -gt $waitDeadline) {
        Say ("ABORT: Aviassembly still running after 240s")
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

function Focus-Game([int]$procId) {
    try {
        $sh = New-Object -ComObject WScript.Shell
        [void]$sh.AppActivate($procId)
    } catch { }
}
Focus-Game $p.Id

$deadline = (Get-Date).AddSeconds($DeadlineSec)
$done = $false
$lastBeat = 0
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    Focus-Game $p.Id
    $elapsed = [int]((Get-Date) - $p.StartTime).TotalSeconds
    $new = Read-FromOffset $log $logLen0
    $has = ($new -ne $null) -and $new.Contains("MachineShop SELFTEST: done")
    if ($elapsed - $lastBeat -ge 20 -or $has) {
        $lastBeat = $elapsed
        Say ("t=" + $elapsed + "s newLog=" + $new.Length + " done=" + $has)
        Flush
    }
    if ($has) { $done = $true; break }
    if ($p.HasExited) { Say ("game exited early, code=" + $p.ExitCode); break }
}
Say ("selftestDone=" + $done)

$new = Read-FromOffset $log $logLen0
[System.IO.File]::WriteAllText($runLog, $new, (New-Object System.Text.UTF8Encoding($false)))
Say ("log slice written -> " + $runLog + " length=" + $new.Length)

Get-ChildItem $shots -Filter *.png -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt $start } |
    Sort-Object Name | ForEach-Object { Say ("newshot " + $_.Name + " " + $_.LastWriteTime.ToString("HH:mm:ss") + " " + $_.Length + " bytes") }

$alive = -not $p.HasExited
Say ("game alive before stop = " + $alive)
if ($alive) {
    try { $p.Kill(); Start-Sleep -Seconds 3; Say "game stopped" } catch { Say ("stop failed: " + $_.Exception.Message) }
}
Flush

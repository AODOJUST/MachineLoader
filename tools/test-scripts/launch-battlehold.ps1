# Launch the game and wait for THIS run's BattleHold self-test to finish, then stop it
# to release the files (other devs work on the same install).
# Important: only look at log lines appended after launch (the previous run's result
# line would otherwise be seen immediately).
# ASCII-only; paths derived from $PSScriptRoot. Output -> launch_battlehold.txt
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$log = Join-Path $gameRoot "Machine\logs\Machine.log"
$shots = Join-Path $gameRoot "Machine\logs\battlehold_shots"
$outPath = Join-Path $root "launch_battlehold.txt"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

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
$existing = Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue
if ($existing) {
    Say ("ABORT: Aviassembly already running (pid " + ($existing | ForEach-Object { $_.Id }) + ")")
    [System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    exit 1
}
Say ("exe exists = " + (Test-Path $exe))
if (-not (Test-Path $shots)) { New-Item -ItemType Directory -Force -Path $shots | Out-Null }
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
    [System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    exit 1
}

$deadline = (Get-Date).AddSeconds(280)
$done = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $elapsed = [int]((Get-Date) - $p.StartTime).TotalSeconds
    $new = Read-FromOffset $log $logLen0
    $has = ($new -ne $null) -and $new.Contains("BattleHold: self-test done")
    Say ("t=" + $elapsed + "s newLog=" + $new.Length + " selftestDone=" + $has)
    if ($has) { $done = $true; break }
    if ($p.HasExited) { Say ("game exited early, code=" + $p.ExitCode); break }
}
Say ("selftestDone=" + $done)

# dump THIS run's log slice so the report is self-contained
$new = Read-FromOffset $log $logLen0
[System.IO.File]::WriteAllText((Join-Path $root "battlehold_run_log.txt"), $new, (New-Object System.Text.UTF8Encoding($false)))
Say ("log slice written, length=" + $new.Length)

Get-ChildItem $shots -Filter *.png -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt $start } |
    Sort-Object Name | ForEach-Object { Say ("newshot " + $_.Name + " " + $_.LastWriteTime.ToString("HH:mm:ss") + " " + $_.Length + " bytes") }

$alive = -not $p.HasExited
Say ("game alive before stop = " + $alive)
if ($alive) {
    try { $p.Kill(); Start-Sleep -Seconds 3; Say "game stopped" } catch { Say ("stop failed: " + $_.Exception.Message) }
}
[System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

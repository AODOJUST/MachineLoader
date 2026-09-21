# Smoke-launch: start the game, wait for this run's BattleHold UI build + layout log,
# then stop the game. ASCII only. Output -> launch_holdui.txt / holdui_run_log.txt
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$log = Join-Path $gameRoot "Machine\logs\Machine.log"
$outPath = Join-Path $root "launch_holdui.txt"

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
$existing = Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue
if ($existing) {
    Say ("ABORT: Aviassembly already running (pid " + ($existing | ForEach-Object { $_.Id }) + ")")
    [System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    exit 1
}
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

$deadline = (Get-Date).AddSeconds(150)
$done = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $new = Read-FromOffset $log $logLen0
    $has = ($new -ne $null) -and $new.Contains("BattleHold: layout")
    $bad = ($new -ne $null) -and $new.Contains("BattleHold: build UI failed")
    if ($has) { $done = $true; Say ("layout line seen"); break }
    if ($bad) { Say ("BUILD UI FAILED"); break }
    if ($p.HasExited) { Say ("game exited early, code=" + $p.ExitCode); break }
}
Say ("layoutSeen=" + $done)

$new = Read-FromOffset $log $logLen0
[System.IO.File]::WriteAllText((Join-Path $root "holdui_run_log.txt"), $new, (New-Object System.Text.UTF8Encoding($false)))
Say ("log slice written, length=" + $new.Length)

$alive = -not $p.HasExited
if ($alive) {
    try { $p.Kill(); Start-Sleep -Seconds 3; Say "game stopped" } catch { Say ("stop failed: " + $_.Exception.Message) }
}
[System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

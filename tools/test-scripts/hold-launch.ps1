# Supervise a game session: start the game and keep this process (and therefore the game) alive.
# Why: this sandbox reaps the game process as soon as the launching shell call returns, so the
# launcher has to stay resident. Run this script as a BACKGROUND task and read hold_status.txt.
# ASCII-only on purpose (PowerShell 5.1 mis-decodes non-ASCII .ps1 without a BOM).
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$status = Join-Path $root "hold_status.txt"
$machineLog = Join-Path $gameRoot "Machine\logs\Machine.log"

function Status($s) {
    $line = (Get-Date -Format "HH:mm:ss") + " " + $s
    [System.IO.File]::AppendAllText($status, $line + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
}

[System.IO.File]::WriteAllText($status, "", (New-Object System.Text.UTF8Encoding($false)))
Status ("pre log size = " + (Get-Item $machineLog).Length)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $gameRoot
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$p = $null
try {
    $p = [System.Diagnostics.Process]::Start($psi)
    Status ("started pid = " + $p.Id)
} catch {
    Status ("launch FAILED: " + $_.Exception.Message)
}

# Hold for up to 20 minutes; report liveness + log size every 15 s.
for ($i = 0; $i -lt 80; $i++) {
    Start-Sleep -Seconds 15
    $n = (Get-Process Aviassembly* -ErrorAction SilentlyContinue | Measure-Object).Count
    $sz = (Get-Item $machineLog).Length
    Status ("t=" + ($i * 15) + "s procs=" + $n + " logsize=" + $sz)
    if ($n -eq 0 -and $i -gt 2) { Status "game gone, stopping supervisor"; break }
}
Status "supervisor exit"

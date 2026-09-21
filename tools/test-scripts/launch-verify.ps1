# Launch the game for verification (ASCII-only; output -> launch_verify_log.txt).
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$logPath = Join-Path $root "launch_verify_log.txt"
$machineLog = Join-Path $gameRoot "Machine\logs\Machine.log"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue | ForEach-Object {
    Say ("killing existing pid " + $_.Id)
    $_.Kill()
}
Start-Sleep -Seconds 2

Say ("pre log size = " + (Get-Item $machineLog).Length)
Say ("exe exists = " + (Test-Path $exe))
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $gameRoot
$psi.UseShellExecute = $true
try {
    $p = [System.Diagnostics.Process]::Start($psi)
    Say ("started pid = " + $p.Id)
} catch {
    Say ("launch failed: " + $_.Exception.Message)
}
[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

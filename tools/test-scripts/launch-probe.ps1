# Launch the game, then report whether it stayed alive (ASCII-only; output -> launch_probe_log.txt).
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$out = Join-Path $root "launch_probe_log.txt"
$machineLog = Join-Path $gameRoot "Machine\logs\Machine.log"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

Say ("exe = " + $exe + " exists=" + (Test-Path $exe))
Say ("pre log size = " + (Get-Item $machineLog).Length)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $gameRoot
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$p = $null
try {
    $p = [System.Diagnostics.Process]::Start($psi)
    Say ("started pid = " + $p.Id)
} catch {
    Say ("launch FAILED: " + $_.Exception.Message)
    [System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    exit
}

Start-Sleep -Seconds 25
$alive = $false
try { $alive = -not $p.HasExited } catch { }
Say ("after 25s alive = " + $alive)
if (-not $alive) {
    try { Say ("exit code = " + $p.ExitCode) } catch { Say "exit code unavailable" }
    try { Say ("stdout: " + $p.StandardOutput.ReadToEnd()) } catch { }
    try { Say ("stderr: " + $p.StandardError.ReadToEnd()) } catch { }
} else {
    try {
        Say ("proc cpuSec=" + [math]::Round($p.TotalProcessorTime.TotalSeconds,1) + " wsMB=" + [math]::Round($p.WorkingSet64/1MB,0))
    } catch { }
}
Say ("post log size = " + (Get-Item $machineLog).Length)
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

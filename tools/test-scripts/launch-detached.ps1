# Launch the game fully detached (no inherited stdout/stderr pipes, no blocking wait).
# Root cause of the earlier failure: with RedirectStandardOutput=$true the game dies the moment the
# launching PowerShell process exits (broken pipe). So: UseShellExecute=$false + CreateNoWindow=$true,
# and NO redirection at all. Result is written to launch_detached_log.txt.
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$out = Join-Path $root "launch_detached_log.txt"
$machineLog = Join-Path $gameRoot "Machine\logs\Machine.log"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

Say ("exe exists = " + (Test-Path $exe))
Say ("pre log size = " + (Get-Item $machineLog).Length)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $gameRoot
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $false
$psi.RedirectStandardError = $false
try {
    $p = [System.Diagnostics.Process]::Start($psi)
    Say ("started pid = " + $p.Id)
} catch {
    Say ("launch FAILED: " + $_.Exception.Message)
}

Start-Sleep -Seconds 12
$p2 = Get-Process Aviassembly* -ErrorAction SilentlyContinue
Say ("after 12s procs = " + ($p2 | Measure-Object).Count)
foreach ($x in $p2) { Say ("  id=" + $x.Id + " wsMB=" + [math]::Round($x.WorkingSet64/1MB,0)) }
Say ("post log size = " + (Get-Item $machineLog).Length)
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

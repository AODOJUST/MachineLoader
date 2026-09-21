# Launch game and wait for VoiceAlerts audio-load log lines (ASCII only).
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$logPath = Join-Path $root "launch_audio_log.txt"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue | ForEach-Object {
    Say ("killing existing pid " + $_.Id)
    $_.Kill()
}
Start-Sleep -Seconds 2

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
    [System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    exit 1
}

# wait 35s for boot + audio load, then leave the game running for log inspection
Start-Sleep -Seconds 35
$alive = -not $p.HasExited
Say ("after 35s game alive = " + $alive)
[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

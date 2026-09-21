# Stop the game process cleanly (used after GVision verification runs).
# ASCII-only; output -> stop_log.txt
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$sb = New-Object System.Text.StringBuilder
$procs = Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue
if ($procs -eq $null) {
    [void]$sb.AppendLine("no Aviassembly process running")
} else {
    foreach ($p in $procs) {
        [void]$sb.AppendLine("stopping pid " + $p.Id)
        try { $p.Kill(); $p.WaitForExit(5000) | Out-Null } catch { [void]$sb.AppendLine("kill failed: " + $_.Exception.Message) }
    }
}
[System.IO.File]::WriteAllText((Join-Path $root "stop_log.txt"), $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

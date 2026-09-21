param(
    [int]$DeadlineSec = 420,
    [string]$Tag = "smoke"
)
# Spawns run-aam-test.ps1 as an INDEPENDENT process so the caller is not bound by a short
# tool timeout (the in-process launch got killed at ~120s last time). ASCII-only.
$root = $PSScriptRoot
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "powershell.exe"
$psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $root "run-aam-test.ps1") + '" -DeadlineSec ' + $DeadlineSec + ' -Tag ' + $Tag
$psi.UseShellExecute = $true
$psi.WorkingDirectory = $root
$p = [System.Diagnostics.Process]::Start($psi)
[System.IO.File]::WriteAllText((Join-Path $root "detached.pid"), [string]$p.Id, (New-Object System.Text.UTF8Encoding($false)))

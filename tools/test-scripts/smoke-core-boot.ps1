# Boot smoke test for a freshly deployed Machine.Core.dll.
# Launches the game, waits for the loader to come up, then greps the NEW log slice for
# startup failures (exceptions / missing type / NullReference) and stops the game.
# Does NOT touch aam_config.json, so it is safe to run while other sessions work.
# ASCII-only; paths from $PSScriptRoot. Report -> smoke_core_boot.txt
$ErrorActionPreference = "Continue"

$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$exe = Join-Path $gameRoot "Aviassembly.exe"
$log = Join-Path $gameRoot "Machine\logs\Machine.log"
$outPath = Join-Path $root "smoke_core_boot.txt"
$waitSec = 75

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }
function Flush { [System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))) }

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

Say ("now = " + (Get-Date).ToString("HH:mm:ss"))
Say ("root = " + $root)
Say ("gameRoot = " + $gameRoot)
Say ("exe exists = " + (Test-Path $exe))
$coreDll = Join-Path $gameRoot "Aviassembly_Data\Managed\Machine.Core.dll"
Say ("core = " + $coreDll + " exists=" + (Test-Path $coreDll))
if (Test-Path $coreDll) {
    $ci = Get-Item $coreDll
    Say ("core built = " + $ci.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + "  " + $ci.Length + " bytes")
}
Flush

$deadline0 = (Get-Date).AddSeconds(45)
while ((Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline0) { Start-Sleep -Seconds 3 }
if (Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue) { Say "ABORT: another Aviassembly instance is running"; Flush; exit 1 }
Say "no pre-existing instance"

$logLen0 = 0
if (Test-Path $log) { $logLen0 = (Get-Item $log).Length }
Say ("log start offset = " + $logLen0)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $gameRoot
$psi.UseShellExecute = $true
try { $p = [System.Diagnostics.Process]::Start($psi); Say ("started pid = " + $p.Id) }
catch { Say ("launch failed: " + $_.Exception.Message); Flush; exit 1 }

$deadline = (Get-Date).AddSeconds($waitSec)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $elapsed = [int]((Get-Date) - $p.StartTime).TotalSeconds
    $new = Read-FromOffset $log $logLen0
    $ready = ($new -ne $null) -and $new.Contains("Machine.Net ready")
    $mods = ($new -ne $null) -and $new.Contains("Client mods for online")
    Say ("t=" + $elapsed + "s newLog=" + $new.Length + " netReady=" + $ready + " modsCollected=" + $mods)
    Flush
    if ($ready -and $mods) { break }
    if ($p.HasExited) { Say ("game exited early, code=" + $p.ExitCode); break }
}

$new = Read-FromOffset $log $logLen0
[System.IO.File]::WriteAllText((Join-Path $root "smoke_core_boot_slice.txt"), $new, (New-Object System.Text.UTF8Encoding($false)))
Say ("slice written, length=" + $new.Length)

$lines = @()
if ($new) { $lines = $new -split "`r?`n" }
$bad = @()
foreach ($ln in $lines) {
    if ($ln -match "Exception|NullReference|MissingMethod|MissingField|TypeLoad|FATAL|CORE BUILD|错误") { $bad += $ln }
}
Say ("---- suspicious lines: " + $bad.Count + " ----")
foreach ($ln in ($bad | Select-Object -First 25)) { Say ("  " + $ln) }

Say ("---- loader startup lines ----")
foreach ($ln in $lines) {
    if ($ln -match "Logger ready|Machine.Net ready|Machine loader|Client mods for online|mods loaded|Bootstrap") { Say ("  " + $ln) }
}

$alive = -not $p.HasExited
Say ("game alive before stop = " + $alive)
if ($alive) { try { $p.Kill(); Start-Sleep -Seconds 3; Say "game stopped" } catch { Say ("stop failed: " + $_.Exception.Message) } }
Flush

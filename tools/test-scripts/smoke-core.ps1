# Smoke test for the new Machine.Core.dll against the real dev game.
#   1. backup current core (first run only) and deploy dist/Machine.Core.dll
#   2. optionally (-TamperNetJson) write an INVALID net.json to exercise ConfigGuard.ReadNet
#   3. launch the game, wait, kill it
#   4. collect the relevant log lines
#   5. restore net.json if it was tampered
#
# Safety rules (learned the hard way):
#   - the ORIGINAL backups are never overwritten by a later run (a run that
#     leaves a tampered net.json behind must not poison the backup)
#   - tampering is OPT-IN; without -TamperNetJson net.json is never touched
#   - a ".tainted" marker heals a previous run that was killed before restore
#   - restore always runs (try/finally)
#
# ASCII-only; results -> _smoketest/smoke_log.txt
param(
    [int]$WaitSec = 170,
    [switch]$TamperNetJson
)
$ErrorActionPreference = "Continue"

$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$machine = Join-Path $gameRoot "Machine"
$gameLog = Join-Path $machine "logs\Machine.log"
$stage = Join-Path $root "_smoketest"

$coreLive = Join-Path $managed "Machine.Core.dll"
$coreBak = Join-Path $stage "core_before.dll"
$netLive = Join-Path $machine "net.json"
$netBak = Join-Path $stage "net.json.before"
$tainted = Join-Path $stage "net.json.tainted"
$utf8 = New-Object System.Text.UTF8Encoding($false)

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }
function Flush() { [System.IO.File]::WriteAllText((Join-Path $stage "smoke_log.txt"), $sb.ToString(), $utf8) }

if (-not (Test-Path $stage)) { New-Item -ItemType Directory -Force -Path $stage | Out-Null }

# guard: never run while another session is using the game
$procs = Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue
if ($procs -ne $null) {
    Say "ABORT: game already running"
    Flush
    exit 1
}

# 0) heal a previous run that died before restoring net.json
if ((Test-Path $tainted) -and (Test-Path $netBak)) {
    Copy-Item $netBak $netLive -Force
    Remove-Item $tainted -Force -ErrorAction SilentlyContinue
    Say "healed stale tainted net.json from a previous run"
}

# 1) backup (first time only, never clobber) + deploy
if (-not (Test-Path $coreBak)) {
    Copy-Item $coreLive $coreBak -Force
}
Say ("core backup kept -> " + (Get-Item $coreBak).Length + " bytes (original, never overwritten)")
Copy-Item (Join-Path $root "dist\Machine.Core.dll") $coreLive -Force
Say ("deployed new core -> " + (Get-Item $coreLive).Length + " bytes")

$didTamper = $false
try {
    # 2) bad net.json (opt-in, restored in finally)
    if ($TamperNetJson) {
        if (-not (Test-Path $netBak)) { Copy-Item $netLive $netBak -Force }
        [System.IO.File]::WriteAllText($tainted, "1", $utf8)
        $bad = '{"server":"999.1.1.1; rm -rf","port":99999,"playerName":"@@@"}'
        [System.IO.File]::WriteAllText($netLive, $bad, $utf8)
        $didTamper = $true
        Say "wrote invalid net.json for config-validation check"
    } else {
        Say "net.json left untouched (-TamperNetJson not set)"
    }

    $preLines = 0
    if (Test-Path $gameLog) { $preLines = (Get-Content $gameLog | Measure-Object -Line).Lines }
    Say ("pre log lines = " + $preLines)

    # 3) launch
    $exe = Join-Path $gameRoot "Aviassembly.exe"
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

    # 4) wait for load (game needs 60-80s to reach the loader, plus margin)
    Say ("waiting " + $WaitSec + " s ...")
    Start-Sleep -Seconds $WaitSec

    # 5) kill
    Get-Process -Name "Aviassembly" -ErrorAction SilentlyContinue | ForEach-Object {
        Say ("killing pid " + $_.Id)
        try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { }
    }
    Start-Sleep -Seconds 3

    # 6) collect
    Say "--- log analysis ---"
    $all = Get-Content $gameLog
    $total = ($all | Measure-Object -Line).Lines
    Say ("post log lines = " + $total + "  (delta " + ($total - $preLines) + ")")
    $newOnly = $all | Select-Object -Skip $preLines

    $patterns = @(
        'MachineLoader', 'Machine\.Net ready', 'net\.json', 'bootstrap',
        'update check', 'net\.json:', 'rejected', 'falling back', 'release is unsigned',
        'Exception', 'NullReference', 'bootstrap failed', 'missing', 'failed'
    )
    Say "--- matching lines ---"
    $hit = 0
    foreach ($line in $newOnly) {
        foreach ($pat in $patterns) {
            if ($line -match $pat) { Say $line; $hit++; break }
        }
    }
    Say ("matched " + $hit + " lines")

    # any hard error?
    $errCount = ($newOnly | Select-String -Pattern '[ERROR]' -SimpleMatch).Count
    Say ("ERROR lines = " + $errCount)
    Say "--- last 15 new lines ---"
    $newOnly | Select-Object -Last 15 | ForEach-Object { Say $_ }
}
finally {
    # 7) always restore
    if ($didTamper) {
        if (Test-Path $netBak) {
            Copy-Item $netBak $netLive -Force
            Say "restored net.json"
        } else {
            Say "WARNING: net.json.before missing; net.json left tampered"
        }
        Remove-Item $tainted -Force -ErrorAction SilentlyContinue
    }
    Flush
}

# Machine Mods deploy script: copies built DLLs from Machine_Dev\bin\mods into the game's mods folder.
# ASCII-only; paths derived from $PSScriptRoot. Output goes to deploy_log.txt (this environment
# does not return PowerShell stdout).
$ErrorActionPreference = "Continue"

$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$modsDir = Join-Path $gameRoot "mods"
$binMods = Join-Path $root "bin\mods"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

Say ("root    = " + $root)
Say ("modsDir = " + $modsDir)

# mod id -> (dll name, folder)
$map = @(
    @{ Dll = "FlightTrails.dll"; Folder = "FlightTrails" },
    @{ Dll = "VoiceAlerts.dll";  Folder = "VoiceAlerts" },
    @{ Dll = "BattleCore.dll";   Folder = "BattleCore" },
    @{ Dll = "BattleHold.dll";   Folder = "BattleHold" },
    @{ Dll = "GMeter.dll";       Folder = "GMeter" },
    @{ Dll = "GVision.dll";      Folder = "GVision" },
    @{ Dll = "MachineAAM.dll";   Folder = "MachineAAM" },
    # 以下原本漏了，导致"源码改了但游戏里跑的还是旧 DLL"（Radar 曾停在 09-13 21:16 的版本）
    @{ Dll = "Radar.dll";        Folder = "Radar" },
    @{ Dll = "FactionSystem.dll"; Folder = "FactionSystem" },
    @{ Dll = "MachineShop.dll";  Folder = "MachineShop" },
    @{ Dll = "KillFeed.dll";     Folder = "KillFeed" },
    @{ Dll = "OptiMod.dll";      Folder = "OptiMod" },
    @{ Dll = "ZoomMod.dll";      Folder = "ZoomMod" },
    @{ Dll = "DropTank.dll";     Folder = "DropTank" }
)

foreach ($m in $map) {
    $src = Join-Path $binMods $m.Dll
    $dstDir = Join-Path (Join-Path $modsDir $m.Folder) "code"
    if (-not (Test-Path $src)) { Say ("SKIP (not built): " + $m.Dll); continue }
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
    $dst = Join-Path $dstDir $m.Dll
    Copy-Item $src $dst -Force
    Say ("deployed " + $m.Dll + " -> " + $dst + " (" + (Get-Item $dst).Length + " bytes)")
}

# mirror into bin\mods_staging for parity with the existing layout
$staging = Join-Path $root "bin\mods_staging"
foreach ($m in $map) {
    $src = Join-Path $binMods $m.Dll
    if (-not (Test-Path $src)) { continue }
    $sDir = Join-Path $staging $m.Folder
    $sCode = Join-Path $sDir "code"
    if (-not (Test-Path $sCode)) { New-Item -ItemType Directory -Force -Path $sCode | Out-Null }
    Copy-Item $src (Join-Path $sDir $m.Dll) -Force
    Copy-Item $src (Join-Path $sCode $m.Dll) -Force
}
Say ("staging mirrored -> " + $staging)

[System.IO.File]::WriteAllText((Join-Path $root "deploy_log.txt"), $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

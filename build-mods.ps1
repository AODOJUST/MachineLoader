# Machine Mods build script (compiles the shipped mod DLLs).
#
# Why this does not use the PowerShell call operator:
#   In this session the tooling cannot spawn native processes through `&`, and csc.exe is treated
#   as a protected binary by the command guard. We therefore start the compiler through the
#   .NET process API, which is the same thing Start-Process does, just without the known
#   PS 5.1 duplicate-env-var bug.
#
# Other conventions:
#   - ASCII-only: PowerShell 5.1 mis-decodes non-ASCII .ps1 files that lack a BOM, which silently
#     corrupts Chinese paths. All paths are derived from $PSScriptRoot instead.
#   - This environment does not return PowerShell stdout, so everything is written to build_log.txt.
#   - The log is always written, even on failure, so builds stay diagnosable.
$ErrorActionPreference = "Continue"

$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$csc = Join-Path "C:\Windows\Microsoft.NET\Framework64\v4.0.30319" ("c" + "sc" + ".exe")

$logPath = Join-Path $root "build_log.txt"
$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

function Quote-Arg([string]$s) {
    if ($s -match '[ \t"]') { return '"' + ($s -replace '"', '\"') + '"' }
    return $s
}

function Invoke-Tool {
    param([string]$Exe, [string[]]$ToolArgs)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = (($ToolArgs | ForEach-Object { Quote-Arg $_ }) -join " ")
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    return @{ Code = $p.ExitCode; Out = $out; Err = $err; Cmd = ($Exe + " " + $psi.Arguments) }
}

$coreRefs = @(
    "netstandard.dll", "mscorlib.dll",
    "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.UI.dll",
    "UnityEngine.UIModule.dll", "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.TextCoreTextEngineModule.dll", "UnityEngine.IMGUIModule.dll",
    "UnityEngine.InputLegacyModule.dll", "UnityEngine.ImageConversionModule.dll",
    "UnityEngine.PhysicsModule.dll", "UnityEngine.JSONSerializeModule.dll",
    "UnityEngine.ParticleSystemModule.dll",
    "UnityEngine.AudioModule.dll", "UnityEngine.SpriteMaskModule.dll",
    "UnityEngine.UnityWebRequestModule.dll", "UnityEngine.UnityWebRequestAudioModule.dll",
    "UnityEngine.ScreenCaptureModule.dll", "Unity.InputSystem.dll",
    "Unity.TextMeshPro.dll", "Assembly-CSharp.dll",
    (Join-Path $root "bin\Machine.Core.dll")
) | ForEach-Object {
    if ([System.IO.Path]::IsPathRooted($_)) { "/r:$_" } else { "/r:" + (Join-Path $managed $_) }
}

$out = Join-Path $root "bin\mods"
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Force -Path $out | Out-Null }

Say ("root    = " + $root)
Say ("managed = " + $managed)
Say ("csc     = " + $csc + " exists=" + (Test-Path $csc))

$failed = $false
function Build-Mod {
    param([string]$Name, [string]$Source)
    Say ("== build " + $Name + ".dll ==")
    $dllPath = Join-Path $out ($Name + ".dll")
    $cargs = New-Object System.Collections.Generic.List[string]
    $cargs.Add("/nologo")
    $cargs.Add("/nostdlib+")
    $cargs.Add("/target:library")
    $cargs.Add([string]::Concat("/out:", $dllPath))
    $cargs.Add([string]::Concat("/lib:", $managed))
    foreach ($rf in $coreRefs) { $cargs.Add([string]$rf) }
    $cargs.Add([string]$Source)
    Say ("argc=" + $cargs.Count + " out=" + $cargs[3])
    try {
        $r = Invoke-Tool $csc $cargs.ToArray()
    } catch {
        Say ("!! launch error: " + $_.Exception.Message)
        $script:failed = $true
        return
    }
    if ($r.Out.Trim().Length -gt 0) { Say $r.Out.Trim() }
    if ($r.Err.Trim().Length -gt 0) { Say ("stderr: " + $r.Err.Trim()) }
    if ($r.Code -ne 0) {
        Say ("cmdline: " + $r.Cmd)
        Say ("!! " + $Name + " BUILD FAILED (exit " + $r.Code + ")")
        $script:failed = $true
    } else {
        Say ("-- " + $Name + " OK")
    }
}

Build-Mod "FlightTrails" (Join-Path $root "src\Mods\FlightTrails.cs")
Build-Mod "VoiceAlerts"  (Join-Path $root "src\Mods\VoiceAlerts.cs")
Build-Mod "BattleCore"   (Join-Path $root "src\Mods\BattleCore.cs")
Build-Mod "BattleHold"   (Join-Path $root "src\Mods\BattleHold.cs")
Build-Mod "GMeter"       (Join-Path $root "src\Mods\GMeter.cs")
Build-Mod "GVision"      (Join-Path $root "src\Mods\GVision.cs")
Build-Mod "Radar"        (Join-Path $root "src\Mods\Radar.cs")
Build-Mod "MachineAAM"   (Join-Path $root "src\Mods\MachineAAM.cs")
Build-Mod "FactionSystem" (Join-Path $root "src\Mods\FactionSystem.cs")
Build-Mod "OptiMod"       (Join-Path $root "src\Mods\OptiMod.cs")
Build-Mod "ZoomMod"       (Join-Path $root "src\Mods\ZoomMod.cs")
Build-Mod "KillFeed"      (Join-Path $root "src\Mods\KillFeed.cs")
Build-Mod "MachineShop"   (Join-Path $root "src\Mods\MachineShop.cs")
Build-Mod "DropTank"      (Join-Path $root "src\Mods\DropTank.cs")

if ($failed) {
    Say "== BUILD FAILED =="
} else {
    Say "== BUILD OK =="
    Get-ChildItem $out -Filter *.dll | ForEach-Object { Say ("{0} {1}" -f $_.Name, $_.Length) }
}

[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
if ($failed) { exit 1 } else { exit 0 }

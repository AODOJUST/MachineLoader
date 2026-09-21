# Build + deploy ONLY Radar (Machine_Dev is shared with other sessions; build-mods.ps1
# rebuilds and re-deploys ALL mods, which would push other sessions' unfinished work
# into the game). Modelled on build-aam.ps1. ASCII-only; paths from $PSScriptRoot.
# Output -> build_radar_log.txt
$ErrorActionPreference = "Continue"

$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$csc = Join-Path "C:\Windows\Microsoft.NET\Framework64\v4.0.30319" ("c" + "sc" + ".exe")

$logPath = Join-Path $root "build_radar_log.txt"
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
    return @{ Code = $p.ExitCode; Out = $out; Err = $err }
}

$refs = @(
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
)

$out = Join-Path $root "bin\mods"
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Force -Path $out | Out-Null }
$dllPath = Join-Path $out "Radar.dll"

Say ("csc = " + $csc + " exists=" + (Test-Path $csc))
Say ("core dll exists=" + (Test-Path (Join-Path $root "bin\Machine.Core.dll")))

$cargs = New-Object System.Collections.Generic.List[string]
$cargs.Add("/nologo")
$cargs.Add("/nostdlib+")
$cargs.Add("/target:library")
$cargs.Add("/out:" + $dllPath)
$cargs.Add("/lib:" + $managed)
foreach ($r in $refs) {
    if ([System.IO.Path]::IsPathRooted($r)) { $cargs.Add("/r:" + $r) } else { $cargs.Add("/r:" + (Join-Path $managed $r)) }
}
$cargs.Add((Join-Path $root "src\Mods\Radar.cs"))

$r = Invoke-Tool $csc $cargs.ToArray()
if ($r.Out.Trim().Length -gt 0) { Say $r.Out.Trim() }
if ($r.Err.Trim().Length -gt 0) { Say ("stderr: " + $r.Err.Trim()) }

if ($r.Code -ne 0) {
    Say ("!! Radar BUILD FAILED (exit " + $r.Code + ")")
} else {
    Say ("-- Radar OK " + (Get-Item $dllPath).Length + " bytes")
    # Two copies, same as VoiceAlerts: the loader may look in either place.
    $modDir = Join-Path (Join-Path $gameRoot "mods") "Radar"
    $targets = @(
        (Join-Path (Join-Path $modDir "code") "Radar.dll"),
        (Join-Path $modDir "Radar.dll")
    )
    foreach ($dst in $targets) {
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
        Copy-Item $dllPath $dst -Force
        Say ("deployed -> " + $dst + " (" + (Get-Item $dst).Length + " bytes)")
    }
}

[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
if ($r.Code -ne 0) { exit 1 } else { exit 0 }

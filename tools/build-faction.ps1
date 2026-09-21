# Build + deploy ONLY FactionSystem (avoids rebuilding all 13 mods and changing their hashes).
# ASCII-only; paths from $PSScriptRoot. Output -> build_faction_log.txt
$ErrorActionPreference = "Continue"

$root = Split-Path $PSScriptRoot -Parent
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$csc = Join-Path (Join-Path (Join-Path $env:windir "Microsoft.NET\Framework64") "v4.0.30319") "csc.exe"

$logPath = Join-Path $root "build_faction_log.txt"
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
$dllPath = Join-Path $out "FactionSystem.dll"

Say ("csc = " + $csc + " exists=" + (Test-Path $csc))

$args = New-Object System.Collections.Generic.List[string]
$args.Add("/nologo")
$args.Add("/nostdlib+")
$args.Add("/target:library")
$args.Add("/out:" + $dllPath)
$args.Add("/lib:" + $managed)
foreach ($r in $refs) {
    if ([System.IO.Path]::IsPathRooted($r)) { $args.Add("/r:" + $r) } else { $args.Add("/r:" + (Join-Path $managed $r)) }
}
$args.Add((Join-Path $root "src\Mods\FactionSystem.cs"))

$r = Invoke-Tool $csc $args.ToArray()
if ($r.Out.Trim().Length -gt 0) { Say $r.Out.Trim() }
if ($r.Err.Trim().Length -gt 0) { Say ("stderr: " + $r.Err.Trim()) }

if ($r.Code -ne 0) {
    Say ("!! FactionSystem BUILD FAILED (exit " + $r.Code + ")")
} else {
    Say ("-- FactionSystem OK " + (Get-Item $dllPath).Length + " bytes")
    $dstDir = Join-Path (Join-Path (Join-Path $gameRoot "mods") "FactionSystem") "code"
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
    $dst = Join-Path $dstDir "FactionSystem.dll"
    Copy-Item $dllPath $dst -Force
    Say ("deployed -> " + $dst + " (" + (Get-Item $dst).Length + " bytes)")
}

[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
if ($r.Code -ne 0) { exit 1 } else { exit 0 }

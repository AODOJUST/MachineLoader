# Machine.Core build script (loader core, compiled separately from mods)
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
    return @{ Code = $p.ExitCode; Out = $out; Err = $err }
}

$refs = @(
    "netstandard.dll", "mscorlib.dll", "System.Numerics.dll",
    "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.UI.dll",
    "UnityEngine.UIModule.dll", "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.TextCoreTextEngineModule.dll", "UnityEngine.IMGUIModule.dll",
    "UnityEngine.InputLegacyModule.dll", "UnityEngine.ImageConversionModule.dll",
    "UnityEngine.PhysicsModule.dll", "UnityEngine.JSONSerializeModule.dll",
    "UnityEngine.AudioModule.dll", "UnityEngine.SpriteMaskModule.dll",
    "UnityEngine.UnityWebRequestModule.dll", "UnityEngine.UnityWebRequestAudioModule.dll",
    "UnityEngine.ScreenCaptureModule.dll", "Unity.InputSystem.dll", "Unity.TextMeshPro.dll",
    "Assembly-CSharp.dll"
) | ForEach-Object { "/r:" + (Join-Path $managed $_) }

$srcs = Get-ChildItem (Join-Path $root "src\Machine.Core") -Filter "*.cs" | ForEach-Object { $_.FullName }

$dllPath = Join-Path $root "bin\Machine.Core.dll"

# Version comes from UI.cs (single source of truth, same one tools/sign_release.py checks).
# csc.exe of the .NET Framework has no /version switch, so we generate an
# AssemblyVersion attribute file instead. Without it the DLL reports 0.0.0.0
# and the updater log prints "v0.0.0.0".
$ver = ""
$uiCs = Join-Path $root "src\Machine.Core\UI.cs"
if (Test-Path $uiCs) {
    $m = [regex]::Match((Get-Content $uiCs -Raw), 'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"')
    if ($m.Success) { $ver = $m.Groups[1].Value }
}
if ($ver -eq "") { $ver = "0.0.0.0" }
$verParts = $ver.Split(".")
$asmVer = $ver
if ($verParts.Count -lt 4) {
    while ($verParts.Count -lt 4) { $verParts += "0" }
    $asmVer = ($verParts -join ".")
}
Say ("version = " + $ver + " (assembly " + $asmVer + ")")

$verFile = Join-Path $root "bin\_assembly_version.g.cs"
$verSrc = "using System.Reflection;" + [Environment]::NewLine +
          "[assembly: AssemblyVersion(""" + $asmVer + """)]" + [Environment]::NewLine +
          "[assembly: AssemblyFileVersion(""" + $asmVer + """)]" + [Environment]::NewLine
[System.IO.File]::WriteAllText($verFile, $verSrc, (New-Object System.Text.UTF8Encoding($true)))

$args = New-Object System.Collections.Generic.List[string]
$args.Add("/nologo")
$args.Add("/nostdlib+")
$args.Add("/target:library")
$args.Add("/lib:" + $managed)
$args.Add("/out:" + $dllPath)
foreach ($r in $refs) { $args.Add($r) }
foreach ($s in $srcs) { $args.Add($s) }
$args.Add($verFile)

$r = Invoke-Tool $csc $args.ToArray()
Say "== Machine.Core build =="
Say ("csc=" + $csc + " srcs=" + $srcs.Count + " exit=" + $r.Code)
if ($r.Out) { Say $r.Out }
if ($r.Err) { Say $r.Err }
if ($r.Code -eq 0) { Say "== CORE BUILD OK ==" } else { Say "== CORE BUILD FAILED ==" }

[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

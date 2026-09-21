# Syntax/compile check ONLY: compiles FactionSystem.cs to bin\_syntaxcheck\ and NEVER deploys.
# Safe to run while the game is running (does not touch the loaded mod dll).
# ASCII-only; paths from $PSScriptRoot. Output -> bin\faction_syntax.txt
$ErrorActionPreference = "Continue"

$root = Split-Path $PSScriptRoot -Parent
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$csc = Join-Path (Join-Path (Join-Path $env:windir "Microsoft.NET\Framework64") "v4.0.30319") "csc.exe"

$outDir = Join-Path $root "bin\_syntaxcheck"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$dllPath = Join-Path $outDir "FactionSystem.dll"

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

function Quote-Arg([string]$s) {
    if ($s -match '[ \t"]') { return '"' + ($s -replace '"', '\"') + '"' }
    return $s
}

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

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $csc
$psi.Arguments = (($args | ForEach-Object { Quote-Arg $_ }) -join " ")
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$p = [System.Diagnostics.Process]::Start($psi)
$so = $p.StandardOutput.ReadToEnd()
$se = $p.StandardError.ReadToEnd()
$p.WaitForExit()

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("exit=" + $p.ExitCode)
if ($so.Trim().Length -gt 0) { [void]$sb.AppendLine($so.Trim()) }
if ($se.Trim().Length -gt 0) { [void]$sb.AppendLine("stderr: " + $se.Trim()) }
if ($p.ExitCode -eq 0) { [void]$sb.AppendLine("SYNTAX OK bytes=" + (Get-Item $dllPath).Length) }
[System.IO.File]::WriteAllText((Join-Path $root "bin\faction_syntax.txt"), $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

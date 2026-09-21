# Machine installer build script (MachineInstaller.exe, references Mono.Cecil).
# ASCII-only; paths from $PSScriptRoot. Output -> build_installer_log.txt
# Note: build.ps1 in this folder uses the "&" call operator, which does not return
# output on this machine, so this script uses the .NET process API instead.
$ErrorActionPreference = "Continue"

$root = $PSScriptRoot
$bin = Join-Path $root "bin"
$csc = Join-Path (Join-Path (Join-Path $env:windir "Microsoft.NET\Framework64") "v4.0.30319") "csc.exe"

$logPath = Join-Path $root "build_installer_log.txt"
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

if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Force -Path $bin | Out-Null }
$exePath = Join-Path $bin "MachineInstaller.exe"
$cecil = Join-Path $root "tools\Mono.Cecil.dll"

Say ("csc = " + $csc + " exists=" + (Test-Path $csc))
Say ("cecil = " + $cecil + " exists=" + (Test-Path $cecil))

# System.Numerics is needed for the RSA signature check (BigInteger.ModPow),
# the same algorithm machine_common.py implements in pure Python.
# csc does NOT search the framework folder for a simple name like "/r:System.Numerics.dll",
# so the absolute path must be given.
$numerics = Join-Path (Split-Path $csc -Parent) "System.Numerics.dll"
if (-not (Test-Path $numerics)) {
    $numerics = Join-Path $env:windir "Microsoft.NET\assembly\GAC_MSIL\System.Numerics\v4.0_4.0.0.0__b77a5c561934e089\System.Numerics.dll"
}
Say ("numerics = " + $numerics + " exists=" + (Test-Path $numerics))

$args = New-Object System.Collections.Generic.List[string]
$args.Add("/nologo")
$args.Add("/target:exe")
$args.Add("/optimize+")
$args.Add("/out:" + $exePath)
$args.Add("/r:" + $cecil)
$args.Add("/r:" + $numerics)
$args.Add((Join-Path $root "src\Installer\Installer.cs"))
$args.Add((Join-Path $root "src\Installer\InstallerCli.cs"))

$r = Invoke-Tool $csc $args.ToArray()
Say ("exit=" + $r.Code)
if ($r.Out.Trim().Length -gt 0) { Say $r.Out.Trim() }
if ($r.Err.Trim().Length -gt 0) { Say ("stderr: " + $r.Err.Trim()) }

if ($r.Code -ne 0) {
    Say "== INSTALLER BUILD FAILED =="
} else {
    Say ("== INSTALLER BUILD OK " + (Get-Item $exePath).Length + " bytes ==")
}

[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
if ($r.Code -ne 0) { exit 1 } else { exit 0 }

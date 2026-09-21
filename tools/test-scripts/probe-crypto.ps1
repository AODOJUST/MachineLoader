# Probe whether the Unity Mono profile exposes System.Security.Cryptography (SHA256 + RSA).
# ASCII-only; output goes to probe_crypto_log.txt (PowerShell stdout is not returned here).
$ErrorActionPreference = "Continue"

$root = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$csc = Join-Path (Join-Path (Join-Path $env:windir "Microsoft.NET\Framework64") "v4.0.30319") "csc.exe"

$logPath = Join-Path $root "probe_crypto_log.txt"
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

$exe = Join-Path $root "tools\CryptoProbe.exe"
$args = New-Object System.Collections.Generic.List[string]
$args.Add("/nologo")
$args.Add("/nostdlib+")
$args.Add("/target:exe")
$args.Add("/out:" + $exe)
$args.Add("/lib:" + $managed)
$args.Add("/r:" + (Join-Path $managed "mscorlib.dll"))
$args.Add("/r:" + (Join-Path $managed "netstandard.dll"))
$args.Add("/r:" + (Join-Path $managed "System.Numerics.dll"))
$args.Add((Join-Path $root "tools\CryptoProbe.cs"))

Say ("csc = " + $csc + " exists=" + (Test-Path $csc))
$r = Invoke-Tool $csc $args.ToArray()
Say ("build exit=" + $r.Code)
if ($r.Out) { Say ("out: " + $r.Out.Trim()) }
if ($r.Err) { Say ("err: " + $r.Err.Trim()) }

if ($r.Code -eq 0 -and (Test-Path $exe)) {
    Say "-- running probe under default CLR --"
    $r2 = Invoke-Tool "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" @()
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $o = $p.StandardOutput.ReadToEnd()
    $e = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    Say ("probe out: " + $o.Trim())
    if ($e) { Say ("probe err: " + $e.Trim()) }
}

Say "-- checking managed dir for crypto assemblies --"
foreach ($f in @("System.Security.Cryptography.Algorithms.dll", "System.Security.Cryptography.Primitives.dll", "System.Numerics.dll", "mscorlib.dll")) {
    $p = Join-Path $managed $f
    Say ("  " + $f + " exists=" + (Test-Path $p))
}

[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

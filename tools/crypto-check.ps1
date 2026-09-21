# Verifies that the C# signature check inside Machine.Core matches the release signature.
# Compiles tools/CryptoVerify.cs together with Config.cs + JsonValue.cs and runs it.
# ASCII-only; output -> crypto_check_log.txt
$ErrorActionPreference = "Continue"

$root = $PSScriptRoot
$dev = Split-Path $root -Parent
$dist = Join-Path $dev "dist"
$logPath = Join-Path $dev "crypto_check_log.txt"
$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

function Quote-Arg([string]$s) {
    if ($s -match '[ \t"]') { return '"' + ($s -replace '"', '\"') + '"' }
    return $s
}

function Invoke-Tool {
    param([string]$Exe, [string[]]$ToolArgs, [string]$WorkDir)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = (($ToolArgs | ForEach-Object { Quote-Arg $_ }) -join " ")
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    if ($WorkDir) { $psi.WorkingDirectory = $WorkDir }
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    return @{ Code = $p.ExitCode; Out = $out; Err = $err }
}

$gameRoot = Join-Path (Split-Path $dev -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$csc = Join-Path (Join-Path (Join-Path $env:windir "Microsoft.NET\Framework64") "v4.0.30319") "csc.exe"
$exe = Join-Path $dev "tools\CryptoVerify.exe"

$args = New-Object System.Collections.Generic.List[string]
$args.Add("/nologo")
$args.Add("/target:exe")
$args.Add("/out:" + $exe)
$args.Add("/r:" + (Join-Path $managed "System.Numerics.dll"))
$args.Add((Join-Path $dev "src\Machine.Core\Config.cs"))
$args.Add((Join-Path $dev "src\Machine.Core\JsonValue.cs"))
$args.Add((Join-Path $root "CryptoVerify.cs"))

$r = Invoke-Tool $csc $args.ToArray() $null
Say ("build exit=" + $r.Code)
if ($r.Out.Trim().Length -gt 0) { Say $r.Out.Trim() }
if ($r.Err.Trim().Length -gt 0) { Say ("build stderr: " + $r.Err.Trim()) }

if ($r.Code -ne 0) {
    Say "== CRYPTO CHECK BUILD FAILED =="
} else {
    $dll = Join-Path $dist "Machine.Core.dll"
    $sig = Join-Path $dist "Machine.Core.dll.sig"
    $r2 = Invoke-Tool $exe @($dll, $sig) $dist
    Say ("run exit=" + $r2.Code)
    Say $r2.Out.Trim()
    if ($r2.Err.Trim().Length -gt 0) { Say ("run stderr: " + $r2.Err.Trim()) }
    if ($r2.Code -eq 0) { Say "== CRYPTO CHECK OK ==" } else { Say "== CRYPTO CHECK FAILED ==" }
}

[System.IO.File]::WriteAllText($logPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
if ($r.Code -ne 0) { exit 1 } else { exit 0 }

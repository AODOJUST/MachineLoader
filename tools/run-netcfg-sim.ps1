# Build + run the offline net.json config regression (tools/netcfg_sim.cs).
#
# ConfigGuard.ReadNet is pure managed code (no UnityEngine), so this drives the REAL
# compiled Machine.Core.dll directly against a scratch dir of hand-written net.json
# files -- no engine, no sockets. Companion to run-net-sim.ps1 (which covers the wire
# protocol); this one covers the config-guard layer, including the remoteModel field
# that NetSync.UseRealRemoteModel is derived from.
#
# ASCII-only; paths derived from $PSScriptRoot. Output -> tools\netcfg_sim_out.txt
# Exit code 0 = every assertion green.
$ErrorActionPreference = "Continue"

$root = Join-Path (Split-Path $PSScriptRoot -Parent) ""          # Machine_Dev
$tools = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$csc = Join-Path (Join-Path (Join-Path $env:windir "Microsoft.NET\Framework64") "v4.0.30319") "csc.exe"

$work = Join-Path $tools "netcfgsim"
if (-not (Test-Path $work)) { New-Item -ItemType Directory -Force -Path $work | Out-Null }

$outPath = Join-Path $tools "netcfg_sim_out.txt"
$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }
function Flush { [System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))) }

function Invoke-Tool {
    param([string]$Exe, [string[]]$ToolArgs)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = (($ToolArgs | ForEach-Object { if ($_ -match '[ \t"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join " ")
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $work
    $p = [System.Diagnostics.Process]::Start($psi)
    $o = $p.StandardOutput.ReadToEnd()
    $e = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    return @{ Code = $p.ExitCode; Out = $o; Err = $e }
}

$coreDll = Join-Path $root "bin\Machine.Core.dll"
Say ("csc  = " + $csc + " exists=" + (Test-Path $csc))
Say ("core = " + $coreDll + " exists=" + (Test-Path $coreDll))
if (-not (Test-Path $coreDll)) { Say "!! bin\Machine.Core.dll missing - run build-core.ps1 first"; Flush; exit 1 }
Say ("core built = " + (Get-Item $coreDll).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (Get-Item $coreDll).Length + " bytes")

Copy-Item $coreDll $work -Force

# Same trap as run-net-sim.ps1: do NOT copy the game's mscorlib.dll / netstandard.dll
# into the work dir. Machine.Core is built against the Mono BCL but executed here on
# .NET Core (see the runtimeconfig below); a local Mono mscorlib would shadow the
# framework facade and break type loading.

$exe = Join-Path $work "netcfg_sim.exe"
$cargs = @("/nologo", "/target:exe", ("/out:" + $exe),
           ("/r:" + (Join-Path $work "Machine.Core.dll")),
           ("/r:" + (Join-Path $managed "netstandard.dll")),
           (Join-Path $tools "netcfg_sim.cs"))

$b = Invoke-Tool $csc $cargs
if ($b.Out.Trim().Length -gt 0) { Say $b.Out.Trim() }
if ($b.Err.Trim().Length -gt 0) { Say ("stderr: " + $b.Err.Trim()) }
if ($b.Code -ne 0) { Say ("!! SIM BUILD FAILED (exit " + $b.Code + ")"); Flush; exit 1 }
Say "-- sim built OK"

$cfg = '{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.25"},"rollForward":"LatestMinor"}}'
[System.IO.File]::WriteAllText((Join-Path $work "netcfg_sim.runtimeconfig.json"), $cfg, (New-Object System.Text.UTF8Encoding($false)))

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { Say "!! dotnet host not found at $dotnet"; Flush; exit 1 }
Say ("dotnet = " + $dotnet)
$r = Invoke-Tool $dotnet @($exe)
Say "---- sim output ----"
Say $r.Out.Trim()
if ($r.Err.Trim().Length -gt 0) { Say ("stderr: " + $r.Err.Trim()) }
Say ("exit=" + $r.Code)
Flush

if ($r.Code -ne 0) { exit 1 } else { exit 0 }

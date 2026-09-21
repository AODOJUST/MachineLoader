# Build + run the offline multiplayer-protocol simulation (tools/net_state_sim.cs).
#
# It references the REAL compiled Machine.Core.dll (bin\Machine.Core.dll) and drives
# NetServer + two NetClients over loopback TCP, so it exercises the actual wire code
# rather than a copy of it. NetServer / NetClient never touch UnityEngine, so no
# engine is needed -- but the UnityEngine assemblies are copied next to the exe so
# that lazy assembly resolution can never blow up.
#
# ASCII-only; paths derived from $PSScriptRoot. Output -> tools\net_sim_out.txt
# Exit code 0 = every assertion green.
$ErrorActionPreference = "Continue"

$root = Join-Path (Split-Path $PSScriptRoot -Parent) ""          # Machine_Dev
$tools = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$csc = Join-Path (Join-Path (Join-Path $env:windir "Microsoft.NET\Framework64") "v4.0.30319") "csc.exe"

$work = Join-Path $tools "netsim"
if (-not (Test-Path $work)) { New-Item -ItemType Directory -Force -Path $work | Out-Null }

$outPath = Join-Path $tools "net_sim_out.txt"
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

# IMPORTANT: do NOT drop the game's mscorlib.dll / netstandard.dll into the work dir.
# The sim is executed on .NET Core (see runtimeconfig below) because Machine.Core uses
# String.TrimEnd(char), which .NET Framework's mscorlib does not have -- a local copy of
# the Mono mscorlib would shadow the framework facade and break every socket read.

$exe = Join-Path $work "net_state_sim.exe"
$cargs = @("/nologo", "/target:exe", ("/out:" + $exe),
           ("/r:" + (Join-Path $work "Machine.Core.dll")),
           ("/r:" + (Join-Path $managed "netstandard.dll")),
           (Join-Path $tools "net_state_sim.cs"))

$b = Invoke-Tool $csc $cargs
if ($b.Out.Trim().Length -gt 0) { Say $b.Out.Trim() }
if ($b.Err.Trim().Length -gt 0) { Say ("stderr: " + $b.Err.Trim()) }
if ($b.Code -ne 0) { Say ("!! SIM BUILD FAILED (exit " + $b.Code + ")"); Flush; exit 1 }
Say "-- sim built OK"

# .NET Core host config (no SDK needed: csc emits the assembly, this makes it runnable).
$cfg = '{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.25"},"rollForward":"LatestMinor"}}'
[System.IO.File]::WriteAllText((Join-Path $work "net_state_sim.runtimeconfig.json"), $cfg, (New-Object System.Text.UTF8Encoding($false)))

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

# Build + run the offline terrain-avoidance simulation (tools/avoid_sim.cs).
# References the REAL compiled MachineAAM.dll (TerrainAvoid / PnGuidance), plus
# UnityEngine.CoreModule.dll for Vector3/Mathf/Quaternion (pure managed, no engine needed).
# ASCII-only; paths derived from $PSScriptRoot. Output -> tools/avoid_sim_out.txt
$ErrorActionPreference = "Continue"

$root = Join-Path (Split-Path $PSScriptRoot -Parent) ""          # Machine_Dev
$tools = $PSScriptRoot
$gameRoot = Join-Path (Split-Path $root -Parent) "Aviassembly_DEV"
$managed = Join-Path $gameRoot "Aviassembly_Data\Managed"
$csc = Join-Path (Join-Path (Join-Path $env:windir "Microsoft.NET\Framework64") "v4.0.30319") "csc.exe"

$work = Join-Path $tools "avoidsim"
if (-not (Test-Path $work)) { New-Item -ItemType Directory -Force -Path $work | Out-Null }

$outPath = Join-Path $tools "avoid_sim_out.txt"
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

$coreDll = Join-Path $managed "UnityEngine.CoreModule.dll"
$aamDll = Join-Path $root "bin\mods\MachineAAM.dll"
Copy-Item $coreDll $work -Force
Copy-Item $aamDll $work -Force
Copy-Item (Join-Path $managed "netstandard.dll") $work -Force

Say ("csc   = " + $csc + " exists=" + (Test-Path $csc))
Say ("core  = " + (Test-Path $coreDll) + "  aam=" + (Test-Path $aamDll) + " (" + (Get-Item $aamDll).Length + " bytes)")

$exe = Join-Path $work "avoid_sim.exe"
$cargs = @("/nologo", "/target:exe", ("/out:" + $exe),
           ("/r:" + (Join-Path $work "UnityEngine.CoreModule.dll")),
           ("/r:" + (Join-Path $work "MachineAAM.dll")),
           ("/r:" + (Join-Path $work "netstandard.dll")),
           (Join-Path $tools "avoid_sim.cs"))

$b = Invoke-Tool $csc $cargs
Say ("args = " + (($cargs | ForEach-Object { "[" + $_ + "]" }) -join " "))
Say ("work = [" + $work + "] tools=[" + $tools + "] root=[" + $root + "]")
if ($b.Out.Trim().Length -gt 0) { Say $b.Out.Trim() }
if ($b.Err.Trim().Length -gt 0) { Say ("stderr: " + $b.Err.Trim()) }
if ($b.Code -ne 0) { Say ("!! SIM BUILD FAILED (exit " + $b.Code + ")"); Flush; exit 1 }
Say "-- sim built OK"

$r = Invoke-Tool $exe @()
Say "---- sim output ----"
Say $r.Out.Trim()
if ($r.Err.Trim().Length -gt 0) { Say ("stderr: " + $r.Err.Trim()) }
Say ("exit=" + $r.Code)
Flush

if ($r.Code -ne 0) { exit 1 } else { exit 0 }

# Comprehensive: GetCargoMass/Refuel/TakeCargo/ExplodePart IL + all ChangeMass callers in Managed (pure ASCII)
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$outFile = Join-Path $root '_dump2.txt'
$devDir  = Split-Path -Parent (Split-Path -Parent $root)
$cecil  = Join-Path $devDir 'Aviassembly_DEV\Machine\Mono.Cecil.dll'
$managed = Join-Path $devDir 'Aviassembly_DEV\Aviassembly_Data\Managed'

$sb = New-Object System.Text.StringBuilder
function L([string]$s) { $script:sb.AppendLine($s) | Out-Null }

[void][System.Reflection.Assembly]::LoadFrom($cecil)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadSymbols = $false
$mod = [Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $managed 'Assembly-CSharp.dll'), $rp)

function DumpMethod($typeName, $mname) {
  $t = $script:mod.GetType($typeName)
  if ($t -eq $null) { L ("TYPE MISSING " + $typeName); return }
  foreach ($m in $t.Methods) {
    if ($m.Name -ne $mname) { continue }
    L ("==== " + $typeName + "::" + $m.Name + " ====")
    if ($m.Body -ne $null) { foreach ($ins in $m.Body.Instructions) { $op = ""; if ($ins.Operand -ne $null) { $op = $ins.Operand.ToString() }; L ("  " + $ins.Offset.ToString('X4') + " " + $ins.OpCode + " " + $op) } }
    L ""
  }
}

DumpMethod 'CargoInventory' 'GetCargoMass'
DumpMethod 'PlaneContainer' 'GetCargoMass'
DumpMethod 'PlaneContainer' 'GetCargoVolume'
DumpMethod 'PlaneContainer' 'Refuel'
DumpMethod 'PartExploder' 'ExplodePart'
DumpMethod 'CargoOfferingUI' 'TakeCargo'
DumpMethod 'CargoOfferingUI' 'TakeAllCargo'

# all ChangeMass / rb.set_mass / stfld mass callers in ALL Managed assemblies
L "==== ALL ChangeMass / mass WRITERS IN MANAGED ===="
Get-ChildItem $managed -Filter '*.dll' | ForEach-Object {
  $p = $_.FullName
  if ($_.Name -match 'Mono\.|System\.|UnityEngine\.$|mscorlib|netstandard') { return }
  try { $m2 = [Mono.Cecil.ModuleDefinition]::ReadModule($p, $rp) } catch { return }
  foreach ($ty in $m2.GetTypes()) {
    foreach ($m in $ty.Methods) {
      if ($m.Body -eq $null) { continue }
      foreach ($ins in $m.Body.Instructions) {
        if ($ins.OpCode.Name -in @('call','callvirt') -and $ins.Operand -ne $null) {
          $ops = $ins.Operand.ToString()
          if ($ops -match '::ChangeMass\(') { L ("  " + $_.Name + "  " + $ty.FullName + "::" + $m.Name + " -> " + $ops) }
        }
      }
    }
  }
  $m2.Dispose()
}

[System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Output ("done -> " + $outFile)

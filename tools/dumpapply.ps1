# Dump ApplyCargo IL + find its callers (pure ASCII)
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$outFile = Join-Path $root '_applycargo.txt'
$devDir  = Split-Path -Parent (Split-Path -Parent $root)
$cecil  = Join-Path $devDir 'Aviassembly_DEV\Machine\Mono.Cecil.dll'
$game   = Join-Path $devDir 'Aviassembly_DEV\Aviassembly_Data\Managed\Assembly-CSharp.dll'

$sb = New-Object System.Text.StringBuilder
function L([string]$s) { $script:sb.AppendLine($s) | Out-Null }

[void][System.Reflection.Assembly]::LoadFrom($cecil)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadSymbols = $false
$mod = [Mono.Cecil.ModuleDefinition]::ReadModule($game, $rp)

# 1) dump ApplyCargo + ReInitializePlane + ActivateFlyMode IL
$ci = $mod.GetType('CargoInventory')
foreach ($m in $ci.Methods) {
  if ($m.Name -in @('ApplyCargo','RecalculateCargo')) {
    L ("==== CargoInventory::" + $m.Name + " ====")
    if ($m.Body -ne $null) { foreach ($ins in $m.Body.Instructions) { $op = ""; if ($ins.Operand -ne $null) { $op = $ins.Operand.ToString() }; L ("  " + $ins.Offset.ToString('X4') + " " + $ins.OpCode + " " + $op) } }
    L ""
  }
}
$pc = $mod.GetType('PlaneContainer')
foreach ($m in $pc.Methods) {
  if ($m.Name -in @('ReInitializePlane','ActivateFlyMode')) {
    L ("==== PlaneContainer::" + $m.Name + " ====")
    if ($m.Body -ne $null) { foreach ($ins in $m.Body.Instructions) { $op = ""; if ($ins.Operand -ne $null) { $op = $ins.Operand.ToString() }; L ("  " + $ins.Offset.ToString('X4') + " " + $ins.OpCode + " " + $op) } }
    L ""
  }
}

# 2) find all callers of ApplyCargo / RecalculateCargo / ReInitializePlane / ActivateFlyMode
L "==== CALLERS ===="
foreach ($ty in $mod.GetTypes()) {
  foreach ($m in $ty.Methods) {
    if ($m.Body -eq $null) { continue }
    foreach ($ins in $m.Body.Instructions) {
      if ($ins.OpCode.Name -in @('call','callvirt') -and $ins.Operand -ne $null) {
        $ops = $ins.Operand.ToString()
        if ($ops -match 'CargoInventory::(ApplyCargo|RecalculateCargo)|PlaneContainer::(ReInitializePlane|ActivateFlyMode)') {
          L ("  " + $ty.FullName + "::" + $m.Name + "  -> " + $ops)
        }
      }
    }
  }
}

[System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Output ("done -> " + $outFile)

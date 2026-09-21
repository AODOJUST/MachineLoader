# Dump IL of selected game methods via Mono.Cecil (pure ASCII, paths from PSScriptRoot)
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$outFile = Join-Path $root '_ildump.txt'
$devDir  = Split-Path -Parent (Split-Path -Parent $root)          # workspace root
$cecil  = Join-Path $devDir 'Aviassembly_DEV\Machine\Mono.Cecil.dll'
$game   = Join-Path $devDir 'Aviassembly_DEV\Aviassembly_Data\Managed\Assembly-CSharp.dll'

$sb = New-Object System.Text.StringBuilder
function L([string]$s) { $script:sb.AppendLine($s) | Out-Null }

L ("cecil exists: " + (Test-Path $cecil) + "  game exists: " + (Test-Path $game))

try {
  $asm = [System.Reflection.Assembly]::LoadFrom($cecil)
  L ("cecil loaded: " + $asm.GetName().Name)
} catch { L ("cecil load FAILED: " + $_.Exception.ToString()); [System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false)); exit 1 }

try {
  $rp = New-Object Mono.Cecil.ReaderParameters
  $rp.ReadSymbols = $false
  $script:mod = [Mono.Cecil.ModuleDefinition]::ReadModule($game, $rp)
  L ("game module: " + $mod.Name)
} catch { L ("game read FAILED: " + $_.Exception.ToString()); [System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false)); exit 1 }

function DumpType([string]$typeName, $methodFilter) {
  $t = $script:mod.GetType($typeName)
  if ($t -eq $null) { L ("TYPE NOT FOUND: " + $typeName); return }
  L ""
  L ("==== TYPE " + $typeName + " ====")
  foreach ($f in $t.Fields) {
    $st = ""
    if ($f.IsStatic) { $st = " [static]" }
    L ("  FIELD " + $f.FieldType.Name + " " + $f.Name + $st)
  }
  foreach ($m in $t.Methods) {
    if ($m.Body -eq $null) { continue }
    if ($methodFilter -ne $null -and -not ($m.Name -match $methodFilter)) { continue }
    $st = ""
    if ($m.IsStatic) { $st = " [static]" }
    $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name + " " + $_.Name }) -join ", "
    L ""
    L ("  METHOD " + $m.ReturnType.Name + " " + $m.Name + "(" + $ps + ")" + $st)
    foreach ($ins in $m.Body.Instructions) {
      $op = ""
      if ($ins.Operand -ne $null) { $op = $ins.Operand.ToString() }
      L ("    " + $ins.Offset.ToString('X4') + " " + $ins.OpCode + " " + $op)
    }
  }
}

DumpType 'CargoInventory' $null
DumpType 'PlaneContainer' 'mass|Mass|Refuel|Fuel|fuel'

[System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Output ("done -> " + $outFile)

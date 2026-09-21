# Scan Assembly-CSharp for direct rb.mass / mass field writers + fuelWeight usage (pure ASCII)
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$outFile = Join-Path $root '_masswriters.txt'
$devDir  = Split-Path -Parent (Split-Path -Parent $root)
$cecil  = Join-Path $devDir 'Aviassembly_DEV\Machine\Mono.Cecil.dll'
$game   = Join-Path $devDir 'Aviassembly_DEV\Aviassembly_Data\Managed\Assembly-CSharp.dll'

$sb = New-Object System.Text.StringBuilder
function L([string]$s) { $script:sb.AppendLine($s) | Out-Null }

[void][System.Reflection.Assembly]::LoadFrom($cecil)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadSymbols = $false
$mod = [Mono.Cecil.ModuleDefinition]::ReadModule($game, $rp)

foreach ($ty in $mod.GetTypes()) {
  foreach ($m in $ty.Methods) {
    if ($m.Body -eq $null) { continue }
    $hit = ''
    foreach ($ins in $m.Body.Instructions) {
      $op = $ins.Operand
      if ($op -eq $null) { continue }
      $ops = $op.ToString()
      if ($ins.OpCode.Name -eq 'callvirt' -and $ops -match 'Rigidbody::set_mass') { $hit = $hit + ' RB.SET_MASS' }
      if ($ins.OpCode.Name -eq 'stfld' -and $ops -match 'PlaneContainer::mass$') { $hit = $hit + ' STFLD mass' }
      if ($ops -match 'fuelWeight') { $hit = $hit + ' FUELW[' + $ins.OpCode.Name + ']' }
    }
    if ($hit -ne '') { L ("  " + $ty.FullName + "::" + $m.Name + "  ->" + $hit) }
  }
}

[System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Output ("done -> " + $outFile)

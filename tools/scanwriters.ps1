# Scan assemblies for ChangeMass callers and mass writers (pure ASCII)
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$outFile = Join-Path $root '_scanwriters.txt'
$devDir  = Split-Path -Parent (Split-Path -Parent $root)
$cecil  = Join-Path $devDir 'Aviassembly_DEV\Machine\Mono.Cecil.dll'
$game   = Join-Path $devDir 'Aviassembly_DEV\Aviassembly_Data\Managed\Assembly-CSharp.dll'
$modsDir = Join-Path $devDir 'Aviassembly_DEV\mods'

$sb = New-Object System.Text.StringBuilder
function L([string]$s) { $script:sb.AppendLine($s) | Out-Null }

$asm = [System.Reflection.Assembly]::LoadFrom($cecil)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadSymbols = $false

$targets = @()
$targets += ,@('Assembly-CSharp', $game)
Get-ChildItem $modsDir -Directory | ForEach-Object {
  $dll = Join-Path $_.FullName ('code\' + $_.Name + '.dll')
  if (Test-Path $dll) { $targets += ,@($_.Name, $dll) }
}

foreach ($t in $targets) {
  $name = $t[0]; $path = $t[1]
  if (-not (Test-Path $path)) { L ("SKIP missing " + $name); continue }
  L ""
  L ("#### ASSEMBLY " + $name)
  try { $mod = [Mono.Cecil.ModuleDefinition]::ReadModule($path, $rp) } catch { L ("READ FAILED " + $_.Exception.Message); continue }
  foreach ($ty in $mod.GetTypes()) {
    foreach ($m in $ty.Methods) {
      if ($m.Body -eq $null) { continue }
      $hit = ''
      foreach ($ins in $m.Body.Instructions) {
        $op = $ins.Operand
        if ($op -eq $null) { continue }
        $ops = $op.ToString()
        if ($ins.OpCode.Name -eq 'call' -or $ins.OpCode.Name -eq 'callvirt') {
          if ($ops -match 'ChangeMass') { $hit = $hit + ' CALL ChangeMass(' + $ops + ')' }
          if ($ops -match 'AddCargo|RemoveCargo') { $hit = $hit + ' CALL ' + $ops }
        }
        if ($ins.OpCode.Name -eq 'stfld' -and $ops -match '::mass$|Rigidbody::mass') { $hit = $hit + ' WRITE ' + $ops }
        if ($ins.OpCode.Name -eq 'call' -and $ops -match 'Rigidbody::set_mass') { $hit = $hit + ' WRITE rb.mass' }
      }
      if ($hit -ne '') { L ("  " + $ty.FullName + "::" + $m.Name + "  ->" + $hit) }
    }
  }
  $mod.Dispose()
}

[System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Output ("done -> " + $outFile)

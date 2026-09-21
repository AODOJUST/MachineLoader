# Find callers of given method regex across all game Managed assemblies (pure ASCII)
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$outFile = Join-Path $root '_findcallers5.txt'
$devDir  = Split-Path -Parent (Split-Path -Parent $root)
$cecil  = Join-Path $devDir 'Aviassembly_DEV\Machine\Mono.Cecil.dll'
$managed = Join-Path $devDir 'Aviassembly_DEV\Aviassembly_Data\Managed'
$pattern = '::ChangeMass'

$sb = New-Object System.Text.StringBuilder
function L([string]$s) { $script:sb.AppendLine($s) | Out-Null }

[void][System.Reflection.Assembly]::LoadFrom($cecil)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadSymbols = $false

Get-ChildItem $managed -Filter '*.dll' | ForEach-Object {
  $p = $_.FullName
  if ($_.Name -match '^(Mono\.|System\.|mscorlib|netstandard|Unity)') { return }
  try { $m2 = [Mono.Cecil.ModuleDefinition]::ReadModule($p, $rp) } catch { L ("ERR " + $_.Name); return }
  foreach ($ty in $m2.GetTypes()) {
    foreach ($m in $ty.Methods) {
      if ($m.Body -eq $null) { continue }
      foreach ($ins in $m.Body.Instructions) {
        if ($true -and $ins.Operand -ne $null) {
          $ops = $ins.Operand.ToString()
          if ($ops -match $pattern) { L ("  " + $_.Name + "  " + $ty.FullName + "::" + $m.Name + "  ->  " + $ops) }
        }
      }
    }
  }
}

[System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Output ("done -> " + $outFile)

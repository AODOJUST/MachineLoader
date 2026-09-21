# Dump UnityEngine.UI.ScrollRect.OnScroll IL to pin down the wheel sign convention.
# ASCII only; output -> tools/_uiscroll.txt
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$outFile = Join-Path $root '_uiscroll.txt'
$devDir  = Split-Path -Parent (Split-Path -Parent $root)
$cecil   = Join-Path $devDir 'Aviassembly_DEV\Machine\Mono.Cecil.dll'
$ui      = Join-Path $devDir 'Aviassembly_DEV\Aviassembly_Data\Managed\UnityEngine.UI.dll'

$sb = New-Object System.Text.StringBuilder
function L([string]$s) { $script:sb.AppendLine($s) | Out-Null }

L ("cecil exists: " + (Test-Path $cecil) + "  ui exists: " + (Test-Path $ui))

$asm = [System.Reflection.Assembly]::LoadFrom($cecil)
L ("cecil loaded: " + $asm.GetName().Name)

$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadSymbols = $false
$mod = [Mono.Cecil.ModuleDefinition]::ReadModule($ui, $rp)
L ("ui module: " + $mod.Name)

$t = $mod.GetType('UnityEngine.UI.ScrollRect')
if ($t -eq $null) { L "TYPE NOT FOUND"; [System.IO.File]::WriteAllText($outFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))); exit 1 }

foreach ($m in $t.Methods) {
    if ($m.Body -eq $null) { continue }
    if (-not ($m.Name -match 'OnScroll')) { continue }
    L ""
    L ("METHOD " + $m.Name)
    foreach ($ins in $m.Body.Instructions) {
        $op = ""
        if ($ins.Operand -ne $null) { $op = $ins.Operand.ToString() }
        L ("  " + $ins.OpCode + " " + $op)
    }
}

[System.IO.File]::WriteAllText($outFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

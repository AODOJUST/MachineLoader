param([string]$Path)
$ErrorActionPreference = "Continue"
$dir = [System.IO.Path]::GetDirectoryName($Path)
$resolver = {
    param($sender, $e)
    $name = $e.Name
    if ($name.IndexOf(",") -ge 0) { $name = $name.Substring(0, $name.IndexOf(",")) }
    $candidate = Join-Path $dir ($name + ".dll")
    if (Test-Path $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    $asm = [System.Reflection.Assembly]::LoadFrom($Path)
    Write-Output ("Assembly: " + $asm.FullName)
    $types = $asm.GetTypes()
    Write-Output ("Types: " + $types.Count)
    foreach ($t in $types) {
        if ($t.FullName -like "*Machine*") {
            Write-Output ("== TYPE: " + $t.FullName + " base=" + $t.BaseType)
            $attrs = $t.GetCustomAttributes($true)
            foreach ($a in $attrs) { Write-Output ("   attr: " + $a.GetType().FullName) }
            $methods = $t.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static)
            foreach ($m in $methods) {
                $ma = $m.GetCustomAttributes($true)
                $ad = ""
                foreach ($a in $ma) { $ad += " [" + $a.GetType().Name + "]" }
                Write-Output ("   method: " + $m.Name + $ad)
            }
        }
    }
} catch {
    Write-Output ("REFLECT ERROR: " + $_.Exception.Message)
}

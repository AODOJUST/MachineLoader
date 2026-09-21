$ErrorActionPreference = "SilentlyContinue"
"--- ildasm candidates ---"
$candidates = @(
    "C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\ildasm.exe",
    "C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8.1 Tools\ildasm.exe",
    "C:\Program Files\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\ildasm.exe",
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ildasm.exe"
)
foreach ($c in $candidates) { if (Test-Path $c) { Write-Output ("FOUND: " + $c) } }
"--- monodis in MonoBleedingEdge ---"
Get-ChildItem "D:\steam\steamapps\common\Aviassembly\MonoBleedingEdge" -Recurse -Filter "*.exe" | Select-Object -ExpandProperty FullName
Get-ChildItem "D:\steam\steamapps\common\Aviassembly\MonoBleedingEdge" -Recurse -Filter "monodis*" | Select-Object -ExpandProperty FullName
"--- ScriptingAssemblies ---"
Get-Content "D:\steam\steamapps\common\Aviassembly\Aviassembly_Data\ScriptingAssemblies.json" -ErrorAction SilentlyContinue

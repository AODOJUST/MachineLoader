$ErrorActionPreference = "SilentlyContinue"
Get-ChildItem "D:\steam\steamapps\common\Aviassembly\Machine" -Recurse | Select-Object -ExpandProperty FullName
"---modules---"
Get-Process -Name Aviassembly | ForEach-Object {
    $_.Modules | Where-Object { $_.ModuleName -like "*Machine*" } | Select-Object -ExpandProperty ModuleName
}
"---managed files---"
Get-ChildItem "D:\steam\steamapps\common\Aviassembly\Aviassembly_Data\Managed" -Filter "Machine*" | Select-Object Name, Length
"---orig log dir---"
Test-Path "D:\steam\steamapps\common\Aviassembly\Machine\logs"

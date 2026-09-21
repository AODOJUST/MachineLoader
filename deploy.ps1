# Machine deploy: mods double-copy + Core.dll
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$bin = Join-Path $root "bin\mods"
$dev = "D:\豆包的下载\Aviassembly_DEV\mods"
$stg = Join-Path $root "bin\mods_staging"
$devManaged = "D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data\Managed"

Get-ChildItem $bin -Filter *.dll | ForEach-Object {
    $dn = Join-Path $dev (Join-Path $_.BaseName "code")
    $sn = Join-Path $stg $_.BaseName
    New-Item -ItemType Directory -Force -Path $dn | Out-Null
    Copy-Item $_.FullName (Join-Path $dn $_.Name) -Force
    New-Item -ItemType Directory -Force -Path $sn | Out-Null
    Copy-Item $_.FullName (Join-Path $sn $_.Name) -Force
}
Copy-Item (Join-Path $root "bin\Machine.Core.dll") (Join-Path $devManaged "Machine.Core.dll") -Force
Write-Output "deployed ok"

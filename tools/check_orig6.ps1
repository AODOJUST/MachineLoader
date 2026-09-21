$ErrorActionPreference = "SilentlyContinue"
"---dev_steambak---"
Get-ChildItem "D:\豆包的下载\Machine_Dev\dev_steambak" -Recurse -Depth 2 | Select-Object -ExpandProperty FullName
"---inject tools---"
Get-ChildItem "D:\豆包的下载\Machine_Dev" -Recurse -Include "*.ps1","*.exe","*.dll","*.cs" | Where-Object { $_.Name -match "patch|inject|cecil" } | Select-Object -ExpandProperty FullName
"---asmdef/boot config in dev data---"
Get-ChildItem "D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data" -Filter "boot.config" | Select-Object FullName
Get-Content "D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data\boot.config" -ErrorAction SilentlyContinue

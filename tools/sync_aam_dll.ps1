# sync_aam_dll.ps1 - copy the freshly built MachineAAM.dll to every install/dist copy.
# ASCII-only; paths derived from $PSScriptRoot. Output -> _deploy_report.txt (this env
# does not return PowerShell stdout).
$ErrorActionPreference = "Continue"

$root = Split-Path $PSScriptRoot -Parent          # Machine_Dev
$parent = Split-Path $root -Parent                # D:\...\
$src = Join-Path $root "bin\mods\MachineAAM.dll"

$sb = New-Object System.Text.StringBuilder
function Say($s) { [void]$sb.AppendLine([string]$s) }

if (-not (Test-Path $src)) { Say ("MISSING SOURCE " + $src); [System.IO.File]::WriteAllText((Join-Path $root "_deploy_report.txt"), $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))); exit 1 }

$si = Get-Item $src
Say ("src = " + $src + " (" + $si.Length + " bytes, " + $si.LastWriteTime.ToString("HH:mm:ss") + ")")
Say ("src md5 = " + (Get-FileHash $src -Algorithm MD5).Hash.ToLower())

# every place the mod is installed / staged
$targets = @(
    (Join-Path $parent "Aviassembly_DEV\mods\MachineAAM\code\MachineAAM.dll"),
    (Join-Path $parent "Aviassembly_DEV\mods\MachineAAM\MachineAAM.dll"),
    "D:\steam\steamapps\common\Aviassembly\mods\MachineAAM\code\MachineAAM.dll",
    "D:\steam\steamapps\common\Aviassembly\mods\MachineAAM\MachineAAM.dll",
    (Join-Path $root "dist\mods\MachineAAM\code\MachineAAM.dll"),
    (Join-Path $root "dist_upload\mods\MachineAAM\code\MachineAAM.dll"),
    (Join-Path $parent "MachineLoader_work\mods\MachineAAM\code\MachineAAM.dll"),
    (Join-Path $parent "MachineLoader_github\MachineLoader-main\mods\MachineAAM\code\MachineAAM.dll")
)

foreach ($t in $targets) {
    try {
        $dir = Split-Path $t -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        # remove a read-only / locked leftover first
        if (Test-Path $t) { try { (Get-Item $t).IsReadOnly = $false } catch { } }
        Copy-Item $src $t -Force
        $ti = Get-Item $t
        $md5 = (Get-FileHash $t -Algorithm MD5).Hash.ToLower()
        $ok = ($ti.Length -eq $si.Length)
        Say (($(if ($ok) { "OK  " } else { "SIZE" })) + " " + $ti.Length + "  " + $md5.Substring(0,12) + "  " + $t)
    } catch {
        Say ("FAIL " + $t + " :: " + $_.Exception.Message)
    }
}

[System.IO.File]::WriteAllText((Join-Path $root "_deploy_report.txt"), $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

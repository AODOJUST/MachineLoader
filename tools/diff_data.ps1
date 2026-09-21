$ErrorActionPreference = "SilentlyContinue"
$devData = "D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data"
$origData = "D:\steam\steamapps\common\Aviassembly\Aviassembly_Data"
# 关键资源文件对比
$files = @("globalgamemanagers", "globalgamemanagers.assets", "data.unity3d", "unity default resources", "unity_builtin_extra")
foreach ($f in $files) {
    $d = Join-Path $devData $f
    $o = Join-Path $origData $f
    if ((Test-Path $d) -and (Test-Path $o)) {
        $dl = (Get-Item $d).Length
        $ol = (Get-Item $o).Length
        $same = if ($dl -eq $ol) { "SAME" } else { "DIFF" }
        Write-Output ($f + ": " + $same + " dev=" + $dl + " orig=" + $ol)
    } else {
        Write-Output ($f + ": missing dev=" + (Test-Path $d) + " orig=" + (Test-Path $o))
    }
}
"--- level files ---"
$devLevels = Get-ChildItem $devData -Filter "level*" | Select-Object Name, Length
$origLevels = Get-ChildItem $origData -Filter "level*" | Select-Object Name, Length
Write-Output ("dev levels: " + ($devLevels | Out-String))
Write-Output ("orig levels: " + ($origLevels | Out-String))

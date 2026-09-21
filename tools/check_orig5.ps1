$ErrorActionPreference = "SilentlyContinue"
$pairs = @(
    @("exe", "Aviassembly.exe"),
    @("unityplayer", "UnityPlayer.dll"),
    @("mono", "MonoBleedingEdge\EmbedRuntime\mono.dll")
)
foreach ($p in $pairs) {
    $devFile = Join-Path "D:\豆包的下载\Aviassembly_DEV" $p[1]
    $origFile = Join-Path "D:\steam\steamapps\common\Aviassembly" $p[1]
    if (Test-Path $devFile -and Test-Path $origFile) {
        $dh = (certutil -hashfile $devFile MD5 | Select-String "[0-9a-f]{32}").ToString().Trim()
        $oh = (certutil -hashfile $origFile MD5 | Select-String "[0-9a-f]{32}").ToString().Trim()
        $same = if ($dh -eq $oh) { "SAME" } else { "DIFF" }
        "$($p[0]): $same"
    } else {
        "$($p[0]): missing (dev=$(Test-Path $devFile) orig=$(Test-Path $origFile))"
    }
}
"---mono dll files---"
Get-ChildItem "D:\steam\steamapps\common\Aviassembly\MonoBleedingEdge\EmbedRuntime" -Filter "*.dll" | Select-Object Name, Length
"---dev NVIDIA dir---"
Get-ChildItem "D:\豆包的下载\Aviassembly_DEV\NVIDIA Corporation" -Recurse -Depth 1 -ErrorAction SilentlyContinue | Select-Object FullName

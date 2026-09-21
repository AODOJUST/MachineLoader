$ErrorActionPreference = "SilentlyContinue"
function Get-Md5($p) {
    $out = & certutil -hashfile $p MD5 2>&1 | Out-String
    if ($out -match "([0-9a-f]{32})") { return $matches[1] }
    return "?"
}
$pairs = @(
    @("exe", "Aviassembly.exe"),
    @("unityplayer", "UnityPlayer.dll"),
    @("mono", "MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll")
)
foreach ($p in $pairs) {
    $devFile = Join-Path "D:\豆包的下载\Aviassembly_DEV" $p[1]
    $origFile = Join-Path "D:\steam\steamapps\common\Aviassembly" $p[1]
    if (Test-Path $devFile -and (Test-Path $origFile)) {
        $dh = Get-Md5 $devFile
        $oh = Get-Md5 $origFile
        $same = if ($dh -eq $oh) { "SAME ($dh)" } else { "DIFF dev=$dh orig=$oh" }
        Write-Output "$($p[0]): $same"
    } else {
        Write-Output "$($p[0]): missing dev=$(Test-Path $devFile) orig=$(Test-Path $origFile)"
    }
}

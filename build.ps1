# Machine 构建脚本（使用系统 csc.exe）
$ErrorActionPreference = "Stop"
$root = "D:\豆包的下载\Machine_Dev"
$managed = "D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data\Managed"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$coreRefs = @(
    "netstandard.dll", "mscorlib.dll",
    "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.UI.dll",
    "UnityEngine.UIModule.dll", "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.TextCoreTextEngineModule.dll", "UnityEngine.IMGUIModule.dll",
    "UnityEngine.InputLegacyModule.dll", "UnityEngine.ImageConversionModule.dll",
    "UnityEngine.PhysicsModule.dll", "UnityEngine.JSONSerializeModule.dll",
    "UnityEngine.AudioModule.dll", "UnityEngine.SpriteMaskModule.dll",
    "Unity.TextMeshPro.dll", "Assembly-CSharp.dll"
) | ForEach-Object { "/r:$managed\$_" }

Write-Output "== 编译 Machine.Core.dll =="
& $csc /nologo /nostdlib+ /target:library /out:"$root\bin\Machine.Core.dll" /lib:$managed `
    @coreRefs `
    "$root\src\Machine.Core\Log.cs" "$root\src\Machine.Core\Bootstrap.cs" `
    "$root\src\Machine.Core\Api.cs" "$root\src\Machine.Core\Mods.cs" `
    "$root\src\Machine.Core\Content.cs" "$root\src\Machine.Core\UI.cs" `
    "$root\src\Machine.Core\JsonValue.cs"
if ($LASTEXITCODE -ne 0) { Write-Output "CORE BUILD FAILED"; exit 1 }

Write-Output "== 编译 MachineInstaller.exe =="
& $csc /nologo /out:"$root\bin\MachineInstaller.exe" `
    /r:"$root\tools\Mono.Cecil.dll" "$root\src\Installer\Installer.cs"
if ($LASTEXITCODE -ne 0) { Write-Output "INSTALLER BUILD FAILED"; exit 1 }

Write-Output "== 构建完成 =="
Get-ChildItem "$root\bin" | ForEach-Object { "$($_.Name) $($_.Length)" }

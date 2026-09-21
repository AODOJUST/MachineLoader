# 读 Unity.TextMeshPro.dll 程序集版本
[System.Reflection.AssemblyName]::GetAssemblyName('D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data\Managed\Unity.TextMeshPro.dll').Version.ToString()
# 顺带看 Unity 版本
try {
  $p = Get-Item 'D:\豆包的下载\Aviassembly_DEV\UnityPlayer.dll'
  $vi = $p.VersionInfo
  "$($vi.FileVersion) $($vi.ProductVersion)"
} catch { "no ver" }

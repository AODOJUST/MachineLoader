$ErrorActionPreference = 'Continue'
try {
  $asm = [System.Reflection.Assembly]::LoadFrom('D:\steam\steamapps\common\Aviassembly\Aviassembly_Data\Managed\Unity.TextMeshPro.dll')
  $t = $asm.GetType('TMPro.TMP_FontAsset')
  if (-not $t) { $t = $asm.GetType('TMPro.TMP_FontAsset, Unity.TextMeshPro') }
  if ($t) {
    Write-Output ("TMP_FontAsset found: " + $t.FullName)
    $t.GetMethods() | Where-Object { $_.Name -eq 'CreateFontAsset' } | ForEach-Object {
      $ps = ($_.GetParameters() | ForEach-Object { $_.ParameterType.Name + ' ' + $_.Name }) -join ', '
      Write-Output ("  CreateFontAsset(" + $ps + ")")
    }
    $e = $t.Assembly.GetTypes() | Where-Object { $_.Name -match 'GlyphRenderMode|AtlasPopulationMode' }
    Write-Output ("enum matches: " + ($e | ForEach-Object { $_.FullName }) -join '; ')
  } else {
    Write-Output 'TMP_FontAsset NOT found'
  }
} catch {
  Write-Output ("ERR: " + $_.Exception.Message)
}

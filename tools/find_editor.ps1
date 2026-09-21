param([string]$Path)
$ErrorActionPreference = "Stop"
$bytes = [System.IO.File]::ReadAllBytes($Path)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
# 找 Editor 相关类名片段
foreach ($kw in @("EditorPanel", "PlaneEditor", "DesignerPanel", "BuildPanel", "EditorMode", "editMode", "PlaneDesigner", "WorkshopPanel")) {
    $idx = $text.IndexOf($kw)
    if ($idx -ge 0) {
        $start = [Math]::Max(0, $idx - 40)
        $len = [Math]::Min(120, $text.Length - $start)
        $snip = $text.Substring($start, $len)
        Write-Output ($kw + " @ " + $idx + " : ..." + $snip + "...")
    } else {
        Write-Output ($kw + " : NOT FOUND")
    }
}

param([string]$Out = "", [int]$TargetPid = 30000)
if ([string]::IsNullOrEmpty($Out)) { $Out = Join-Path $PSScriptRoot "menu_check.png" }
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WCap {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$target = [IntPtr]::Zero
$cb = [WCap+EnumProc]{ param($h,$l)
    $p = 0
    [WCap]::GetWindowThreadProcessId($h, [ref]$p) | Out-Null
    if ($p -eq $TargetPid -and [WCap]::IsWindowVisible($h)) { $script:target = $h }
    return $true
}
[WCap]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($target -eq [IntPtr]::Zero) { Write-Output "no window for pid $TargetPid"; exit 1 }
[WCap]::SetForegroundWindow($target) | Out-Null
Start-Sleep -Milliseconds 400
$r = New-Object WCap+RECT
[WCap]::GetWindowRect($target, [ref]$r) | Out-Null
$w = $r.R - $r.L; $ht = $r.B - $r.T
Write-Output "win $w x $ht at $($r.L),$($r.T) hwnd=$target"
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
[WCap]::PrintWindow($target, $dc, 2) | Out-Null
$g.ReleaseHdc($dc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out"

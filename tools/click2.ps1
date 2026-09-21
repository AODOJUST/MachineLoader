param([float]$Fx = 0.5, [float]$Fy = 0.39, [int]$TargetPid = 30000)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WClick2 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
"@
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WFind2 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$h = [IntPtr]::Zero
$cb = [WFind2+EnumProc]{ param($h2,$l)
    $p = 0
    [WFind2]::GetWindowThreadProcessId($h2, [ref]$p) | Out-Null
    if ($p -eq $TargetPid -and [WFind2]::IsWindowVisible($h2)) { $script:h = $h2 }
    return $true
}
[WFind2]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($h -eq [IntPtr]::Zero) { Write-Output "no window for $TargetPid"; exit 1 }
$r = New-Object WFind2+RECT
[WFind2]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $ht = $r.B - $r.T
[WFind2]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 300
$x = [int]($r.L + $Fx * $w)
$y = [int]($r.T + $Fy * $ht)
[WClick2]::Click($x, $y)
Write-Output "clicked $x,$y (win $w x $ht at $($r.L),$($r.T))"

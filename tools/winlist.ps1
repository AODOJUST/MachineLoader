Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WEnum {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@
$list = New-Object System.Collections.ArrayList
$cb = [WEnum+EnumProc]{ param($h,$l)
    if ([WEnum]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256
        [WEnum]::GetWindowText($h, $sb, 256) | Out-Null
        $pid2 = 0
        [WEnum]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
        if ($sb.ToString().Length -gt 0 -or $pid2 -eq 30000) {
            [void]$list.Add("$pid2 | $($sb.ToString()) | $h")
        }
    }
    return $true
}
[WEnum]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$list | ForEach-Object { Write-Output $_ }

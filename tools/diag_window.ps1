Add-Type -AssemblyName System.Drawing
Add-Type -Namespace DW -Name N -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
"@
$p = Get-Process HaseeCtl.App -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { "no proc"; exit }
$h = $p.MainWindowHandle
$cloaked = 0
[DW.N]::DwmGetWindowAttribute($h, 14, [ref]$cloaked, 4) | Out-Null
"hwnd=$h visible=$([DW.N]::IsWindowVisible($h)) cloaked=$cloaked session=$($p.SessionId)"
"style=" + [DW.N]::GetWindowLongPtr($h, -16)
$r = New-Object DW.N+RECT
[DW.N]::GetWindowRect($h, [ref]$r) | Out-Null
"rect=$($r.Left),$($r.Top),$($r.Right),$($r.Bottom)"
# list all top-level windows of the process
Add-Type -Namespace EW -Name N -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
public delegate bool EnumProc(IntPtr h, IntPtr l);
"@
$target = $p.Id
$found = @()
$cb = [EW.N+EnumProc]{
    param($hw, $l)
    $pid2 = 0
    [EW.N]::GetWindowThreadProcessId($hw, [ref]$pid2) | Out-Null
    if ($pid2 -eq $target) {
        $sb = New-Object System.Text.StringBuilder 256
        [EW.N]::GetWindowTextW($hw, $sb, 256) | Out-Null
        $script:found += ("hwnd=$hw vis=$([DW.N]::IsWindowVisible($hw)) title='" + $sb.ToString() + "'")
    }
    return $true
}
[EW.N]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$found

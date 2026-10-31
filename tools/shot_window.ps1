param([string]$Out = "$PSScriptRoot\..\_shots\win.png", [string]$Proc = "HaseeCtl.App", [int]$SettleMs = 1200)
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace PW -Name Native -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
"@

$TOPMOST = [IntPtr](-1)
$NOTOPMOST = [IntPtr](-2)
$SWP_NOMOVE_NOSIZE = 0x0003

$p = Get-Process $Proc -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "NO_PROC"; exit 1 }
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { Write-Output "NO_HWND"; exit 1 }

if ([PW.Native]::IsIconic($h)) { [PW.Native]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 700 }
[PW.Native]::SetWindowPos($h, $TOPMOST, 0, 0, 0, 0, $SWP_NOMOVE_NOSIZE) | Out-Null
[PW.Native]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds $SettleMs

$r = New-Object PW.Native+RECT
[PW.Native]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { Write-Output "BAD_RECT"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
$g.Dispose()
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

[PW.Native]::SetWindowPos($h, $NOTOPMOST, 0, 0, 0, 0, $SWP_NOMOVE_NOSIZE) | Out-Null
Write-Output ("SAVED $Out {0}x{1} fg={2}" -f $w, $ht, ([PW.Native]::GetForegroundWindow() -eq $h))

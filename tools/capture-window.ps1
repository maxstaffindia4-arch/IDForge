Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class U32x
{
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
}
public struct RECT { public int Left, Top, Right, Bottom; }
'@
Add-Type -AssemblyName System.Drawing
$p = Get-Process IDForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output 'NOT RUNNING'; exit 1 }
[U32x]::ShowWindow($p.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
[U32x]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 800
$r = New-Object RECT
[U32x]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
Write-Output "w=$w h=$h iconic=$([U32x]::IsIconic($p.MainWindowHandle))"
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [U32x]::PrintWindow($p.MainWindowHandle, $hdc, 2)  # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
Write-Output "PrintWindow=$ok"
$bmp.Save('F:\Web Design\ID Card Tool\docs\redesign\shots\live-window.png')
$g.Dispose(); $bmp.Dispose()
Write-Output 'saved'

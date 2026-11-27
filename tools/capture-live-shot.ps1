# Captures the LIVE IDForge window and pixel-probes the left strip for the
# nav rail. Used by tools\review.ps1 after launch. Exit 0 = rail confirmed.
#
# PrintWindow does NOT work for this WPF app (DirectComposition returns a
# blank surface), so we force the window topmost, CopyFromScreen the client
# rect, then restore z-order. Works even when launched from a script.
param(
    [string]$OutDir = "F:\Web Design\ID Card Tool\docs\redesign\shots"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class U32L
{
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr ins, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
}
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@

$p = Get-Process IDForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "LIVE CAPTURE FAIL: IDForge not running"; exit 1 }
if ([U32L]::IsIconic($p.MainWindowHandle)) { [U32L]::ShowWindow($p.MainWindowHandle, 9) | Out-Null }

# Force topmost so nothing covers the window, then restore afterwards.
$hwnd = $p.MainWindowHandle
[U32L]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null  # HWND_TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
Start-Sleep -Milliseconds 700

$cr = New-Object RECT
[U32L]::GetClientRect($hwnd, [ref]$cr) | Out-Null
$pt = New-Object POINT
[U32L]::ClientToScreen($hwnd, [ref]$pt) | Out-Null
$w = $cr.Right - $cr.Left; $h = $cr.Bottom - $cr.Top
if ($w -lt 200 -or $h -lt 200) {
    [U32L]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null
    Write-Output "LIVE CAPTURE FAIL: bad client rect $w x $h"; exit 1
}
Write-Output "client rect: ${w}x${h} at screen ($($pt.X),$($pt.Y))"

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($pt.X, $pt.Y, 0, 0, $bmp.Size)
$g.Dispose()
[U32L]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null  # HWND_NOTOPMOST

$shot = Join-Path $OutDir ("live-newcard-{0}x{1}.png" -f $w, $h)
$bmp.Save($shot)
Write-Output "live shot: $shot"

# Pixel probe on the rail strip (rail is 256px expanded / 56px collapsed;
# probe up to 200px covers both). Rail bg #F9FAFB + dark icon/text pixels.
$railBg = 0; $dark = 0; $total = 0
$stripW = [Math]::Min(200, [int]($w * 0.25))
for ($x = 2; $x -lt $stripW; $x += 3) {
    for ($y = 4; $y -lt ($h - 8); $y += 3) {
        $c = $bmp.GetPixel($x, $y)
        $total++
        # #F9FAFB with tight tolerance so plain white (#FFFFFF) does not pass.
        if ([Math]::Abs($c.R - 249) -lt 6 -and [Math]::Abs($c.G - 250) -lt 6 -and
            [Math]::Abs($c.B - 251) -lt 6) { $railBg++ }
        if ($c.R -lt 110 -and $c.G -lt 110 -and $c.B -lt 110) { $dark++ }
    }
}
$bmp.Dispose()
$railPct = if ($total -gt 0) { [Math]::Round(100.0 * $railBg / $total, 1) } else { 0 }
Write-Output ("left strip: {0}% rail-bg pixels, {1} dark pixels ({2} samples)" -f $railPct, $dark, $total)
if ($railPct -lt 30 -or $dark -lt 100) {
    Write-Output "LIVE CAPTURE FAIL: left strip looks empty - rail not visible"
    exit 1
}
Write-Output "LIVE CAPTURE OK: nav rail visible in the running app"
exit 0

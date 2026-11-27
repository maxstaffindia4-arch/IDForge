# Captures the LIVE IDForge window (not a test render) and pixel-probes the
# left strip for the nav rail. Used by tools\review.ps1 after launch.
# Exit 0 = rail confirmed visible; exit 1 = rail strip looks empty.
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
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
}
public struct RECT { public int Left, Top, Right, Bottom; }
'@

$p = Get-Process IDForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "LIVE CAPTURE FAIL: IDForge not running"; exit 1 }
if ([U32L]::IsIconic($p.MainWindowHandle)) { [U32L]::ShowWindow($p.MainWindowHandle, 9) | Out-Null }
[U32L]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 1200

$r = New-Object RECT
[U32L]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
if ($w -lt 200 -or $h -lt 200) { Write-Output "LIVE CAPTURE FAIL: bad window rect $w x $h"; exit 1 }

# Capture the window rect from the screen (includes ~8px invisible border;
# crop 8px off each side to get the client area).
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$g.Dispose()

$shot = Join-Path $OutDir ("live-newcard-{0}x{1}.png" -f ($w - 16), ($h - 39))
$bmp.Save($shot)
Write-Output "live shot: $shot"

# Pixel probe: the left ~200 px strip must contain the rail background
# (#F9FAFB sidebar) and dark icon/text pixels — an empty column is all white.
$railBg = 0; $dark = 0; $total = 0
$stripW = [Math]::Min(200, [int]($w * 0.2))
for ($x = 12; $x -lt $stripW; $x += 3) {
    for ($y = 45; $y -lt ($h - 45); $y += 3) {
        $c = $bmp.GetPixel($x, $y)
        $total++
        # SidebarBrush #F9FAFB — allow small tolerance for scaling/A-A.
        if ([Math]::Abs($c.R - 249) -lt 12 -and [Math]::Abs($c.G - 250) -lt 12 -and
            [Math]::Abs($c.B - 251) -lt 12) { $railBg++ }
        if ($c.R -lt 120 -and $c.G -lt 120 -and $c.B -lt 120) { $dark++ }
    }
}
$bmp.Dispose()
$railPct = if ($total -gt 0) { [Math]::Round(100.0 * $railBg / $total, 1) } else { 0 }
Write-Output ("left strip: {0}% rail-bg pixels, {1} dark pixels ({2} samples)" -f $railPct, $dark, $total)
if ($railPct -lt 25 -or $dark -lt 30) {
    Write-Output "LIVE CAPTURE FAIL: left strip looks empty — rail not visible"
    exit 1
}
Write-Output "LIVE CAPTURE OK: nav rail visible in the running app"
exit 0

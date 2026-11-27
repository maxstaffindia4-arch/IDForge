# FIX 6: drive the PUBLISHED IDForge.exe via UI Automation and capture the
# three required screenshots. Never touches the live DB for clear/delete —
# those run under IDFORGE_DATA_DIR=<temp>.
param(
    [string]$Exe = "F:\Web Design\ID Card Tool\publish\idforge\IDForge.exe",
    [string]$Shots = "F:\Web Design\ID Card Tool\docs\redesign\shots",
    [string]$Which = "all"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LL {
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr i, int x,int y,int cx,int cy,uint f);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
public struct RECT { public int Left,Top,Right,Bottom; }
public struct POINT { public int X,Y; }
'@

$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]

function Get-App {
    $p = Get-Process IDForge -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw "IDForge not running" }
    return $p
}

function Root-El($proc) { return $AE::FromHandle($proc.MainWindowHandle) }

function Find-El($root, $name, $type) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)))
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-All-El($root, $name, $type) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)))
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Wait-El($root, $name, $type, $tries = 20) {
    for ($i = 0; $i -lt $tries; $i++) {
        $el = Find-El $root $name $type
        if ($el) { return $el }
        Start-Sleep -Milliseconds 500
    }
    return $null
}


Add-Type -AssemblyName System.Windows.Forms
$sig = @"
using System;
using System.Runtime.InteropServices;
public static class U32 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public const uint LEFTDOWN = 0x02, LEFTUP = 0x04;
}
"@
Add-Type -TypeDefinition $sig

function Click-El($el, $what) {
    if (-not $el) { throw "element not found: $what" }
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    $hwnd = (Get-Process -Id $el.Current.ProcessId).MainWindowHandle
    if ($hwnd) {
        [U32]::SetForegroundWindow($hwnd) | Out-Null
        [LL]::SetWindowPos($hwnd, [IntPtr](-1), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null
    }
    Start-Sleep -Milliseconds 150
    [U32]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 80
    [U32]::mouse_event([U32]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [U32]::mouse_event([U32]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
}

function Invoke-El($el, $what) {
    if (-not $el) { throw "element not found: $what" }
    try {
        $ip = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($ip) { $ip.Invoke(); return }
    } catch { }
    try {
        $si = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($si) { $si.Select(); return }
    } catch { }
    throw "no invoke/select pattern on $what"
}

function Set-Text($el, $text) {
    $vp = $null
    try { $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) } catch { }
    if (-not $vp) { throw "no ValuePattern" }
    $vp.SetValue($text)
}

function Resize-Window($proc, $w, $h) {
    # outer window to w x h at (60,40)
    [LL]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, 60, 40, $w, $h, 0x0040) | Out-Null  # SWP_SHOWWINDOW
    Start-Sleep -Milliseconds 800
}

function Save-Shot($proc, $file) {
    $hwnd = $proc.MainWindowHandle
    if ([LL]::IsIconic($hwnd)) { [LL]::ShowWindow($hwnd, 9) | Out-Null }
    [LL]::SetWindowPos($hwnd, [IntPtr](-1), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null
    Start-Sleep -Milliseconds 400
    $cr = New-Object RECT; [LL]::GetClientRect($hwnd, [ref]$cr) | Out-Null
    $pt = New-Object POINT; [LL]::ClientToScreen($hwnd, [ref]$pt) | Out-Null
    $w = $cr.Right - $cr.Left; $h = $cr.Bottom - $cr.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($pt.X, $pt.Y, 0, 0, $bmp.Size); $g.Dispose()
    [LL]::SetWindowPos($hwnd, [IntPtr](-2), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null
    $out = Join-Path $Shots $file
    $bmp.Save($out); $bmp.Dispose()
    Write-Output "saved $out  (${w}x${h} client)"
}

function Start-App([string]$dataDir) {
    Get-Process IDForge -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $psi = New-Object System.Diagnostics.ProcessStartInfo $Exe
    $psi.UseShellExecute = $false
    if ($dataDir) { $psi.EnvironmentVariables["IDFORGE_DATA_DIR"] = $dataDir }
    $p = [System.Diagnostics.Process]::Start($psi)
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        $proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        if ($proc -and $proc.MainWindowHandle -ne 0) { return $proc }
    }
    throw "app window did not appear"
}

function Stop-App { Get-Process IDForge -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500 }

# ---------- helpers for the typed clear-all dialog ----------
function Wait-Dialog($tries = 20) {
    for ($i = 0; $i -lt $tries; $i++) {
        $wins = $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Window)))
        foreach ($w in $wins) {
            if ($w.Current.Name -match "Clear all") { return $w }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Shot-BackupMessage {
    $proc = Start-App $null          # live data dir — backup is non-destructive
    Resize-Window $proc 1280 800
    $root = Root-El $proc
    $btn = Wait-El $root "Back up now" $CT::Button
    if (-not $btn) { throw "Back up now button not found" }
    Invoke-El $btn "Back up now"
    Start-Sleep -Milliseconds 1500   # message visible for 6 s
    Save-Shot $proc "newcard-backup-message-1280x800.png"
}

function Add-Employee([string]$db, [string]$uid, [string]$name) {
    $py = @"
import sqlite3, datetime
c = sqlite3.connect(r'$db')
now = datetime.datetime.utcnow().isoformat()
c.execute("INSERT INTO Employees (Uid,Name,BloodGroup,Position,ContractType,CreatedAt) VALUES (?,?,?,?,?,?)",
          ('$uid','$name','N/A','Intern','Permanent',now))
c.commit()
"@
    $tmp = [IO.Path]::GetTempFileName() + ".py"
    [IO.File]::WriteAllText($tmp, $py)
    python $tmp | Out-Null
    Remove-Item $tmp
}


function Find-ByAid($root, $aid, $type) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $aid)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)))
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Wait-ByAid($root, $aid, $type, $tries = 20) {
    for ($i = 0; $i -lt $tries; $i++) {
        $el = Find-ByAid $root $aid $type
        if ($el) { return $el }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Shot-AfterClear([string]$dir) {
    Stop-App
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $proc = Start-App $dir
    Resize-Window $proc 1280 800
    Start-Sleep -Seconds 2
    $db = Join-Path $dir "idcard.db"
    for ($i=0; $i -lt 20 -and -not (Test-Path $db); $i++) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path $db)) { throw "temp idcard.db not created" }
    Add-Employee $db "55500011122" "Temp Clear Target"
    Start-Sleep -Milliseconds 500

    $root = Root-El $proc
    # Type the UID -> duplicate verdict under the field.
    $uidEl = Wait-ByAid $root "TxtUid" $CT::Edit 10
    if (-not $uidEl) { throw "TxtUid not found" }
    Set-Text $uidEl "55500011122"
    Start-Sleep -Milliseconds 800

    # Records page -> Clear all... -> typed DELETE ALL -> confirm.
    Click-El (Wait-ByAid $root "NavRecords" $CT::RadioButton) "Records nav"
    Start-Sleep -Milliseconds 800
    $clearBtn = Wait-El $root "Clear all..." $CT::Button
    if (-not $clearBtn) { throw "Clear all button not found" }
    Invoke-El $clearBtn "Clear all"

    $dlg = Wait-Dialog
    if (-not $dlg) { throw "typed dialog did not appear" }
    $edit = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Edit)))
    if (-not $edit) { throw "dialog text box not found" }
    Set-Text $edit "DELETE ALL"
    Start-Sleep -Milliseconds 400
    $okBtn = Find-El $dlg "Clear all data" $CT::Button
    if (-not $okBtn) { throw "confirm button not found" }
    if (-not $okBtn.Current.IsEnabled) { throw "confirm button still disabled" }
    Invoke-El $okBtn "confirm clear"
    Start-Sleep -Milliseconds 1200

    # Back to New card — footer must be clean, message in status bar.
    Click-El (Wait-ByAid $root "NavNewCard" $CT::RadioButton) "New card nav"
    Start-Sleep -Milliseconds 800
    Save-Shot $proc "newcard-after-clear-1280x800.png"
}

function Shot-Duplicate([string]$dir) {
    Stop-App
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $proc = Start-App $dir
    Resize-Window $proc 1280 800
    Start-Sleep -Seconds 2
    $db = Join-Path $dir "idcard.db"
    for ($i=0; $i -lt 20 -and -not (Test-Path $db); $i++) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path $db)) { throw "temp idcard.db not created" }
    Add-Employee $db "44400099988" "Dup Shot Person"
    Start-Sleep -Milliseconds 500

    $root = Root-El $proc
    $uidEl = Wait-ByAid $root "TxtUid" $CT::Edit 10
    if (-not $uidEl) { throw "TxtUid not found" }
    Set-Text $uidEl "44400099988"
    Start-Sleep -Milliseconds 900
    Save-Shot $proc "newcard-duplicate-1280x800.png"
}

switch ($Which) {
    "backup"   { Shot-BackupMessage }
    "clear"    { Shot-AfterClear "$env:TEMP\idforge-shot-clear" }
    "dup"      { Shot-Duplicate "$env:TEMP\idforge-shot-dup" }
    default    { Shot-AfterClear "$env:TEMP\idforge-shot-clear"; Shot-Duplicate "$env:TEMP\idforge-shot-dup" }
}
Stop-App

<#
Drives the built Hail Mary app through Windows UI Automation (no real mouse/keyboard input,
never brings the window to the foreground) — for checking UI changes and taking screenshots.

Build first:  cd src\HailMary; dotnet build -c Debug -p:Platform=x64

Usage (PowerShell 7):
  .\scripts\ui_drive.ps1 -Action launch                       # start app, window 1700x1000
  .\scripts\ui_drive.ps1 -Action click  -Name "Oxco"          # invoke/select element by its UIA name
  .\scripts\ui_drive.ps1 -Action list   -Name "Frame"         # list elements (type | name), optional name filter
  .\scripts\ui_drive.ps1 -Action set    -Name "Original (file)" -Value "C:\x.mp4"   # text field by header
  .\scripts\ui_drive.ps1 -Action combo  -Name "Analysis mode" -Value "Blob (largest changed region, faster)"
  .\scripts\ui_drive.ps1 -Action resize -Value 1700x1900
  .\scripts\ui_drive.ps1 -Action shot   -Value C:\temp\shot.png  # PrintWindow capture, works when covered
  .\scripts\ui_drive.ps1 -Action close

Notes:
  - Names are the visible (localized) texts, so they depend on the UI language.
  - A TextBox set via "set" commits to the view model only after it loses focus (WinUI TwoWay binding);
    for paths that must take effect immediately, set them in the tool's config JSON before "launch".
#>
param(
    [ValidateSet("launch", "list", "click", "set", "combo", "resize", "shot", "close")]
    [string]$Action = "list",
    [string]$Name = "",
    [string]$Value = "",
    [string]$Exe = ""
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class HmWin {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public struct RECT { public int L, T, R, B; }
}
"@

if (-not $Exe) {
    $Exe = Join-Path $PSScriptRoot "..\src\HailMary\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\HailMary.exe"
}

$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]::Descendants

function Get-AppProcess {
    Get-Process HailMary -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}

function Get-MainWindow {
    $p = Get-AppProcess
    if (-not $p) { throw "Hail Mary is not running (use -Action launch)." }
    $UIA::FromHandle($p.MainWindowHandle)
}

function Find-ByName([System.Windows.Automation.AutomationElement]$root, [string]$text) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $text)
    $root.FindFirst($Scope, $cond)
}

switch ($Action) {
    "launch" {
        Start-Process $Exe | Out-Null
        for ($i = 0; $i -lt 60 -and -not (Get-AppProcess); $i++) { Start-Sleep -Milliseconds 500 }
        $p = Get-AppProcess
        if (-not $p) { throw "No window after 30 s." }
        [HmWin]::MoveWindow($p.MainWindowHandle, 20, 20, 1700, 1000, $true) | Out-Null
        Start-Sleep 3
        "launched pid $($p.Id)"
    }
    "list" {
        $all = (Get-MainWindow).FindAll($Scope, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($e in $all) {
            $n = $e.Current.Name
            if ($n -and ($Name -eq "" -or $n -like "*$Name*")) { "{0} | {1}" -f $e.Current.ControlType.ProgrammaticName, $n }
        }
    }
    "click" {
        $e = Find-ByName (Get-MainWindow) $Name
        if (-not $e) { "not found: $Name"; break }
        $o = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$o)) { $o.Select() }
        elseif ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$o)) { $o.Invoke() }
        else { "no select/invoke pattern: $Name"; break }
        Start-Sleep 2
        "clicked: $Name"
    }
    "set" {
        $e = Find-ByName (Get-MainWindow) $Name
        if (-not $e) { "not found: $Name"; break }
        $o = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$o)) { $o.SetValue($Value); "set $Name" }
        else { "no value pattern: $Name" }
        Start-Sleep 1
    }
    "combo" {
        $w = Get-MainWindow
        $cb = Find-ByName $w $Name
        if (-not $cb) { "not found: $Name"; break }
        $ec = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ec.Expand(); Start-Sleep 1
        $item = Find-ByName $cb $Value
        if (-not $item) { $item = Find-ByName $w $Value }
        if (-not $item) { $ec.Collapse(); "item not found: $Value"; break }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep 1
        try { $ec.Collapse() } catch { }
        "selected $Value"
    }
    "resize" {
        $wh = $Value -split "x"
        [HmWin]::MoveWindow((Get-AppProcess).MainWindowHandle, 0, 0, [int]$wh[0], [int]$wh[1], $true) | Out-Null
        Start-Sleep 2
        "resized $Value"
    }
    "shot" {
        if (-not $Value) { throw "-Value <file.png> required" }
        $h = (Get-AppProcess).MainWindowHandle
        $r = New-Object HmWin+RECT
        [HmWin]::GetWindowRect($h, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [HmWin]::PrintWindow($h, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT: renders WinUI content even when covered
        $g.ReleaseHdc($hdc)
        $bmp.Save($Value, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        "saved $Value"
    }
    "close" {
        Get-Process HailMary -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
        Start-Sleep 2
        Get-Process HailMary -ErrorAction SilentlyContinue | Stop-Process -Force
        "closed"
    }
}

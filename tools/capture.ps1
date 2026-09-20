<#
Captures an Offender window to a PNG for visual checking.

Two things this script gets right that a naive version does not:

1. It runs DPI-aware. The app is PerMonitorV2, so a DPI-virtualised caller gets
   scaled-down rects back from GetWindowRect and captures the wrong screen region.

2. It finds windows with EnumWindows, not FindWindow. A window class registered by
   another process without CS_GLOBALCLASS cannot be resolved by name from outside that
   process, so FindWindow('OffenderPanel') always returns 0 even though the window is
   right there.

PrintWindow cannot read a layered window (the Clear theme, the taskbar readout), so use
-Screen for those: it captures the real screen region and shows the composited result.
#>
[CmdletBinding()]
param(
    [string]$Class = 'OffenderPanel',
    [string]$Out = 'panel.png',
    [switch]$Screen,
    [int]$PadWidth = 0,
    [int]$PadHeight = 0
)

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Cap {
  public delegate bool Cb(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(Cb cb, IntPtr p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern int SetProcessDpiAwarenessContext(IntPtr c);
  public struct RECT { public int L, T, R, B; }

  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, Cb cb, IntPtr p);

  static string ClassOf(IntPtr h) {
    var sb = new StringBuilder(256);
    GetClassNameW(h, sb, 256);
    return sb.ToString();
  }

  // Searches top-level windows and their children. The taskbar readout is a child of
  // Shell_TrayWnd, so a top-level-only sweep misses it.
  public static IntPtr FindByClass(string want) {
    IntPtr hit = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      if (ClassOf(h) == want) { hit = h; return false; }
      EnumChildWindows(h, delegate(IntPtr c, IntPtr q) {
        if (ClassOf(c) == want) { hit = c; return false; }
        return true;
      }, IntPtr.Zero);
      return hit == IntPtr.Zero;
    }, IntPtr.Zero);
    return hit;
  }
}
"@

# -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
[void][Cap]::SetProcessDpiAwarenessContext([IntPtr](-4))

$h = [Cap]::FindByClass($Class)
if ($h -eq [IntPtr]::Zero) { Write-Error "window '$Class' not found"; exit 1 }

$r = New-Object Cap+RECT
[void][Cap]::GetWindowRect($h, [ref]$r)
$w = ($r.R - $r.L) + $PadWidth
$ht = ($r.B - $r.T) + $PadHeight
Write-Host "$Class at $($r.L),$($r.T)  ${w}x${ht}  visible=$([Cap]::IsWindowVisible($h))"

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)

if ($Screen) {
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
} else {
    $dc = $g.GetHdc()
    [void][Cap]::PrintWindow($h, $dc, 0)
    $g.ReleaseHdc($dc)
}

$path = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $PSScriptRoot "..\$Out" }
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved $path"

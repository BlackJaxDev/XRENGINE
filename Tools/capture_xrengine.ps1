# capture_xrengine.ps1
# Captures only the XREngine.Editor window by its process name.
# Usage:  powershell -ExecutionPolicy Bypass -File Tools\capture_xrengine.ps1
# Output: Build\Logs\capture.png  (overwritten each run)
#         Also prints per-pixel diagnostics to stdout.

param(
    [string]$OutPath = "$PSScriptRoot\..\Build\Logs\capture.png"
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class NativeCapture
{
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest,
        int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    // PW_RENDERFULLCONTENT = 2  (captures DWM-composed content including layered windows)
    public static Bitmap CaptureWindow(IntPtr hWnd)
    {
        RECT rc;
        GetWindowRect(hWnd, out rc);
        int w = rc.Right - rc.Left;
        int h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0)
            throw new Exception("Window has zero size: " + w + "x" + h);

        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            // Try PrintWindow with PW_RENDERFULLCONTENT first (works even if occluded)
            bool ok = PrintWindow(hWnd, hdc, 2);
            g.ReleaseHdc(hdc);

            if (!ok)
            {
                // Fallback: BitBlt from screen (window must be visible)
                using (Graphics gScreen = Graphics.FromHwnd(IntPtr.Zero))
                {
                    IntPtr hdcScreen = gScreen.GetHdc();
                    IntPtr hdcDest = g.GetHdc();
                    BitBlt(hdcDest, 0, 0, w, h, hdcScreen, rc.Left, rc.Top, 0x00CC0020); // SRCCOPY
                    g.ReleaseHdc(hdcDest);
                    gScreen.ReleaseHdc(hdcScreen);
                }
            }
        }
        return bmp;
    }
}
'@

# --- Find the XREngine.Editor process ---
$procs = Get-Process -Name "XREngine.Editor" -ErrorAction SilentlyContinue
if (-not $procs -or $procs.Count -eq 0) {
    Write-Error "XREngine.Editor is not running."
    exit 1
}
$proc = $procs | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
if (-not $proc) {
    Write-Error "XREngine.Editor has no visible main window."
    exit 1
}

$hwnd = $proc.MainWindowHandle
Write-Host "Capturing PID=$($proc.Id)  HWND=$hwnd"
Write-Host "Title: $($proc.MainWindowTitle)"

# --- Capture ---
$bmp = [NativeCapture]::CaptureWindow($hwnd)
$w = $bmp.Width
$h = $bmp.Height
Write-Host "Captured: ${w}x${h}"

# --- Save ---
$OutPath = [System.IO.Path]::GetFullPath($OutPath)
$dir = [System.IO.Path]::GetDirectoryName($OutPath)
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Saved: $OutPath"

# --- Pixel diagnostics ---

# Center pixel
$cx = [int]($w / 2); $cy = [int]($h / 2)
$cp = $bmp.GetPixel($cx, $cy)
Write-Host "Center($cx,$cy): R=$($cp.R) G=$($cp.G) B=$($cp.B)"

# Grid sample (5x5)
Write-Host "`n=== 5x5 Grid Sample ==="
for ($gy = 0; $gy -lt 5; $gy++) {
    $line = ""
    for ($gx = 0; $gx -lt 5; $gx++) {
        $sx = [int](($gx + 0.5) * $w / 5)
        $sy = [int](($gy + 0.5) * $h / 5)
        $px = $bmp.GetPixel($sx, $sy)
        $line += "($($px.R),$($px.G),$($px.B)) "
    }
    Write-Host $line
}

# Histogram buckets
$black = 0; $vdark = 0; $dark = 0; $mid = 0; $bright = 0; $total = 0
for ($y = 0; $y -lt $h; $y += 4) {
    for ($x = 0; $x -lt $w; $x += 4) {
        $total++
        $px = $bmp.GetPixel($x, $y)
        $lum = [int]$px.R + [int]$px.G + [int]$px.B
        if     ($lum -eq 0)   { $black++ }
        elseif ($lum -lt 30)  { $vdark++ }
        elseif ($lum -lt 150) { $dark++ }
        elseif ($lum -lt 450) { $mid++ }
        else                  { $bright++ }
    }
}
Write-Host "`n=== Histogram ==="
Write-Host ("Black(0):      {0:F1}%" -f ($black  / $total * 100))
Write-Host ("VeryDark(<10): {0:F1}%" -f ($vdark  / $total * 100))
Write-Host ("Dark(<50):     {0:F1}%" -f ($dark   / $total * 100))
Write-Host ("Mid(<150):     {0:F1}%" -f ($mid    / $total * 100))
Write-Host ("Bright(>=150): {0:F1}%" -f ($bright / $total * 100))

# Find max brightness
$maxR = 0; $maxG = 0; $maxB = 0
for ($y = 0; $y -lt $h; $y += 4) {
    for ($x = 0; $x -lt $w; $x += 4) {
        $px = $bmp.GetPixel($x, $y)
        if ($px.R -gt $maxR) { $maxR = $px.R }
        if ($px.G -gt $maxG) { $maxG = $px.G }
        if ($px.B -gt $maxB) { $maxB = $px.B }
    }
}
Write-Host "MaxRGB: $maxR, $maxG, $maxB"

$bmp.Dispose()
Write-Host "`nDone."

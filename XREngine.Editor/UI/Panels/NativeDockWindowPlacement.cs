using XREngine.Rendering;

namespace XREngine.Editor;

/// <summary>Validates a saved detached-window position against current displays.</summary>
internal readonly record struct NativeDockWindowPlacement(int X, int Y, int Width, int Height)
{
    internal static NativeDockWindowPlacement Resolve(
        int? x, int? y, int? width, int? height,
        ReadOnlySpan<RuntimeDesktopMonitor> monitors,
        NativeDockWindowPlacement fallback)
    {
        if (x is not { } savedX || y is not { } savedY ||
            width is not { } savedWidth || height is not { } savedHeight ||
            savedWidth < 320 || savedHeight < 240 ||
            savedWidth > 8192 || savedHeight > 8192)
            return fallback;

        int selected = -1;
        long bestOverlap = 0;
        for (int index = 0; index < monitors.Length; index++)
        {
            RuntimeDesktopMonitor monitor = monitors[index];
            if (monitor.WorkWidth <= 0 || monitor.WorkHeight <= 0)
                continue;
            long overlapWidth = Math.Max(0L, Math.Min((long)savedX + savedWidth,
                (long)monitor.WorkX + monitor.WorkWidth) - Math.Max(savedX, monitor.WorkX));
            long overlapHeight = Math.Max(0L, Math.Min((long)savedY + savedHeight,
                (long)monitor.WorkY + monitor.WorkHeight) - Math.Max(savedY, monitor.WorkY));
            long overlap = overlapWidth * overlapHeight;
            if (overlap <= bestOverlap)
                continue;
            bestOverlap = overlap;
            selected = index;
        }
        if (selected < 0)
            return fallback;

        RuntimeDesktopMonitor workArea = monitors[selected];
        int clampedWidth = Math.Min(savedWidth, workArea.WorkWidth);
        int clampedHeight = Math.Min(savedHeight, workArea.WorkHeight);
        if (clampedWidth < 320 || clampedHeight < 240)
            return fallback;

        // Keep enough of the title strip visible to move the window again.
        int visibleTitleWidth = Math.Min(64, clampedWidth);
        int clampedX = (int)Math.Clamp((long)savedX,
            (long)workArea.WorkX - clampedWidth + visibleTitleWidth,
            (long)workArea.WorkX + workArea.WorkWidth - visibleTitleWidth);
        int clampedY = (int)Math.Clamp((long)savedY,
            (long)workArea.WorkY,
            (long)workArea.WorkY + workArea.WorkHeight - 32);
        return new(clampedX, clampedY, clampedWidth, clampedHeight);
    }
}

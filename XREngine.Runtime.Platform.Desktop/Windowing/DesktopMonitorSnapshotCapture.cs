using System.Runtime.InteropServices;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Captures Win32 monitor metrics on the native window owner.</summary>
internal static class DesktopMonitorSnapshotCapture
{
    private delegate bool MonitorCallback(nint monitor, nint hdc, nint rectangle, nint userData);

    public static RuntimeDesktopMonitor[] Capture()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        List<RuntimeDesktopMonitor> monitors = [];
        MonitorCallback callback = (monitor, _, _, _) =>
        {
            NativeMonitorInfo info = new() { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                return true;
            float scale = 1.0f;
            try
            {
                if (GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                    scale = dpiX / 96.0f;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            monitors.Add(new RuntimeDesktopMonitor(
                monitor,
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top,
                info.Work.Left,
                info.Work.Top,
                info.Work.Right - info.Work.Left,
                info.Work.Bottom - info.Work.Top,
                scale));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0))
            return [];
        return [.. monitors];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint userData);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}

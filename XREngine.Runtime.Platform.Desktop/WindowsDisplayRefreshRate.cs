using System.Runtime.InteropServices;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>
/// Reads the refresh rate of the primary display through the Win32 display settings.
/// </summary>
internal static class WindowsDisplayRefreshRate
{
    private const int EnumCurrentSettings = -1;
    private const int DeviceNameLength = 32;
    private const int FormNameLength = 32;

    /// <summary>Returns the primary display refresh rate in hertz, or 0 when it is unknown.</summary>
    internal static float Read()
    {
        if (!OperatingSystem.IsWindows())
            return 0.0f;

        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref mode))
            return 0.0f;

        // DEVMODE uses 0 and 1 for "hardware default".
        return mode.DisplayFrequency <= 1 ? 0.0f : mode.DisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNumber, ref DevMode devMode);

    /// <summary>The display variant of the Win32 <c>DEVMODEW</c> structure.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceNameLength)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = FormNameLength)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}

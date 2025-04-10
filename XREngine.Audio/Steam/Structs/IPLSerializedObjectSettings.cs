using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLSerializedObjectSettings
{
    public IntPtr data;
    public IntPtr size;
}
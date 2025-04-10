using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLHRTFSettings
{
    public IPLHRTFType type;
    public IntPtr sofaFileName;
    public IntPtr sofaData;
    public int sofaDataSize;
    public float volume;
    public IPLHRTFNormType normType;
}
using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLTrueAudioNextDeviceSettings
{
    public int frameSize;
    public int irSize;
    public int order;
    public int maxSources;
}
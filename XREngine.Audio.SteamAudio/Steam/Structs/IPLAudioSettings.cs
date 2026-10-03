using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLAudioSettings
{
    public int samplingRate;
    public int frameSize;
}
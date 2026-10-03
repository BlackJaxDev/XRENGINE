using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLSpeakerLayout
{
    public IPLSpeakerLayoutType type;
    public int numSpeakers;
    public IntPtr speakers;
}
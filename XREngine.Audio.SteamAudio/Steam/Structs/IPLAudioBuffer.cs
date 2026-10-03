using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLAudioBuffer
{
    public int numChannels;
    public int numSamples;
    public IntPtr data;
}
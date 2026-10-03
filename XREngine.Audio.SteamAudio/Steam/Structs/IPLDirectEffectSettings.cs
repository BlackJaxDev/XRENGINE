using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create a direct effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLDirectEffectSettings
{
    /** Number of channels that will be used by input and output buffers. */
    public Int32 numChannels;
}

using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create a reflection effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLReflectionEffectSettings
{
    /** Type of reflection effect algorithm to use. */
    public IPLReflectionEffectType type;

    /** Number of samples per channel in the IR. */
    public Int32 irSize;

    /** Number of channels in the IR. */
    public Int32 numChannels;
}

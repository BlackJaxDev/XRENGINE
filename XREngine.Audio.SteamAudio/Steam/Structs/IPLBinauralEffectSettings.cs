using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create a binaural effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLBinauralEffectSettings
{
    /** The HRTF to use. */
    public IPLHRTF hrtf;
}

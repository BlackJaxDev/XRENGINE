using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create an Ambisonics binaural effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsBinauralEffectSettings
{
    /** The HRTF to use. */
    public IPLHRTF hrtf;

    /** The maximum Ambisonics order that will be used by input audio buffers. */
    public Int32 maxOrder;
}

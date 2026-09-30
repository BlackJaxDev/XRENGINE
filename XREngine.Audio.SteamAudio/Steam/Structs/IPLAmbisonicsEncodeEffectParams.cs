using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying an Ambisonics encode effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsEncodeEffectParams
{
    /** Vector pointing from the listener towards the source. Need not be normalized; Steam Audio will automatically
        normalize this vector. If a zero-length vector is passed, the output will be order 0 (omnidirectional). */
    public IPLVector3 direction;

    /** Ambisonic order of the output buffer. May be less than the \c maxOrder specified when creating the effect,
        in which case the effect will generate fewer output channels, reducing CPU usage. */
    public Int32 order;
}

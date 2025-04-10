using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying an Ambisonics decode effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsDecodeEffectParams
{
    /** Ambisonic order of the input buffer. May be less than the \c maxOrder specified when creating the effect,
        in which case the effect will process fewer input channels, reducing CPU usage. */
    public Int32 order;

    /** The HRTF to use. */
    public IPLHRTF hrtf;

    /** The orientation of the listener. */
    public IPLCoordinateSpace3 orientation;

    /** Whether to use binaural rendering or panning. */
    public IPLbool binaural;
}

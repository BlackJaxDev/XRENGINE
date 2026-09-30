using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying an Ambisonics rotation effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsRotationEffectParams
{
    /** The orientation of the listener. */
    public IPLCoordinateSpace3 orientation;

    /** Ambisonic order of the input and output buffers. May be less than the \c maxOrder specified when creating the
        effect, in which case the effect will process fewer channels, reducing CPU usage. */
    public Int32 order;
}

using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying an Ambisonics panning effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsPanningEffectParams
{
    /** Ambisonic order of the input buffer. May be less than the \c maxOrder specified when creating the effect,
        in which case the effect will process fewer input channels, reducing CPU usage. */
    public Int32 order;
}

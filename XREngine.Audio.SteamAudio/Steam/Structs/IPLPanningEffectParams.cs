using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying a panning effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLPanningEffectParams
{
    /** Unit vector pointing from the listener towards the source. */
    public IPLVector3 direction;
}

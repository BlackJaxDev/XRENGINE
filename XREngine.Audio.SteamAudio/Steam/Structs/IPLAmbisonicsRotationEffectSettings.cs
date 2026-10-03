using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create an Ambisonics rotation effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsRotationEffectSettings
{
    /** The maximum Ambisonics order that will be used by input audio buffers. */
    public Int32 maxOrder;
}

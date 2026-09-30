using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create an Ambisonics encode effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsEncodeEffectSettings
{
    /** Maximum Ambisonics order to encode audio buffers to. */
    public Int32 maxOrder;
}

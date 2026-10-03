using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create an Ambisonics panning effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsPanningEffectSettings
{
    /** The speaker layout that will be used by output audio buffers. */
    public IPLSpeakerLayout speakerLayout;

    /** The maximum Ambisonics order that will be used by input audio buffers. */
    public Int32 maxOrder;
}

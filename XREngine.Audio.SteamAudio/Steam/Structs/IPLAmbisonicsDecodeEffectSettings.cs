using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create an Ambisonics decode effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsDecodeEffectSettings
{
    /** The speaker layout that will be used by output audio buffers. */
    public IPLSpeakerLayout speakerLayout;

    /** The HRTF to use. */
    public IPLHRTF hrtf;

    /** The maximum Ambisonics order that will be used by input audio buffers. */
    public Int32 maxOrder;
}

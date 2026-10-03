using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create a panning effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLPanningEffectSettings
{
    /** The speaker layout to pan input audio to. */
    public IPLSpeakerLayout speakerLayout;
}

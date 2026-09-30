using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create a virtual surround effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLVirtualSurroundEffectSettings
{
    /** The speaker layout that will be used by input audio buffers. */
    public IPLSpeakerLayout speakerLayout;

    /** The HRTF to use. */
    public IPLHRTF hrtf;
}

using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying a virtual surround effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLVirtualSurroundEffectParams
{
    /** The HRTF to use. */
    public IPLHRTF hrtf;
}

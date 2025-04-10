using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Settings used to create a path effect. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLPathEffectSettings
{
    /** The maximum Ambisonics order that will be used by output audio buffers. */
    public Int32 maxOrder;

    /** If \c IPL_TRUE, then this effect will render spatialized audio into the output buffer. If \c IPL_FALSE,
        this effect will render un-spatialized (and un-rotated) Ambisonic audio. Setting this to \c IPL_FALSE is
        mainly useful only if you plan to mix multiple Ambisonic buffers and/or apply additional processing to
        the Ambisonic audio before spatialization. If you plan to immediately spatialize the output of the path
        effect, setting this value to \c IPL_TRUE can result in significant performance improvements. */
    public IPLbool spatialize;

    /** The speaker layout to use when spatializing. Only used if \c spatialize is \c IPL_TRUE. */
    public IPLSpeakerLayout speakerLayout;

    /** The HRTF to use when spatializing. Only used if \c spatialize is \c IPL_TRUE. */
    public IPLHRTF hrtf;
}

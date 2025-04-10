using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying a reflection effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLReflectionEffectParams
{
    /** Type of reflection effect algorithm to use. */
    public IPLReflectionEffectType type;

    /** The impulse response. For \c IPL_REFLECTIONEFFECTTYPE_CONVOLUTION or \c IPL_REFLECTIONEFFECTTYPE_HYBRID. */
    public IPLReflectionEffectIR ir;

    /** 3-band reverb decay times (RT60). For \c IPL_REFLECTIONEFFECTTYPE_PARAMETRIC or
        \c IPL_REFLECTIONEFFECTTYPE_HYBRID. */
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] reverbTimes;

    /** 3-band EQ coefficients applied to the parametric part to ensure smooth transition.
        For \c IPL_REFLECTIONEFFECTTYPE_HYBRID. */
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] eq;

    /** Samples after which parametric part starts. For \c IPL_REFLECTIONEFFECTTYPE_HYBRID. */
    public Int32 delay;

    /** Number of IR channels to process. May be less than the number of channels specified when creating the effect,
        in which case CPU usage will be reduced. */
    public Int32 numChannels;

    /** Number of IR samples per channel to process. May be less than the number of samples specified when creating
        the effect, in which case CPU usage will be reduced. */
    public Int32 irSize;

    /** The TrueAudio Next device to use for convolution processing. For \c IPL_REFLECTIONEFFECTTYPE_TAN. */
    public IPLTrueAudioNextDevice tanDevice;

    /** The TrueAudio Next slot index to use for convolution processing. The slot identifies the IR to use. For
        \c IPL_REFLECTIONEFFECTTYPE_TAN. */
    public Int32 tanSlot;
}

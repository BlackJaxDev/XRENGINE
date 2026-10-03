using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying a path effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLPathEffectParams
{
    /** 3-band EQ coefficients for modeling frequency-dependent attenuation caused by paths bending around
        obstacles. */
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] eqCoeffs;

    /** Ambisonic coefficients for modeling the directional distribution of sound reaching the listener.
        The coefficients are specified in world-space, and must be rotated to match the listener's orientation
        separately. */
    public IntPtr shCoeffs;

    /** Ambisonic order of the output buffer. May be less than the maximum order specified when creating the effect,
        in which case higher-order \c shCoeffs will be ignored, and CPU usage will be reduced. */
    public Int32 order;

    /** If \c IPL_TRUE, spatialize using HRTF-based binaural rendering. Only used if \c spatialize was set to
        \c IPL_TRUE in \c IPLPathEffectSettings. */
    public IPLbool binaural;

    /** The HRTF to use when spatializing. Only used if \c spatialize was set to \c IPL_TRUE in
        \c IPLPathEffectSettings and \c binaural is set to \c IPL_TRUE. */
    public IPLHRTF hrtf;

    /** The position and orientation of the listener. Only used if \c spatialize was set to \c IPL_TRUE in
        \c IPLPathEffectSettings and \c binaural is set to \c IPL_TRUE. */
    public IPLCoordinateSpace3 listener;
}

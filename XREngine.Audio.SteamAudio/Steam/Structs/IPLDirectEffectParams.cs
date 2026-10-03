using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying a direct effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLDirectEffectParams
{
    /** Flags indicating which direct path effects to apply. */
    public IPLDirectEffectFlags flags;

    /** Mode of applying transmission effect, if \c IPL_DIRECTEFFECTFLAGS_APPLYTRANSMISSION is enabled. */
    public IPLTransmissionType transmissionType;

    /** Value of distance attenuation, between 0 and 1. */
    public float distanceAttenuation;

    /** 3-band EQ coefficients for air absorption, each between 0 and 1. */
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] airAbsorption;

    /** Value of directivity term, between 0 and 1. */
    public float directivity;

    /** Value of occlusion factor, between 0 and 1. */
    public float occlusion;

    /** 3-band EQ coefficients for transmission, each between 0 and 1. */
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] transmission;
}

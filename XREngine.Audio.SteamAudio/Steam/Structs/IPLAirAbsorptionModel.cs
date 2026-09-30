using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** An air absorption model that can be used for modeling frequency-dependent attenuation of sound over
    distance. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLAirAbsorptionModel
{
    /** The type of air absorption model to use. */
    public IPLAirAbsorptionModelType type;

    /** The exponential falloff coefficients to use when \c type is \c IPL_AIRABSORPTIONTYPE_EXPONENTIAL. */
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] coefficients;

    /** When \c type is \c IPL_AIRABSORPTIONTYPE_CALLBACK, this function will be called whenever Steam
        Audio needs to evaluate air absorption. */
    public IntPtr callback;

    /** Pointer to arbitrary data that will be provided to the \c callback function whenever it is called.
        May be \c NULL. */
    public IntPtr userData;

    /** Set to \c IPL_TRUE to indicate that the air absorption model defined by the \c callback function
        has changed since the last time simulation was run. For example, the callback may be evaluating a set of
        curves defined in a GUI. If the user is editing the curves in real-time, set this to \c IPL_TRUE whenever
        the curves change, so Steam Audio can update simulation results to match. */
    public IPLbool dirty;
}

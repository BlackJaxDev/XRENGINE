using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** A distance attenuation model that can be used for modeling attenuation of sound over distance. Can be used
    with both direct and indirect sound propagation. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLDistanceAttenuationModel
{
    /** The type of distance attenuation model to use. */
    public IPLDistanceAttenuationModelType type;

    /** When \c type is \c IPL_DISTANCEATTENUATIONTYPE_INVERSEDISTANCE, no distance attenuation is applied to
        any sound whose distance from the listener is less than this value. */
    public float minDistance;

    /** When \c type is \c IPL_DISTANCEATTENUATIONTYPE_CALLBACK, this function will be called whenever Steam
        Audio needs to evaluate distance attenuation. */
    public IntPtr callback;

    /** Pointer to arbitrary data that will be provided to the \c callback function whenever it is called.
        May be \c NULL. */
    public IntPtr userData;

    /** Set to \c IPL_TRUE to indicate that the distance attenuation model defined by the \c callback function
        has changed since the last time simulation was run. For example, the callback may be evaluating a
        curve defined in a GUI. If the user is editing the curve in real-time, set this to \c IPL_TRUE whenever
        the curve changes, so Steam Audio can update simulation results to match. */
    public IPLbool dirty;
}

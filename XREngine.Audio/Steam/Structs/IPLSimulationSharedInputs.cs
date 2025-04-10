using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Simulation parameters that are not specific to any source. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLSimulationSharedInputs
{
    /** The position and orientation of the listener. */
    public IPLCoordinateSpace3 listener;

    /** The number of rays to trace from the listener. Increasing this value results in more accurate
        reflections, at the cost of increased CPU usage. */
    public Int32 numRays;

    /** The number of times each ray traced from the listener is reflected when it encounters a solid
        object. Increasing this value results in longer, more accurate reverb tails, at the cost of
        increased CPU usage during simulation. */
    public Int32 numBounces;

    /** The duration (in seconds) of the impulse responses generated when simulating reflections.
        Increasing this value results in longer, more accurate reverb tails, at the cost of increased
        CPU usage during audio processing. */
    public float duration;

    /** The Ambisonic order of the impulse responses generated when simulating reflections. Increasing
        this value results in more accurate directional variation of reflected sound, at the cost
        of increased CPU usage during audio processing. */
    public Int32 order;

    /** When calculating how much sound energy reaches a surface directly from a source, any source that is
        closer than \c irradianceMinDistance to the surface is assumed to be at a distance of
        \c irradianceMinDistance, for the purposes of energy calculations. */
    public float irradianceMinDistance;

    /** Callback for visualizing valid path segments during call to \c iplSimulatorRunPathing.*/
    public IntPtr pathingVisCallback;

    /** Pointer to arbitrary user-specified data provided when calling the function that will
        call this callback.*/
    public IntPtr pathingUserData;
}

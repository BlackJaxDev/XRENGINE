using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/// <summary>
/// Simulation parameters for a source.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLSimulationInputs
{
    /// <summary>
    /// The types of simulation to run for this source.
    /// </summary>
    public IPLSimulationFlags flags;

    /// <summary>
    /// The types of direct simulation to run for this source.
    /// </summary>
    public IPLDirectSimulationFlags directFlags;

    /// <summary>
    /// The position and orientation of this source.
    /// </summary>
    public IPLCoordinateSpace3 source;

    /// <summary>
    /// The distance attenuation model to use for this source.
    /// </summary>
    public IPLDistanceAttenuationModel distanceAttenuationModel;

    /// <summary>
    /// The air absorption model to use for this source.
    /// </summary>
    public IPLAirAbsorptionModel airAbsorptionModel;

    /// <summary>
    /// The directivity pattern to use for this source.
    /// </summary>
    public IPLDirectivity directivity;

    /// <summary>
    /// The occlusion algorithm to use for this source.
    /// </summary>
    public IPLOcclusionType occlusionType;

    /// <summary>
    /// If using volumetric occlusion, the source is modeled as a sphere with this radius.
    /// </summary>
    public float occlusionRadius;

    /// <summary>
    /// If using volumetric occlusion, this is the number of point samples to consider when
    /// tracing rays. This value can change between simulation runs.
    /// </summary>
    public Int32 numOcclusionSamples;

    /// <summary>
    /// If using parametric or hybrid reverb for rendering reflections, the reverb decay times
    /// for each frequency band are scaled by these values. Set to {1.0f, 1.0f, 1.0f} to use
    /// the simulated values without modification.
    /// </summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] reverbScale;

    /// <summary>
    /// If using hybrid reverb for rendering reflections, this is the length (in seconds) of
    /// impulse response to use for convolution reverb. The rest of the impulse response will
    /// be used for parametric reverb estimation only. Increasing this value results in more
    /// accurate reflections, at the cost of increased CPU usage.
    /// </summary>
    public float hybridReverbTransitionTime;

    /// <summary>
    /// If using hybrid reverb for rendering reflections, this is the amount of overlap between
    /// the convolution and parametric parts. To ensure smooth transitions from the early
    /// convolution part to the late parametric part, the two are cross-faded towards the end of
    /// the convolution part. For example, if hybridReverbTransitionTime is 1.0f, and
    /// hybridReverbOverlapPercent is 0.25f, then the first 0.75 seconds are pure convolution,
    /// the next 0.25 seconds are a blend between convolution and parametric, and the portion of
    /// the tail beyond 1.0 second is pure parametric.
    /// </summary>
    public float hybridReverbOverlapPercent;

    /// <summary>
    /// If IPL_TRUE, this source will use baked data for reflections simulation.
    /// </summary>
    public IPLbool baked;

    /// <summary>
    /// The identifier used to specify which layer of baked data to use for simulating reflections
    /// for this source.
    /// </summary>
    public IPLBakedDataIdentifier bakedDataIdentifier;

    /// <summary>
    /// The probe batch within which to find paths from this source to the listener.
    /// </summary>
    public IPLProbeBatch pathingProbes;

    /// <summary>
    /// When testing for mutual visibility between a pair of probes, each probe is treated as a sphere of
    /// this radius (in meters), and point samples are generated within this sphere.
    /// </summary>
    public float visRadius;

    /// <summary>
    /// When tracing rays to test for mutual visibility between a pair of probes, the fraction of rays that
    /// are unoccluded must be greater than this threshold for the pair of probes to be considered
    /// mutually visible.
    /// </summary>
    public float visThreshold;

    /// <summary>
    /// If the distance between two probes is greater than this value, the probes are not considered mutually
    /// visible. Increasing this value can result in simpler paths, at the cost of increased CPU usage.
    /// </summary>
    public float visRange;

    /// <summary>
    /// If simulating pathing, this is the Ambisonic order used for representing path directionality. Higher
    /// values result in more precise spatialization of paths, at the cost of increased CPU usage.
    /// </summary>
    public Int32 pathingOrder;

    /// <summary>
    /// If IPL_TRUE, baked paths are tested for visibility. This is useful if your scene has dynamic
    /// objects that might occlude baked paths.
    /// </summary>
    public IPLbool enableValidation;

    /// <summary>
    /// If IPL_TRUE, and enableValidation is IPL_TRUE, then if a baked path is occluded by dynamic
    /// geometry, path finding is re-run in real-time to find alternate paths that take into account the
    /// dynamic geometry.
    /// </summary>
    public IPLbool findAlternatePaths;

    /// <summary>
    /// If simulating transmission, this is the maximum number of surfaces, starting from the closest
    /// surface to the listener, whose transmission coefficients will be considered when calculating
    /// the total amount of sound transmitted. Increasing this value will result in more accurate
    /// results when multiple surfaces lie between the source and the listener, at the cost of
    /// increased CPU usage.
    /// </summary>
    public Int32 numTransmissionRays;
}

using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters used to control how reflections data is baked. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLReflectionsBakeParams
{
    /** The scene in which the probes exist. */
    public IPLScene scene;

    /** A probe batch containing the probes at which reflections data should be baked. */
    public IPLProbeBatch probeBatch;

    /** The type of scene being used. */
    public IPLSceneType sceneType;

    /** An identifier for the data layer that should be baked. The identifier determines what data is simulated and
        stored at each probe. If the probe batch already contains data with this identifier, it will be overwritten. */
    public IPLBakedDataIdentifier identifier;

    /** The types of data to save for each probe. */
    public IPLReflectionsBakeFlags bakeFlags;

    /** The number of rays to trace from each listener position when baking. Increasing this number results in
        improved accuracy, at the cost of increased bake times. */
    public Int32 numRays;

    /** The number of directions to consider when generating diffusely-reflected rays when baking. Increasing
        this number results in slightly improved accuracy of diffuse reflections. */
    public Int32 numDiffuseSamples;

    /** The number of times each ray is reflected off of solid geometry. Increasing this number results in
        longer reverb tails and improved accuracy, at the cost of increased bake times. */
    public Int32 numBounces;

    /** The length (in seconds) of the impulse responses to simulate. Increasing this number allows the baked
        data to represent longer reverb tails (and hence larger spaces), at the cost of increased memory
        usage while baking. */
    public float simulatedDuration;

    /** The length (in seconds) of the impulse responses to save at each probe. Increasing this number allows
        the baked data to represent longer reverb tails (and hence larger spaces), at the cost of increased
        disk space usage and memory usage at run-time.

        It may be useful to set \c savedDuration to be less than \c simulatedDuration, especially if you plan
        to use hybrid reverb for rendering baked reflections. This way, the parametric reverb data is
        estimated using a longer IR, resulting in more accurate estimation, but only the early part of the IR
        can be saved for subsequent rendering. */
    public float savedDuration;

    /** Ambisonic order of the baked IRs. */
    public Int32 order;

    /** Number of threads to use for baking. */
    public Int32 numThreads;

    /** If using custom ray tracer callbacks, this the number of rays that will be passed to the callbacks
        every time rays need to be traced. */
    public Int32 rayBatchSize;

    /** When calculating how much sound energy reaches a surface directly from a source, any source that is
        closer than \c irradianceMinDistance to the surface is assumed to be at a distance of
        \c irradianceMinDistance, for the purposes of energy calculations. */
    public float irradianceMinDistance;

    /** If using Radeon Rays or if \c identifier.variation is \c IPL_BAKEDDATAVARIATION_STATICLISTENER, this is the
        number of probes for which data is baked simultaneously. */
    public Int32 bakeBatchSize;

    /** The OpenCL device, if using Radeon Rays. */
    public IPLOpenCLDevice openCLDevice;

    /** The Radeon Rays device, if using Radeon Rays. */
    public IPLRadeonRaysDevice radeonRaysDevice;
}

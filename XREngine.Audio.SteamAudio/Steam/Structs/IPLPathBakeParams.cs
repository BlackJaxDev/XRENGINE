using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters used to control how pathing data is baked. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLPathBakeParams
{
    /** The scene in which the probes exist. */
    public IPLScene scene;

    /** A probe batch containing the probes for which pathing data should be baked. */
    public IPLProbeBatch probeBatch;

    /** An identifier for the data layer that should be baked. The identifier determines what data is simulated and
        stored at each probe. If the probe batch already contains data with this identifier, it will be overwritten. */
    public IPLBakedDataIdentifier identifier;

    /** Number of point samples to use around each probe when testing whether one probe can see another. To
        determine if two probes are mutually visible, numSamples * numSamples rays are traced, from each
        point sample of the first probe, to every other point sample of the second probe. */
    public Int32 numSamples;

    /** When testing for mutual visibility between a pair of probes, each probe is treated as a sphere of
        this radius (in meters), and point samples are generated within this sphere. */
    public float radius;

    /** When tracing rays to test for mutual visibility between a pair of probes, the fraction of rays that
        are unoccluded must be greater than this threshold for the pair of probes to be considered
        mutually visible. */
    public float threshold;

    /** If the distance between two probes is greater than this value, the probes are not considered mutually
        visible. Increasing this value can result in simpler paths, at the cost of increased bake times. */
    public float visRange;

    /** If the length of the path between two probes is greater than this value, the probes are considered to
        not have any path between them. Increasing this value allows sound to propagate over greater
        distances, at the cost of increased bake times and memory usage. */
    public float pathRange;

    /** Number of threads to use for baking. */
    public Int32 numThreads;
}

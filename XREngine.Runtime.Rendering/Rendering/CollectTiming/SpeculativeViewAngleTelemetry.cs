namespace XREngine.Rendering;

/// <summary>
/// Histogram of the angle between the head view that a speculative stereo collect culled with and
/// the predicted head view of the XR frame that receives those commands (measured when the collect
/// thread assigns the commands to the located frame). A large angle means that objects at the edge
/// of the final view can be missing, because the speculative volume is only somewhat wider than one
/// eye pair. Cumulative; capture twice and use the differences. Writers use interlocked increments
/// on a preallocated table; only <see cref="Capture"/> allocates.
/// </summary>
public static class SpeculativeViewAngleTelemetry
{
    /// <summary>Width of one bucket in degrees.</summary>
    public const double BucketDegrees = 0.1;

    /// <summary>Number of buckets. The last bucket also counts every larger angle.</summary>
    public const int BucketCount = 300;

    private static readonly long[] s_buckets = new long[BucketCount];

    /// <summary>Adds one angle sample in degrees.</summary>
    public static void Record(double degrees)
    {
        if (!double.IsFinite(degrees))
            return;

        int bucket = (int)Math.Clamp(degrees / BucketDegrees, 0.0, BucketCount - 1);
        Interlocked.Increment(ref s_buckets[bucket]);
    }

    /// <summary>Copies the bucket counts. Allocates; not for frame paths.</summary>
    public static long[] Capture()
    {
        long[] copy = new long[BucketCount];
        for (int index = 0; index < copy.Length; ++index)
            copy[index] = Interlocked.Read(ref s_buckets[index]);
        return copy;
    }
}

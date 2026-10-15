namespace XREngine.Rendering;

/// <summary>
/// Process-wide counts of sealed visibility stream reuse. A backend records one
/// event for each visibility family seal, so the cost is a few atomic additions
/// for each frame.
/// </summary>
public static class AdvancedVisibilityStreamReuseTelemetry
{
    private static readonly long[] s_misses = new long[(int)EAdvancedVisibilityStreamReuseMiss.DeformationOutput + 1];
    private static long s_reuses;

    /// <summary>Records a seal that kept the stream of an earlier frame.</summary>
    public static void RecordReuse() => Interlocked.Increment(ref s_reuses);

    /// <summary>Records a seal that built the stream again, with the first reason.</summary>
    public static void RecordReseal(EAdvancedVisibilityStreamReuseMiss miss)
    {
        int index = (int)miss;
        if ((uint)index >= (uint)s_misses.Length || miss == EAdvancedVisibilityStreamReuseMiss.None)
            index = (int)EAdvancedVisibilityStreamReuseMiss.NoRetainedStream;
        Interlocked.Increment(ref s_misses[index]);
    }

    /// <summary>Captures the cumulative counts.</summary>
    public static AdvancedVisibilityStreamReuseSnapshot Capture()
    {
        long Miss(EAdvancedVisibilityStreamReuseMiss miss) => Interlocked.Read(ref s_misses[(int)miss]);
        long reseals = 0;
        for (int index = 0; index < s_misses.Length; ++index)
            reseals += Interlocked.Read(ref s_misses[index]);
        return new(
            Interlocked.Read(ref s_reuses),
            reseals,
            Miss(EAdvancedVisibilityStreamReuseMiss.NoRetainedStream),
            Miss(EAdvancedVisibilityStreamReuseMiss.Columns),
            Miss(EAdvancedVisibilityStreamReuseMiss.Publication),
            Miss(EAdvancedVisibilityStreamReuseMiss.SceneGeometry),
            Miss(EAdvancedVisibilityStreamReuseMiss.Pass),
            Miss(EAdvancedVisibilityStreamReuseMiss.Context),
            Miss(EAdvancedVisibilityStreamReuseMiss.Submission),
            Miss(EAdvancedVisibilityStreamReuseMiss.DeformationOutput));
    }
}

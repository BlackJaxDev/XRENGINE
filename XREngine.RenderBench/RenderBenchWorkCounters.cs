namespace XREngine.RenderBench;

/// <summary>Exact bounded work performed during retained measured frames.</summary>
public readonly record struct RenderBenchWorkCounters(
    long Draws,
    long Dispatches,
    long Submissions,
    long CommandBuffers,
    long Descriptors,
    long Barriers,
    long UploadBytes,
    long PassIterations,
    long CommandBufferDecisions)
{
    public static RenderBenchWorkCounters operator +(RenderBenchWorkCounters left, RenderBenchWorkCounters right)
        => new(
            left.Draws + right.Draws,
            left.Dispatches + right.Dispatches,
            left.Submissions + right.Submissions,
            left.CommandBuffers + right.CommandBuffers,
            left.Descriptors + right.Descriptors,
            left.Barriers + right.Barriers,
            left.UploadBytes + right.UploadBytes,
            left.PassIterations + right.PassIterations,
            left.CommandBufferDecisions + right.CommandBufferDecisions);
}

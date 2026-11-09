namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Selects which video frame upload failures to log, so that a failure on every
/// frame cannot flood the log.
/// </summary>
/// <remarks>
/// The reporter selects the first failure for each distinct error text. After that,
/// it selects one failure in every <see cref="ReportInterval"/> failures. It keeps at
/// most <see cref="MaxDistinctErrors"/> error texts; other texts use the interval only.
/// Use one instance for each streaming session, on one thread.
/// </remarks>
internal sealed class VideoFrameUploadFailureReporter
{
    /// <summary>The number of failures between two reports of a known error text.</summary>
    public const int ReportInterval = 300;

    /// <summary>The maximum number of distinct error texts that are reported immediately.</summary>
    public const int MaxDistinctErrors = 16;

    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);

    /// <summary>The number of failures that this reporter recorded.</summary>
    public long FailureCount { get; private set; }

    /// <summary>The number of failures since the last report that were not reported.</summary>
    public long UnreportedFailureCount { get; private set; }

    /// <summary>
    /// Records one failed upload.
    /// </summary>
    /// <param name="error">The error text of the failure.</param>
    /// <param name="unreportedBefore">
    /// When the method returns <c>true</c>, the number of failures that were not reported before this one.
    /// </param>
    /// <returns><c>true</c> when the caller must log this failure.</returns>
    public bool RecordFailure(string error, out long unreportedBefore)
    {
        FailureCount++;
        bool firstOfItsKind = _reportedErrors.Count < MaxDistinctErrors && _reportedErrors.Add(error);
        if (!firstOfItsKind && UnreportedFailureCount + 1 < ReportInterval)
        {
            UnreportedFailureCount++;
            unreportedBefore = 0;
            return false;
        }

        unreportedBefore = UnreportedFailureCount;
        UnreportedFailureCount = 0;
        return true;
    }
}

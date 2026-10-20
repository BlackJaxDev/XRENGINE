namespace XREngine.Rendering.Commands;

/// <summary>
/// Cumulative counters of the prepared unchanged-row verdict, from
/// <see cref="AdvancedPreparedGateTelemetry.Capture"/>. All counts are
/// process-wide sums since the process started. Capture twice and use the differences.
/// </summary>
public readonly struct AdvancedPreparedGateTelemetrySnapshot
{
    private readonly long[]? _refusals;
    private readonly long[]? _misses;
    private readonly long[]? _verifyDisagreements;

    internal AdvancedPreparedGateTelemetrySnapshot(
        bool prepareEnabled,
        bool verifyEnabled,
        long prepared,
        long exceptions,
        long hits,
        long reevaluatedRows,
        long[] refusals,
        long[] misses,
        long[] verifyDisagreements,
        string? lastExceptionType,
        string? lastExceptionMessage,
        int lastExceptionHResult)
    {
        PrepareEnabled = prepareEnabled;
        VerifyEnabled = verifyEnabled;
        Prepared = prepared;
        Exceptions = exceptions;
        Hits = hits;
        ReevaluatedRows = reevaluatedRows;
        _refusals = refusals;
        _misses = misses;
        _verifyDisagreements = verifyDisagreements;
        RefusalTotal = Sum(refusals);
        MissTotal = Sum(misses);
        VerifyDisagreementTotal = Sum(verifyDisagreements);
        LastExceptionType = lastExceptionType;
        LastExceptionMessage = lastExceptionMessage;
        LastExceptionHResult = lastExceptionHResult;
    }

    /// <summary>Gets whether the collect thread prepares verdicts. XRE_ADVANCED_PREPARED_GATE=0 disables it.</summary>
    public bool PrepareEnabled { get; }

    /// <summary>Gets whether each hit also runs the swap-time gate. XRE_ADVANCED_PREPARED_GATE_VERIFY=1 enables it.</summary>
    public bool VerifyEnabled { get; }

    /// <summary>Gets the prepares that stored a verdict.</summary>
    public long Prepared { get; }

    /// <summary>Gets the prepares that threw an exception.</summary>
    public long Exceptions { get; }

    /// <summary>Gets the publications that accepted their verdict.</summary>
    public long Hits { get; }

    /// <summary>
    /// Gets the rows that the consumes evaluated again at the swap: the rows
    /// that the prepare flagged, and the rows whose command changed after the prepare.
    /// </summary>
    public long ReevaluatedRows { get; }

    /// <summary>Gets the prepares that stored no verdict, for all reasons.</summary>
    public long RefusalTotal { get; }

    /// <summary>Gets the publications that did not accept a verdict, for all reasons.</summary>
    public long MissTotal { get; }

    /// <summary>Gets the accepted verdicts that the swap-time gate rejected, for all reasons.</summary>
    public long VerifyDisagreementTotal { get; }

    /// <summary>Gets the type name of the last prepare exception, or null when no prepare threw.</summary>
    public string? LastExceptionType { get; }

    /// <summary>Gets the message of the last prepare exception, or null when no prepare threw.</summary>
    public string? LastExceptionMessage { get; }

    /// <summary>Gets the HRESULT of the last prepare exception, or 0 when no prepare threw.</summary>
    public int LastExceptionHResult { get; }

    /// <summary>Gets all prepares: the stored verdicts, the refusals, and the exceptions.</summary>
    public long PrepareCalls => Prepared + RefusalTotal + Exceptions;

    /// <summary>Gets all publications that reached the unchanged-row gate decision: the hits and the misses.</summary>
    public long ConsumeCalls => Hits + MissTotal;

    /// <summary>Gets the prepares that stored no verdict for <paramref name="reason"/>.</summary>
    public long GetRefusals(EAdvancedPreparedGateOutcome reason) => Read(_refusals, reason);

    /// <summary>Gets the publications that did not accept a verdict for <paramref name="reason"/>.</summary>
    public long GetMisses(EAdvancedPreparedGateOutcome reason) => Read(_misses, reason);

    /// <summary>Gets the accepted verdicts that the swap-time gate rejected for <paramref name="reason"/>.</summary>
    public long GetVerifyDisagreements(EAdvancedPreparedGateOutcome reason) => Read(_verifyDisagreements, reason);

    private static long Read(long[]? counts, EAdvancedPreparedGateOutcome reason)
        => counts is not null && (uint)reason < (uint)counts.Length ? counts[(int)reason] : 0L;

    private static long Sum(long[] counts)
    {
        long total = 0L;
        for (int index = 0; index < counts.Length; ++index)
            total += counts[index];
        return total;
    }
}

using System.Threading;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Always-on cumulative counters of the prepared unchanged-row verdict of the
/// Advanced resident-scene publication. Capture twice around a timed window and
/// use the differences.
/// </summary>
/// <remarks>
/// <para>
/// Each prepare records one result: a stored verdict, a refusal with its
/// reason, or an exception. Each world-swap publication that reaches the
/// unchanged-row gate decision records one consume result: a hit, or a miss
/// with its reason. This is the scope of the row-gate pass and failure counts
/// of the swap stage timers, so while the prepare is enabled the consume calls
/// equal the sum of those counts. A publication without a verdict records the miss reason
/// <see cref="EAdvancedPreparedGateOutcome.NotPrepared"/>. A publication
/// outside the world swap, and a verdict that a boundary growth discards
/// before the gate decision, record no consume result.
/// </para>
/// <para>
/// An exception in the prepare also records its type name, its HRESULT, and
/// its message, because the warning log does not exist in Release builds.
/// </para>
/// <para>
/// The writers use interlocked operations, so any thread can record. Only
/// <see cref="RecordException"/> can allocate, and only on its failure path.
/// </para>
/// </remarks>
public static class AdvancedPreparedGateTelemetry
{
    private const int ReasonCount = (int)EAdvancedPreparedGateOutcome.Count;

    private static readonly long[] s_refusals = new long[ReasonCount];
    private static readonly long[] s_misses = new long[ReasonCount];
    private static readonly long[] s_verifyDisagreements = new long[ReasonCount];
    private static long s_prepared;
    private static long s_hits;
    private static long s_reevaluatedRows;
    private static long s_exceptions;
    private static string? s_lastExceptionType;
    private static string? s_lastExceptionMessage;
    private static int s_lastExceptionHResult;

    /// <summary>Counts one prepare that stored a verdict.</summary>
    internal static void RecordPrepared()
        => Interlocked.Increment(ref s_prepared);

    /// <summary>Counts one prepare that stored no verdict, with its reason.</summary>
    internal static void RecordRefusal(EAdvancedPreparedGateOutcome reason)
        => Interlocked.Increment(ref s_refusals[ToIndex(reason)]);

    /// <summary>
    /// Counts one prepare that threw an exception, and keeps the type name,
    /// the HRESULT, and the message of <paramref name="exception"/>. The last
    /// exception replaces the values of an earlier one.
    /// </summary>
    internal static void RecordException(Exception exception)
    {
        Volatile.Write(ref s_lastExceptionType, exception.GetType().FullName);
        Volatile.Write(ref s_lastExceptionMessage, exception.Message);
        Volatile.Write(ref s_lastExceptionHResult, exception.HResult);
        Interlocked.Increment(ref s_exceptions);
    }

    /// <summary>Counts one publication that accepted its verdict.</summary>
    internal static void RecordHit()
        => Interlocked.Increment(ref s_hits);

    /// <summary>Counts one publication that did not accept a verdict, with its reason.</summary>
    internal static void RecordMiss(EAdvancedPreparedGateOutcome reason)
        => Interlocked.Increment(ref s_misses[ToIndex(reason)]);

    /// <summary>
    /// Adds the rows that one consume evaluated again at the swap: the rows
    /// that the prepare flagged, and the rows whose command changed after the
    /// prepare. These rows keep the swap-time cost of the row walk.
    /// </summary>
    internal static void RecordReevaluatedRows(int rows)
    {
        if (rows > 0)
            Interlocked.Add(ref s_reevaluatedRows, rows);
    }

    /// <summary>
    /// Counts one accepted verdict that the swap-time gate rejected, with the
    /// reason of the swap-time gate. The reason names the witness that should
    /// have detected the change.
    /// </summary>
    internal static void RecordVerifyDisagreement(EAdvancedPreparedGateOutcome reason)
        => Interlocked.Increment(ref s_verifyDisagreements[ToIndex(reason)]);

    /// <summary>
    /// Captures all cumulative counters. This method allocates the reason
    /// arrays of the snapshot. Do not call it on a frame path.
    /// </summary>
    public static AdvancedPreparedGateTelemetrySnapshot Capture()
        => new(
            AdvancedGpuScenePublisher.PreparedGateEnabled,
            AdvancedGpuScenePublisher.PreparedGateVerifyEnabled,
            Interlocked.Read(ref s_prepared),
            Interlocked.Read(ref s_exceptions),
            Interlocked.Read(ref s_hits),
            Interlocked.Read(ref s_reevaluatedRows),
            CopyCounts(s_refusals),
            CopyCounts(s_misses),
            CopyCounts(s_verifyDisagreements),
            Volatile.Read(ref s_lastExceptionType),
            Volatile.Read(ref s_lastExceptionMessage),
            Volatile.Read(ref s_lastExceptionHResult));

    private static long[] CopyCounts(long[] source)
    {
        long[] copy = new long[source.Length];
        for (int index = 0; index < source.Length; ++index)
            copy[index] = Interlocked.Read(ref source[index]);
        return copy;
    }

    // An out-of-range reason counts as None, so a record never throws.
    private static int ToIndex(EAdvancedPreparedGateOutcome reason)
        => (uint)reason < ReasonCount ? (int)reason : 0;
}

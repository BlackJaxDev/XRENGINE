using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// Process-wide counters of the static directional cascade cache: caster promotions and
/// demotions, static tile draws, tile copies, overlay frames, and the reasons why a frame
/// could not use the cached tile. The counters only grow. Every 5 seconds the lane writes
/// one line with the totals to the auxiliary log
/// <see cref="LogFileName"/>, so Release sessions record them too. No method allocates,
/// except the 5-second log line.
/// </summary>
public static class DirectionalShadowStaticCacheDiagnostics
{
    /// <summary>Auxiliary log file that receives the counter lines.</summary>
    public const string LogFileName = "directional-shadow-static-cache.log";

    private static readonly long[] s_causes =
        new long[(int)EDirectionalShadowStaticCacheCause.SameFrame + 1];
    private static long s_promotions;
    private static long s_demotions;
    private static long s_demotionBatches;
    private static int s_pendingDemotions;
    private static long s_staticRenders;
    private static long s_copies;
    private static long s_overlayFrames;
    private static long s_dynamicRecordDraws;
    private static long s_staticRecordCount;
    private static long s_pageLayerInitializations;
    private static long s_tilesCreated;
    private static long s_tilesReleased;
    private static long s_lastLogTimestamp;

    /// <summary>Casters that motion made dynamic.</summary>
    public static long Promotions => Interlocked.Read(ref s_promotions);
    /// <summary>Casters that a demotion batch returned to the static set.</summary>
    public static long Demotions => Interlocked.Read(ref s_demotions);
    /// <summary>Demotion batches.</summary>
    public static long DemotionBatches => Interlocked.Read(ref s_demotionBatches);
    /// <summary>Dynamic casters that are due for demotion and wait for a batch.</summary>
    public static int PendingDemotions => Volatile.Read(ref s_pendingDemotions);
    /// <summary>Draws of a static caster tile (full static renders).</summary>
    public static long StaticRenders => Interlocked.Read(ref s_staticRenders);
    /// <summary>Copies of a static caster tile into its cascade tile.</summary>
    public static long Copies => Interlocked.Read(ref s_copies);
    /// <summary>Cascade refreshes that copied the static tile without drawing it first.</summary>
    public static long OverlayFrames => Interlocked.Read(ref s_overlayFrames);
    /// <summary>Dynamic caster records drawn over a copied static tile.</summary>
    public static long DynamicRecordDraws => Interlocked.Read(ref s_dynamicRecordDraws);

    /// <summary>Returns how often <paramref name="cause"/> prevented the copy without a static draw.</summary>
    public static long GetCauseCount(EDirectionalShadowStaticCacheCause cause)
        => Interlocked.Read(ref s_causes[(int)cause]);

    internal static void RecordPromotion()
        => Interlocked.Increment(ref s_promotions);

    internal static void RecordDemotionBatch(int demotedCount)
    {
        Interlocked.Add(ref s_demotions, demotedCount);
        Interlocked.Increment(ref s_demotionBatches);
    }

    internal static void SetPendingDemotions(int count)
        => Volatile.Write(ref s_pendingDemotions, count);

    /// <summary>Counts one cause. The lane calls it once per group and frame that does not copy without a static draw.</summary>
    public static void RecordCause(EDirectionalShadowStaticCacheCause cause)
        => Interlocked.Increment(ref s_causes[(int)cause]);

    /// <summary>Counts a static tile draw with <paramref name="staticRecords"/> static caster records.</summary>
    public static void RecordStaticRender(int staticRecords)
    {
        Interlocked.Increment(ref s_staticRenders);
        Interlocked.Exchange(ref s_staticRecordCount, staticRecords);
    }

    /// <summary>Counts one copy of a static tile into its cascade tile; <paramref name="overlayOnly"/> is true when the frame did not draw the static tile.</summary>
    public static void RecordCopy(bool overlayOnly)
    {
        Interlocked.Increment(ref s_copies);
        if (overlayOnly)
            Interlocked.Increment(ref s_overlayFrames);
    }

    /// <summary>Page layers that the lane moved into the attachment layout before a copy, because they had no layout of their own yet.</summary>
    public static long PageLayerInitializations => Interlocked.Read(ref s_pageLayerInitializations);

    /// <summary>Static tiles created.</summary>
    public static long TilesCreated => Interlocked.Read(ref s_tilesCreated);
    /// <summary>Static tiles released after their cascade had no dynamic caster change for a while.</summary>
    public static long TilesReleased => Interlocked.Read(ref s_tilesReleased);

    internal static void RecordTileCreated()
        => Interlocked.Increment(ref s_tilesCreated);

    internal static void RecordTileReleased()
        => Interlocked.Increment(ref s_tilesReleased);

    /// <summary>Counts one page layer initialization before a copy.</summary>
    public static void RecordPageLayerInitialization()
        => Interlocked.Increment(ref s_pageLayerInitializations);

    /// <summary>Counts dynamic caster records drawn over a copied static tile.</summary>
    public static void RecordDynamicDraws(int records)
        => Interlocked.Add(ref s_dynamicRecordDraws, records);

    /// <summary>
    /// Writes one line with all totals to <see cref="LogFileName"/> when 5 seconds have
    /// passed since the last line. Call it from the render thread.
    /// </summary>
    public static void WriteIfDue()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref s_lastLogTimestamp);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(5))
            return;
        if (Interlocked.CompareExchange(ref s_lastLogTimestamp, now, last) != last)
            return;

        XREngine.Debug.WriteAuxiliaryLog(
            LogFileName,
            $"{DateTimeOffset.Now:O} DirectionalShadow.StaticCache: " +
            $"staticRenders={StaticRenders} copies={Copies} overlayFrames={OverlayFrames} " +
            $"dynamicRecordDraws={DynamicRecordDraws} tiles={TilesCreated - TilesReleased} tilesCreated={TilesCreated} tilesReleased={TilesReleased} pageLayerInitializations={PageLayerInitializations} lastStaticRecords={Interlocked.Read(ref s_staticRecordCount)} " +
            $"promotions={Promotions} demotions={Demotions} demotionBatches={DemotionBatches} pendingDemotions={PendingDemotions} " +
            $"causes: disabled={GetCauseCount(EDirectionalShadowStaticCacheCause.Disabled)} " +
            $"outOfScope={GetCauseCount(EDirectionalShadowStaticCacheCause.OutOfScope)} " +
            $"noDynamicCasters={GetCauseCount(EDirectionalShadowStaticCacheCause.NoDynamicCasters)} " +
            $"gpuCasterSelection={GetCauseCount(EDirectionalShadowStaticCacheCause.GpuCasterSelection)} " +
            $"tileUnavailable={GetCauseCount(EDirectionalShadowStaticCacheCause.TileUnavailable)} " +
            $"noStaticContent={GetCauseCount(EDirectionalShadowStaticCacheCause.NoStaticContent)} " +
            $"fitMismatch={GetCauseCount(EDirectionalShadowStaticCacheCause.FitMismatch)} " +
            $"tileMismatch={GetCauseCount(EDirectionalShadowStaticCacheCause.TileMismatch)} " +
            $"staticEpochChanged={GetCauseCount(EDirectionalShadowStaticCacheCause.StaticEpochChanged)} " +
            $"recordSetChanged={GetCauseCount(EDirectionalShadowStaticCacheCause.RecordSetChanged)} " +
            $"sameFrame={GetCauseCount(EDirectionalShadowStaticCacheCause.SameFrame)}");
    }
}

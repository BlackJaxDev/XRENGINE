using System.Diagnostics;
using System.Runtime.CompilerServices;
using XREngine.Components.Lights;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// Always-on cumulative counters that show how the directional lights collect, swap, submit and render their primary
/// shadow view and their cascades. Capture twice around a timed window and use the differences.
/// </summary>
/// <remarks>
/// <para>
/// The collect thread writes most counters. The render thread writes the tile and legacy render counters. The render
/// thread also writes the primary collection and swap counters when a render path forces a collection with
/// <c>collectVisibleNow</c>, while the collect thread collects the next frame. All writes therefore use interlocked
/// operations. No method allocates managed memory.
/// </para>
/// <para>
/// This class keeps no collection time. <see cref="CollectVisibleStageTimings"/> keeps the primary shadow and cascade
/// view times, and it records only the collections in the collect section of the engine timer. The counts in this
/// class also include the collections on other threads, so the two sets of counts can differ.
/// <see cref="DirectionalShadowConsumptionSnapshot.PrimaryTileCollectNow"/>,
/// <see cref="DirectionalCascadeConsumptionCounts.RenderCollectNow"/> and
/// <see cref="DirectionalLegacyShadowRenderCounts.CollectNow"/> count the render-thread collections. This class keeps
/// the primary swap time, because no other counter splits it from the directional light swaps.
/// </para>
/// <para>
/// <see cref="DirectionalLegacyShadowRenderCounts.RenderCalls"/> and <see cref="DirectionalLegacyShadowRenderCounts.PrimaryRenders"/>
/// mirror the per-light <see cref="DirectionalLightComponent.StandaloneShadowRenderRequestCount"/> and
/// <see cref="DirectionalLightComponent.StandaloneShadowRenderPassCount"/> as sums for all directional lights, so that
/// one static read gets them.
/// </para>
/// <para>
/// The render thread reads the published backend-ready frame package of the primary shadow view only when that view
/// renders. The shadow atlas tile render and the legacy primary render call the view render, and the view render
/// validates the package and records its draws. No other render-thread code reads that package.
/// <see cref="DirectionalShadowConsumptionSnapshot.PrimaryPackageRenderReads"/> counts these view renders.
/// </para>
/// </remarks>
public static class DirectionalShadowConsumptionTelemetry
{
    private static readonly long[] s_values = new long[(int)EDirectionalShadowConsumptionCounter.Count];

    /// <summary>Adds one to <paramref name="counter"/>. Any thread can call it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Count(EDirectionalShadowConsumptionCounter counter)
        => Interlocked.Increment(ref s_values[(int)counter]);

    /// <summary>Returns a stopwatch timestamp to give to <see cref="RecordPrimarySwap"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Timestamp()
        => Stopwatch.GetTimestamp();

    /// <summary>
    /// Counts one primary shadow view swap, adds its time since <paramref name="startTicks"/>, and adds the mesh
    /// commands of its rendering buffer.
    /// </summary>
    internal static void RecordPrimarySwap(long startTicks, int swappedMeshCommands)
    {
        long elapsed = Stopwatch.GetTimestamp() - startTicks;
        Interlocked.Add(ref s_values[(int)EDirectionalShadowConsumptionCounter.PrimarySwapTicks], elapsed);
        Interlocked.Add(ref s_values[(int)EDirectionalShadowConsumptionCounter.PrimarySwappedMeshCommands], swappedMeshCommands);
        Count(EDirectionalShadowConsumptionCounter.PrimarySwaps);
    }

    /// <summary>Counts the first term that made the primary shadow view necessary at one light collection.</summary>
    internal static void CountPrimaryNeed(EDirectionalPrimaryShadowNeed need)
        => Count(need switch
        {
            EDirectionalPrimaryShadowNeed.CascadesDisabled => EDirectionalShadowConsumptionCounter.NeedCascadesDisabled,
            EDirectionalPrimaryShadowNeed.NoCascadeReceiver => EDirectionalShadowConsumptionCounter.NeedNoCascadeReceiver,
            EDirectionalPrimaryShadowNeed.NoPublishedCascades => EDirectionalShadowConsumptionCounter.NeedNoPublishedCascades,
            EDirectionalPrimaryShadowNeed.NoRenderWorld => EDirectionalShadowConsumptionCounter.NeedNoRenderWorld,
            EDirectionalPrimaryShadowNeed.PrimaryViewport => EDirectionalShadowConsumptionCounter.NeedPrimaryViewport,
            EDirectionalPrimaryShadowNeed.NoRelevantViewport => EDirectionalShadowConsumptionCounter.NeedNoRelevantViewport,
            EDirectionalPrimaryShadowNeed.PendingShadowCapture => EDirectionalShadowConsumptionCounter.NeedPendingShadowCapture,
            EDirectionalPrimaryShadowNeed.ShadowOnlyCasters => EDirectionalShadowConsumptionCounter.NeedShadowOnlyCasters,
            _ => EDirectionalShadowConsumptionCounter.NeedNone,
        });

    /// <summary>
    /// Counts one primary submission decision of the atlas planning. When <paramref name="cascadesActive"/> is true,
    /// the planning ran the viewport test, and this method also counts its result and the reason for a true result.
    /// </summary>
    internal static void CountAtlasPrimaryDecision(bool cascadesActive, bool needsPrimaryViewport, bool noRelevantViewport)
    {
        Count(EDirectionalShadowConsumptionCounter.AtlasPrimaryDecisions);
        if (!cascadesActive)
            return;

        Count(needsPrimaryViewport
            ? EDirectionalShadowConsumptionCounter.NeedsPrimaryViewportTrue
            : EDirectionalShadowConsumptionCounter.NeedsPrimaryViewportFalse);
        if (noRelevantViewport)
            Count(EDirectionalShadowConsumptionCounter.NeedsPrimaryViewportNoRelevantViewport);
    }

    /// <summary>Counts the result of one primary atlas request submission.</summary>
    internal static void CountAtlasPrimarySubmit(bool accepted, bool dirty)
    {
        if (accepted)
            Count(EDirectionalShadowConsumptionCounter.AtlasPrimarySubmitted);
        if (dirty)
            Count(EDirectionalShadowConsumptionCounter.AtlasPrimaryDirty);
    }

    /// <summary>Captures all cumulative counts. Times are in milliseconds.</summary>
    public static DirectionalShadowConsumptionSnapshot Capture()
        => new(
            Read(EDirectionalShadowConsumptionCounter.ShadowCollectPasses),
            Read(EDirectionalShadowConsumptionCounter.LightCollectCalls),
            Read(EDirectionalShadowConsumptionCounter.PrimaryCollections),
            Read(EDirectionalShadowConsumptionCounter.PrimarySwaps),
            ToMilliseconds(Read(EDirectionalShadowConsumptionCounter.PrimarySwapTicks)),
            Read(EDirectionalShadowConsumptionCounter.PrimarySwappedMeshCommands),
            Read(EDirectionalShadowConsumptionCounter.AtlasPrimaryDecisions),
            Read(EDirectionalShadowConsumptionCounter.NeedsPrimaryViewportTrue),
            Read(EDirectionalShadowConsumptionCounter.NeedsPrimaryViewportNoRelevantViewport),
            Read(EDirectionalShadowConsumptionCounter.NeedsPrimaryViewportFalse),
            Read(EDirectionalShadowConsumptionCounter.AtlasPrimarySubmitted),
            Read(EDirectionalShadowConsumptionCounter.AtlasPrimaryDirty),
            Read(EDirectionalShadowConsumptionCounter.PrimaryTileRenderAttempts),
            Read(EDirectionalShadowConsumptionCounter.PrimaryTileCollectNow),
            Read(EDirectionalShadowConsumptionCounter.PrimaryTileRenders),
            Read(EDirectionalShadowConsumptionCounter.PrimaryPackageRenderReads),
            new DirectionalPrimaryShadowNeedCounts(
                Read(EDirectionalShadowConsumptionCounter.NeedNone),
                Read(EDirectionalShadowConsumptionCounter.NeedCascadesDisabled),
                Read(EDirectionalShadowConsumptionCounter.NeedNoCascadeReceiver),
                Read(EDirectionalShadowConsumptionCounter.NeedNoPublishedCascades),
                Read(EDirectionalShadowConsumptionCounter.NeedNoRenderWorld),
                Read(EDirectionalShadowConsumptionCounter.NeedPrimaryViewport),
                Read(EDirectionalShadowConsumptionCounter.NeedNoRelevantViewport),
                Read(EDirectionalShadowConsumptionCounter.NeedPendingShadowCapture),
                Read(EDirectionalShadowConsumptionCounter.NeedShadowOnlyCasters)),
            new DirectionalLegacyShadowRenderCounts(
                Read(EDirectionalShadowConsumptionCounter.LegacyRenderCalls),
                Read(EDirectionalShadowConsumptionCounter.LegacyCollectNow),
                Read(EDirectionalShadowConsumptionCounter.LegacyPrimaryRenderAttempts),
                Read(EDirectionalShadowConsumptionCounter.LegacyPrimaryRenders),
                Read(EDirectionalShadowConsumptionCounter.LegacyCascadeRenderCalls)),
            new DirectionalCascadeConsumptionCounts(
                Read(EDirectionalShadowConsumptionCounter.CascadeSourceCollectCalls),
                Read(EDirectionalShadowConsumptionCounter.CascadeSkipDisabled),
                Read(EDirectionalShadowConsumptionCounter.CascadeSkipNoCascades),
                Read(EDirectionalShadowConsumptionCounter.CascadeSkipNoAtlasRenderRequest),
                Read(EDirectionalShadowConsumptionCounter.CascadeGpuPackages),
                Read(EDirectionalShadowConsumptionCounter.CascadeGroupCollections),
                Read(EDirectionalShadowConsumptionCounter.CascadeGroupSkipNoCollectionRequest),
                Read(EDirectionalShadowConsumptionCounter.CascadeViewportCollections),
                Read(EDirectionalShadowConsumptionCounter.CascadeTileRenders),
                Read(EDirectionalShadowConsumptionCounter.CascadeGroupRenders),
                Read(EDirectionalShadowConsumptionCounter.CascadeRenderCollectNow)),
            Read(EDirectionalShadowConsumptionCounter.PrimaryTileStaleDeclines));

    private static long Read(EDirectionalShadowConsumptionCounter counter)
        => Interlocked.Read(ref s_values[(int)counter]);

    private static double ToMilliseconds(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;
}

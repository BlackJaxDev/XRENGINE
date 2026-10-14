using System.Diagnostics;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private enum DispatcherStage
    {
        Total,
        PollReceipts,
        ConfirmSpatial,
        AcceptInputs,
        PrepareArena,
        Simulation,
        SelectiveReadback,
        CollectBindings,
        PaletteMatrices,
        PreviousPalette,
        Bounds,
        Commit,
        FinalizeOwnership,
        PaletteSources,
        CompleteUpdates,
        BoundsRoutes,
        BoundsRows,
        BoundsSceneCopy,
        SimTreeParams,
        SimActiveWork,
        SimUploadRanges,
        SimUploadColliders,
        SimUploadOther,
        Count,
    }

    // Only the render thread writes these cumulative timestamp sums. A 64-bit read is not torn.
    private readonly long[] _stageTicks = new long[(int)DispatcherStage.Count];
    private long _stageCalls;

    /// <summary>Adds the time since <paramref name="startTicks"/> to one stage.</summary>
    private void AddStageTime(DispatcherStage stage, long startTicks)
        => _stageTicks[(int)stage] += Stopwatch.GetTimestamp() - startTicks;

    /// <summary>Captures the cumulative stage times of the shared dispatcher.</summary>
    public static PhysicsChainDispatcherStageTimings CaptureStageTimings()
        => Instance.GetStageTimings();

    /// <summary>Captures the cumulative render-thread time of each dispatcher stage.</summary>
    public PhysicsChainDispatcherStageTimings GetStageTimings()
    {
        double ToMs(DispatcherStage stage)
            => Volatile.Read(ref _stageTicks[(int)stage]) * 1000.0 / Stopwatch.Frequency;
        return new(Volatile.Read(ref _stageCalls),
            ToMs(DispatcherStage.Total), ToMs(DispatcherStage.PollReceipts),
            ToMs(DispatcherStage.ConfirmSpatial), ToMs(DispatcherStage.AcceptInputs),
            ToMs(DispatcherStage.PrepareArena), ToMs(DispatcherStage.Simulation),
            ToMs(DispatcherStage.SelectiveReadback), ToMs(DispatcherStage.CollectBindings),
            ToMs(DispatcherStage.PaletteMatrices), ToMs(DispatcherStage.PreviousPalette),
            ToMs(DispatcherStage.Bounds), ToMs(DispatcherStage.Commit),
            ToMs(DispatcherStage.FinalizeOwnership), ToMs(DispatcherStage.PaletteSources),
            ToMs(DispatcherStage.CompleteUpdates),
            new PhysicsChainDispatcherSubStageTimings(
                ToMs(DispatcherStage.BoundsRoutes), ToMs(DispatcherStage.BoundsRows),
                ToMs(DispatcherStage.BoundsSceneCopy), ToMs(DispatcherStage.SimTreeParams),
                ToMs(DispatcherStage.SimActiveWork), ToMs(DispatcherStage.SimUploadRanges),
                ToMs(DispatcherStage.SimUploadColliders), ToMs(DispatcherStage.SimUploadOther)));
    }
}

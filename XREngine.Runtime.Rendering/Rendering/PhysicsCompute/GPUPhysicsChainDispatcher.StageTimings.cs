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
        CommitBackendBatch,
        CommitPageLockWait,
        CommitFence,
        CommitPageState,
        CommitCollectNotifications,
        CommitNotify,
        Count,
    }

    // Only the render thread writes these cumulative timestamp sums and counts. A 64-bit read
    // is not torn.
    private readonly long[] _stageTicks = new long[(int)DispatcherStage.Count];
    private long _stageCalls;
    private long _commitNotificationCount;

    /// <summary>Adds the time since <paramref name="startTicks"/> to one stage.</summary>
    private void AddStageTime(DispatcherStage stage, long startTicks)
        => _stageTicks[(int)stage] += Stopwatch.GetTimestamp() - startTicks;

    /// <summary>
    /// Adds the time since <paramref name="mark"/> to one stage and moves the mark to now.
    /// Sequential marks on one mark value make contiguous stages with no gap between them.
    /// </summary>
    private void MarkStageTime(DispatcherStage stage, ref long mark)
    {
        long now = Stopwatch.GetTimestamp();
        _stageTicks[(int)stage] += now - mark;
        mark = now;
    }

    /// <summary>Captures the cumulative stage times of the shared dispatcher.</summary>
    public static PhysicsChainDispatcherStageTimings CaptureStageTimings()
        => Instance.GetStageTimings();

    /// <summary>Captures the cumulative render-thread time of each dispatcher stage.</summary>
    public PhysicsChainDispatcherStageTimings GetStageTimings()
    {
        double ToMs(DispatcherStage stage)
            => Volatile.Read(ref _stageTicks[(int)stage]) * 1000.0 / Stopwatch.Frequency;

        // Read the commit stage one time, so that the top-level value and the remainder agree.
        double commit = ToMs(DispatcherStage.Commit);
        double commitBackendBatch = ToMs(DispatcherStage.CommitBackendBatch);
        double commitPageLockWait = ToMs(DispatcherStage.CommitPageLockWait);
        double commitFence = ToMs(DispatcherStage.CommitFence);
        double commitPageState = ToMs(DispatcherStage.CommitPageState);
        double commitCollectNotifications = ToMs(DispatcherStage.CommitCollectNotifications);
        double commitNotify = ToMs(DispatcherStage.CommitNotify);
        double commitOther = commit - (commitBackendBatch + commitPageLockWait + commitFence
            + commitPageState + commitCollectNotifications + commitNotify);
        return new(Volatile.Read(ref _stageCalls),
            ToMs(DispatcherStage.Total), ToMs(DispatcherStage.PollReceipts),
            ToMs(DispatcherStage.ConfirmSpatial), ToMs(DispatcherStage.AcceptInputs),
            ToMs(DispatcherStage.PrepareArena), ToMs(DispatcherStage.Simulation),
            ToMs(DispatcherStage.SelectiveReadback), ToMs(DispatcherStage.CollectBindings),
            ToMs(DispatcherStage.PaletteMatrices), ToMs(DispatcherStage.PreviousPalette),
            ToMs(DispatcherStage.Bounds), commit,
            ToMs(DispatcherStage.FinalizeOwnership), ToMs(DispatcherStage.PaletteSources),
            ToMs(DispatcherStage.CompleteUpdates),
            new PhysicsChainDispatcherSubStageTimings(
                ToMs(DispatcherStage.BoundsRoutes), ToMs(DispatcherStage.BoundsRows),
                ToMs(DispatcherStage.BoundsSceneCopy), ToMs(DispatcherStage.SimTreeParams),
                ToMs(DispatcherStage.SimActiveWork), ToMs(DispatcherStage.SimUploadRanges),
                ToMs(DispatcherStage.SimUploadColliders), ToMs(DispatcherStage.SimUploadOther),
                commitBackendBatch, commitPageLockWait, commitFence, commitPageState,
                commitCollectNotifications, commitNotify, commitOther,
                Volatile.Read(ref _outputProducerEpoch),
                Volatile.Read(ref _commitNotificationCount)));
    }
}

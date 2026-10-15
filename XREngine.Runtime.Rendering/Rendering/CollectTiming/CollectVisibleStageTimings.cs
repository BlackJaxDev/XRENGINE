using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Always-on cumulative stage times of the collect phase and of the serial swap of the engine timer, with
/// per-view path times, row counts, allocated bytes, Advanced unchanged-row gate results and frame handoff
/// counters. Capture twice around a timed window and use the differences.
/// </summary>
/// <remarks>
/// <para>
/// A thread records stage times only while it has an open section. The engine timer opens the collect section
/// in its collect dispatch, and the swap section in the serial swap of the threaded loop. The world swap
/// switches the swap section to <see cref="ECollectVisibleSection.WorldSwap"/>. Calls from other threads, for
/// example a render-thread collection or a unit test, find no open section and record nothing.
/// <see cref="Shadows.DirectionalShadowConsumptionTelemetry"/> counts the directional shadow collections on all
/// threads, so its primary shadow and cascade counts can be larger than the view calls of this class.
/// </para>
/// <para>
/// Only one thread at a time has an open section, so the stage sums use plain adds with no interlocked
/// operation. A reader gets each 64-bit sum with <see cref="Volatile.Read(ref long)"/>. A tick sum and its call
/// count can differ by one frame. If visibility collection ever runs on more than one thread at a time, the
/// stage writes must change to interlocked operations.
/// </para>
/// <para>
/// The frame handoff counters are different: the render thread and the collect thread write them with
/// interlocked operations. No method allocates managed memory.
/// </para>
/// </remarks>
public static class CollectVisibleStageTimings
{
    private const int StageCount = (int)ECollectVisibleStage.Count;
    private const int ViewKindCount = (int)ECollectVisibleViewKind.Count;
    private const int PathCount = (int)ECollectVisiblePath.Count;

    private static readonly long[] s_ticks = new long[StageCount];
    private static readonly long[] s_calls = new long[StageCount];
    private static readonly long[] s_viewTicks = new long[ViewKindCount];
    private static readonly long[] s_viewCalls = new long[ViewKindCount];
    private static readonly long[] s_viewRows = new long[ViewKindCount];
    private static readonly long[] s_pathTicks = new long[ViewKindCount * PathCount];

    private static long s_collectAllocatedBytes;
    private static long s_swapAllocatedBytes;
    private static long s_skinnedMeshRows;
    private static long s_gpuBoundsEligibleRows;
    private static long s_coveredCommandSourceRows;

    private static long s_advancedRowGatePasses;
    private static long s_advancedRowGateFailures;
    private static long s_advancedUnchangedRowPublications;

    private static long s_renderUntilCollectReleaseTicks;
    private static long s_renderUntilCollectReleaseCalls;
    private static long s_collectHandoffIterations;
    private static long s_collectFinishedAfterRelease;

    [ThreadStatic]
    private static ECollectVisibleSection t_section;

    [ThreadStatic]
    private static ECollectVisibleViewKind t_viewKind;

    [ThreadStatic]
    private static bool t_pathOpen;

    [ThreadStatic]
    private static long t_sectionStartAllocatedBytes;

    /// <summary>Opens the collect section on this thread. The engine timer calls this at the start of its collect dispatch.</summary>
    internal static void BeginCollect()
        => OpenSection(ECollectVisibleSection.Collect);

    /// <summary>
    /// Adds the collect-phase total and the allocated bytes, then closes the section on this thread.
    /// The engine timer passes the same timestamps that it gives to the collect-phase total of the frame lifecycle.
    /// </summary>
    internal static void EndCollect(long startTicks, long endTicks)
    {
        if (t_section == ECollectVisibleSection.Collect)
        {
            AddStage(ECollectVisibleStage.CollectTotal, endTicks - startTicks);
            s_collectAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - t_sectionStartAllocatedBytes;
        }

        ResetScope();
    }

    /// <summary>Opens the swap section on this thread. The threaded loop calls this at the start of its serial swap.</summary>
    internal static void BeginSwap()
        => OpenSection(ECollectVisibleSection.Swap);

    /// <summary>
    /// Adds the serial swap total and the allocated bytes, then closes the section on this thread.
    /// The threaded loop passes the same timestamps that it gives to the swap-phase total of the frame lifecycle.
    /// </summary>
    internal static void EndSwap(long startTicks, long endTicks)
    {
        if (t_section == ECollectVisibleSection.Swap)
        {
            AddStage(ECollectVisibleStage.SwapTotal, endTicks - startTicks);
            s_swapAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - t_sectionStartAllocatedBytes;
        }

        ResetScope();
    }

    /// <summary>Closes any open section on this thread without a record. Use it on failure and exit paths.</summary>
    internal static void ResetScope()
    {
        t_section = ECollectVisibleSection.None;
        t_viewKind = ECollectVisibleViewKind.Other;
        t_pathOpen = false;
    }

    /// <summary>Returns a start timestamp when <paramref name="section"/> is open on this thread, otherwise 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long StartIn(ECollectVisibleSection section)
        => t_section == section ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>
    /// Adds the time since <paramref name="mark"/> to <paramref name="stage"/> and moves the mark to now.
    /// A mark of 0 records nothing.
    /// </summary>
    internal static void Mark(ref long mark, ECollectVisibleStage stage)
    {
        if (mark == 0L)
            return;

        long now = Stopwatch.GetTimestamp();
        AddStage(stage, now - mark);
        mark = now;
    }

    /// <summary>Moves a nonzero mark to now without a record, so that the work since the mark stays in the parent residual.</summary>
    internal static void Restart(ref long mark)
    {
        if (mark != 0L)
            mark = Stopwatch.GetTimestamp();
    }

    /// <summary>Adds the time since <paramref name="startTicks"/> to <paramref name="stage"/>. A start of 0 records nothing.</summary>
    internal static void End(ECollectVisibleStage stage, long startTicks)
    {
        if (startTicks == 0L)
            return;

        AddStage(stage, Stopwatch.GetTimestamp() - startTicks);
    }

    /// <summary>
    /// Adds the time since an existing timestamp to <paramref name="stage"/> when <paramref name="section"/> is open on this thread.
    /// </summary>
    internal static void EndIn(ECollectVisibleSection section, ECollectVisibleStage stage, long startTicks)
    {
        if (t_section != section)
            return;

        AddStage(stage, Stopwatch.GetTimestamp() - startTicks);
    }

    /// <summary>
    /// Switches the swap section to the world swap section and returns the previous section.
    /// Any other section stays the same. Give the result to <see cref="ExitWorldSwap"/>.
    /// </summary>
    internal static ECollectVisibleSection EnterWorldSwap()
    {
        ECollectVisibleSection previous = t_section;
        if (previous == ECollectVisibleSection.Swap)
            t_section = ECollectVisibleSection.WorldSwap;
        return previous;
    }

    /// <summary>Restores the swap section after <see cref="EnterWorldSwap"/> switched it.</summary>
    internal static void ExitWorldSwap(ECollectVisibleSection previous)
    {
        if (previous == ECollectVisibleSection.Swap && t_section == ECollectVisibleSection.WorldSwap)
            t_section = previous;
    }

    /// <summary>
    /// Sets the view kind for the path times that follow, when the collect section is open, and returns the previous kind.
    /// Give the result to <see cref="EndView(ECollectVisibleViewKind, long)"/>.
    /// </summary>
    internal static ECollectVisibleViewKind EnterView(ECollectVisibleViewKind kind)
    {
        ECollectVisibleViewKind previous = t_viewKind;
        if (t_section == ECollectVisibleSection.Collect)
            t_viewKind = kind;
        return previous;
    }

    /// <summary>Adds the view time since <paramref name="startTicks"/> to the current view kind and restores the previous kind.</summary>
    internal static void EndView(ECollectVisibleViewKind previous, long startTicks)
    {
        if (startTicks == 0L)
        {
            t_viewKind = previous;
            return;
        }

        EndView(previous, startTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Adds the view time between two existing timestamps to the current view kind and restores the previous kind.
    /// It records only in the collect section.
    /// </summary>
    internal static void EndView(ECollectVisibleViewKind previous, long startTicks, long endTicks)
    {
        int kind = (int)t_viewKind;
        t_viewKind = previous;
        if (startTicks == 0L || t_section != ECollectVisibleSection.Collect)
            return;

        s_viewTicks[kind] += endTicks - startTicks;
        ++s_viewCalls[kind];
    }

    /// <summary>
    /// Opens a collection path and returns its start timestamp. It returns 0 outside the collect section and when a
    /// path is already open, so a nested collection stays part of the outer path.
    /// </summary>
    internal static long StartPath()
    {
        if (t_section != ECollectVisibleSection.Collect || t_pathOpen)
            return 0L;

        t_pathOpen = true;
        return Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Adds the path time and the accepted rows to the current view kind and closes the path.
    /// A start of 0 records nothing and leaves an outer path open.
    /// </summary>
    internal static void EndPath(ECollectVisiblePath path, long startTicks, int rows)
    {
        if (startTicks == 0L)
            return;

        long ticks = Stopwatch.GetTimestamp() - startTicks;
        t_pathOpen = false;
        int kind = (int)t_viewKind;
        s_pathTicks[kind * PathCount + (int)path] += ticks;
        s_viewRows[kind] += rows;
    }

    /// <summary>
    /// Adds the world pre-collect split from four timestamps that the caller already took.
    /// It records only in the collect section.
    /// </summary>
    internal static void AddPreCollect(long matrixStartTicks, long meshStartTicks, long sceneStartTicks, long sceneEndTicks)
    {
        if (t_section != ECollectVisibleSection.Collect)
            return;

        AddStage(ECollectVisibleStage.PreCollectTransforms, meshStartTicks - matrixStartTicks);
        AddStage(ECollectVisibleStage.PreCollectPendingMeshMatrices, sceneStartTicks - meshStartTicks);
        AddStage(ECollectVisibleStage.PreCollectScene, sceneEndTicks - sceneStartTicks);
    }

    /// <summary>
    /// Adds the world swap split from six timestamps that the caller already took.
    /// It records only in the world swap section.
    /// </summary>
    internal static void AddWorldSwap(
        long matrixStartTicks,
        long meshStartTicks,
        long lightsStartTicks,
        long captureStartTicks,
        long sceneStartTicks,
        long sceneEndTicks)
    {
        if (t_section != ECollectVisibleSection.WorldSwap)
            return;

        AddStage(ECollectVisibleStage.SwapTransforms, meshStartTicks - matrixStartTicks);
        AddStage(ECollectVisibleStage.SwapPendingMeshMatrices, lightsStartTicks - meshStartTicks);
        AddStage(ECollectVisibleStage.SwapLights, captureStartTicks - lightsStartTicks);
        AddStage(ECollectVisibleStage.SwapAdvancedGlobalCapture, sceneStartTicks - captureStartTicks);
        AddStage(ECollectVisibleStage.SwapScene, sceneEndTicks - sceneStartTicks);
    }

    /// <summary>
    /// Adds the list sizes of one scene refresh: the visited skinned meshes, the GPU-bounds eligible renderables and
    /// the covered command sources. It records only in the collect section.
    /// </summary>
    internal static void AddSceneRefreshRows(int skinnedMeshes, int gpuBoundsEligible, int coveredCommandSources)
    {
        if (t_section != ECollectVisibleSection.Collect)
            return;

        s_skinnedMeshRows += skinnedMeshes;
        s_gpuBoundsEligibleRows += gpuBoundsEligible;
        s_coveredCommandSourceRows += coveredCommandSources;
    }

    /// <summary>
    /// Adds the unchanged-row gate time since <paramref name="mark"/> to <see cref="ECollectVisibleStage.SwapAdvancedRowGate"/>,
    /// counts the gate result and moves the mark to now. A mark of 0 records nothing.
    /// </summary>
    /// <remarks>
    /// Get the mark with <see cref="StartIn"/> and <see cref="ECollectVisibleSection.WorldSwap"/>, the same section as the
    /// parent stage. Then the gate records only for the publications that the parent stage records.
    /// </remarks>
    internal static void MarkAdvancedRowGate(ref long mark, bool passed)
    {
        if (mark == 0L)
            return;

        Mark(ref mark, ECollectVisibleStage.SwapAdvancedRowGate);
        if (passed)
            ++s_advancedRowGatePasses;
        else
            ++s_advancedRowGateFailures;
    }

    /// <summary>
    /// Counts one Advanced publication that the unchanged-row path completed after the gate passed.
    /// A mark of 0 records nothing.
    /// </summary>
    internal static void CountAdvancedUnchangedRowPublication(long mark)
    {
        if (mark != 0L)
            ++s_advancedUnchangedRowPublications;
    }

    /// <summary>
    /// Adds the time from the start of a render dispatch to the first release of the collect thread in that frame.
    /// Any thread can call it.
    /// </summary>
    internal static void RecordRenderUntilCollectRelease(long ticks)
    {
        Interlocked.Add(ref s_renderUntilCollectReleaseTicks, ticks);
        Interlocked.Increment(ref s_renderUntilCollectReleaseCalls);
    }

    /// <summary>
    /// Counts one collect iteration at its wait for the render thread. <paramref name="renderAlreadyReleased"/> is true
    /// when the render thread released the collect thread before the collect phase ended.
    /// </summary>
    internal static void CountCollectHandoff(bool renderAlreadyReleased)
    {
        Interlocked.Increment(ref s_collectHandoffIterations);
        if (renderAlreadyReleased)
            Interlocked.Increment(ref s_collectFinishedAfterRelease);
    }

    /// <summary>Captures all cumulative values. Times are in milliseconds.</summary>
    public static CollectVisibleStageTimingSnapshot Capture()
        => new(
            Calls(ECollectVisibleStage.CollectTotal),
            Calls(ECollectVisibleStage.SwapTotal),
            Ms(ECollectVisibleStage.CollectTotal),
            Ms(ECollectVisibleStage.PreCollectEvent),
            Ms(ECollectVisibleStage.CollectEvent),
            Ms(ECollectVisibleStage.PostCollectEvent),
            Ms(ECollectVisibleStage.PrepareSwapPublicationEvent),
            Volatile.Read(ref s_collectAllocatedBytes),
            Ms(ECollectVisibleStage.SwapTotal),
            Volatile.Read(ref s_swapAllocatedBytes),
            new CollectVisiblePreCollectStageTimings(
                Calls(ECollectVisibleStage.PreCollectTransforms),
                Ms(ECollectVisibleStage.PreCollectTransforms),
                Ms(ECollectVisibleStage.PreCollectTransformStorePublish),
                Ms(ECollectVisibleStage.PreCollectPendingMeshMatrices),
                Ms(ECollectVisibleStage.PreCollectScene),
                Calls(ECollectVisibleStage.SceneCpuTreeSwap),
                Ms(ECollectVisibleStage.SceneRenderableOperations),
                Ms(ECollectVisibleStage.SceneGpuBoundsEligibility),
                Ms(ECollectVisibleStage.SceneCoveredCommandRefresh),
                Ms(ECollectVisibleStage.SceneSkinnedCpuBounds),
                Ms(ECollectVisibleStage.SceneCpuTreeSwap),
                Volatile.Read(ref s_skinnedMeshRows),
                Volatile.Read(ref s_gpuBoundsEligibleRows),
                Volatile.Read(ref s_coveredCommandSourceRows)),
            new CollectVisibleLightStageTimings(
                Calls(ECollectVisibleStage.LightsShadowCollect),
                Ms(ECollectVisibleStage.LightsPlanning),
                Ms(ECollectVisibleStage.LightsShadowCollect)),
            CaptureView(ECollectVisibleViewKind.MainView),
            CaptureView(ECollectVisibleViewKind.PrimaryShadow),
            CaptureView(ECollectVisibleViewKind.Cascade),
            CaptureView(ECollectVisibleViewKind.LocalShadow),
            CaptureView(ECollectVisibleViewKind.Other),
            new CollectVisibleSwapStageTimings(
                Ms(ECollectVisibleStage.SwapJobs),
                Calls(ECollectVisibleStage.SwapTransforms),
                Ms(ECollectVisibleStage.SwapWorld),
                Ms(ECollectVisibleStage.SwapTransforms),
                Ms(ECollectVisibleStage.SwapTransformStorePublish),
                Ms(ECollectVisibleStage.SwapPendingMeshMatrices),
                Ms(ECollectVisibleStage.SwapLights),
                Ms(ECollectVisibleStage.SwapAdvancedGlobalCapture),
                Ms(ECollectVisibleStage.SwapScene),
                Ms(ECollectVisibleStage.SwapCoveredSources),
                Ms(ECollectVisibleStage.SwapCpuTree),
                Ms(ECollectVisibleStage.SwapSceneCommands),
                Ms(ECollectVisibleStage.SwapAdvancedPublication),
                new CollectVisibleSwapAdvancedPublicationTimings(
                    Volatile.Read(ref s_advancedRowGatePasses),
                    Volatile.Read(ref s_advancedRowGateFailures),
                    Ms(ECollectVisibleStage.SwapAdvancedRowGate),
                    Volatile.Read(ref s_advancedUnchangedRowPublications),
                    Calls(ECollectVisibleStage.SwapAdvancedCommit),
                    Ms(ECollectVisibleStage.SwapAdvancedCommit)),
                Ms(ECollectVisibleStage.SwapViewports),
                Calls(ECollectVisibleStage.MainViewSwap),
                Ms(ECollectVisibleStage.MainViewSwap)),
            new CollectVisibleSwapLightStageTimings(
                Calls(ECollectVisibleStage.SwapLightsCaptures),
                Ms(ECollectVisibleStage.SwapLightsDirectional),
                Ms(ECollectVisibleStage.SwapLightsLocal),
                Ms(ECollectVisibleStage.SwapLightsAtlasPublication),
                Ms(ECollectVisibleStage.SwapLightsCaptures)),
            new CollectVisibleHandoffTimings(
                Interlocked.Read(ref s_renderUntilCollectReleaseCalls),
                ToMilliseconds(Interlocked.Read(ref s_renderUntilCollectReleaseTicks)),
                Interlocked.Read(ref s_collectHandoffIterations),
                Interlocked.Read(ref s_collectFinishedAfterRelease)));

    private static void OpenSection(ECollectVisibleSection section)
    {
        t_viewKind = ECollectVisibleViewKind.Other;
        t_pathOpen = false;
        t_sectionStartAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        t_section = section;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddStage(ECollectVisibleStage stage, long ticks)
    {
        int index = (int)stage;
        s_ticks[index] += ticks;
        ++s_calls[index];
    }

    private static CollectVisibleViewStageTimings CaptureView(ECollectVisibleViewKind kind)
    {
        int index = (int)kind;
        int pathBase = index * PathCount;
        return new CollectVisibleViewStageTimings(
            Volatile.Read(ref s_viewCalls[index]),
            Volatile.Read(ref s_viewRows[index]),
            ToMilliseconds(Volatile.Read(ref s_viewTicks[index])),
            ToMilliseconds(Volatile.Read(ref s_pathTicks[pathBase + (int)ECollectVisiblePath.GpuCollection])),
            ToMilliseconds(Volatile.Read(ref s_pathTicks[pathBase + (int)ECollectVisiblePath.CpuCollection])),
            ToMilliseconds(Volatile.Read(ref s_pathTicks[pathBase + (int)ECollectVisiblePath.MaskedFamily])),
            ToMilliseconds(Volatile.Read(ref s_pathTicks[pathBase + (int)ECollectVisiblePath.PackagePrepare])));
    }

    private static long Calls(ECollectVisibleStage stage)
        => Volatile.Read(ref s_calls[(int)stage]);

    private static double Ms(ECollectVisibleStage stage)
        => ToMilliseconds(Volatile.Read(ref s_ticks[(int)stage]));

    private static double ToMilliseconds(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;
}

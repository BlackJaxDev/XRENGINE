using System;
using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Bounded, process-wide observation for the S13a publication investigation.
/// Enable before launch with XRE_S13A_PUBLICATION_TELEMETRY=1. Counters are
/// cumulative so an MCP reader never retains commands, publications or leases.
/// </summary>
public static class S13aPublicationTelemetry
{
    private const int TraceCapacity = 65536;
    public static bool TraceEnabled { get; } =
        Environment.GetEnvironmentVariable("XRE_S13A_PUBLICATION_TRACE") == "1";
    public static uint TracedCommandId { get; } =
        uint.TryParse(Environment.GetEnvironmentVariable("XRE_S13A_TRACE_COMMAND_ID"),
            out uint commandId) ? commandId : 0u;
    public static bool Enabled { get; } =
        TraceEnabled || Environment.GetEnvironmentVariable("XRE_S13A_PUBLICATION_TELEMETRY") == "1";

    // A separate gate for each fixed slot lets MCP copy events without holding a
    // rendering lock. Contended writes are counted and dropped, never delayed.
    private static readonly S13aPublicationTraceEvent[] s_traceEvents =
        TraceEnabled ? new S13aPublicationTraceEvent[TraceCapacity] : [];
    private static readonly int[] s_traceGates = TraceEnabled ? new int[TraceCapacity] : [];
    private static readonly long[] s_traceDroppedSequences =
        TraceEnabled ? new long[TraceCapacity] : [];
    private static long s_traceSequence;
    private static long s_traceBusyDrops;
    private static long s_nextCollectionId;

    internal static long AllocateCollectionId()
        => TraceEnabled ? Interlocked.Increment(ref s_nextCollectionId) : 0L;

    internal static void Trace(S13aPublicationTraceEventKind kind, uint commandId = 0,
        long collectionId = 0, long collectionCycle = 0, ulong publicationSequence = 0,
        ulong frameId = 0, int detail = 0,
        S13aRenderCommandProperty propertyCode = S13aRenderCommandProperty.Other,
        uint propertyNameHash = 0, ulong databaseEpoch = 0,
        ulong frameGeneration = 0, ulong topologyGeneration = 0,
        ulong contentGeneration = 0, ulong lookupGeneration = 0)
    {
        if (!TraceEnabled || (TracedCommandId != 0u && commandId != 0u &&
            commandId != TracedCommandId)) return;
        long sequence = Interlocked.Increment(ref s_traceSequence);
        int slot = (int)((sequence - 1L) & (TraceCapacity - 1));
        if (Interlocked.CompareExchange(ref s_traceGates[slot], 1, 0) != 0)
        {
            // A delayed producer can reach this slot after the ring wraps. Keep
            // the newest drop marker so an older producer cannot erase its gap.
            ref long droppedSequence = ref s_traceDroppedSequences[slot];
            long previous = Volatile.Read(ref droppedSequence);
            while (previous < sequence)
            {
                long observed = Interlocked.CompareExchange(
                    ref droppedSequence, sequence, previous);
                if (observed == previous)
                    break;
                previous = observed;
            }
            Interlocked.Increment(ref s_traceBusyDrops);
            return;
        }

        try
        {
            // A producer reserved before a full wrap must not replace a newer
            // event or drop marker, including after the newer producer finishes.
            if (sequence <= Interlocked.Read(ref s_traceSequence) - TraceCapacity ||
                s_traceEvents[slot].Sequence > sequence ||
                Volatile.Read(ref s_traceDroppedSequences[slot]) > sequence)
                return;

            s_traceEvents[slot] = new S13aPublicationTraceEvent(sequence,
                Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId, kind,
                commandId, collectionId, collectionCycle, publicationSequence,
                frameId, detail, propertyCode, propertyNameHash,
                databaseEpoch, frameGeneration, topologyGeneration,
                contentGeneration, lookupGeneration);
        }
        finally
        {
            Volatile.Write(ref s_traceGates[slot], 0);
        }
    }

    internal static void TracePublicationCommitted(
        in AdvancedGpuScenePublication publication, ulong frameId)
        => Trace(S13aPublicationTraceEventKind.PublicationCommitted,
            publicationSequence: publication.Sequence, frameId: frameId,
            databaseEpoch: publication.DatabaseEpoch,
            frameGeneration: publication.FrameGeneration,
            topologyGeneration: publication.TopologyGeneration,
            contentGeneration: publication.ContentGeneration,
            lookupGeneration: publication.LookupGeneration);

    internal static S13aRenderCommandProperty ClassifyProperty(string? name) => name switch
    {
        nameof(RenderCommandMesh3D.PublishCanonicalDrawIdentities) => S13aRenderCommandProperty.PublishCanonicalDrawIdentities,
        nameof(RenderCommand.RenderPass) => S13aRenderCommandProperty.RenderPass,
        nameof(RenderCommand.Enabled) => S13aRenderCommandProperty.Enabled,
        nameof(RenderCommandMesh3D.GPUCommandIndex) => S13aRenderCommandProperty.GPUCommandIndex,
        nameof(RenderCommandMesh3D.Mesh) => S13aRenderCommandProperty.Mesh,
        nameof(RenderCommandMesh3D.WorldMatrix) => S13aRenderCommandProperty.WorldMatrix,
        nameof(RenderCommandMesh3D.MaterialOverride) => S13aRenderCommandProperty.MaterialOverride,
        nameof(RenderCommandMesh3D.RenderOptionsOverride) => S13aRenderCommandProperty.RenderOptionsOverride,
        nameof(RenderCommandMesh3D.Instances) => S13aRenderCommandProperty.Instances,
        nameof(RenderCommandMesh3D.WorldMatrixIsModelMatrix) => S13aRenderCommandProperty.WorldMatrixIsModelMatrix,
        nameof(RenderCommandMesh3D.ForceCpuRendering) => S13aRenderCommandProperty.ForceCpuRendering,
        nameof(RenderCommandMesh3D.EditorHighlightBits) => S13aRenderCommandProperty.EditorHighlightBits,
        nameof(RenderCommandMesh3D.WorldCullingVolumeOverride) => S13aRenderCommandProperty.WorldCullingVolumeOverride,
        nameof(RenderCommandMesh3D.GpuProfilingLabel) => S13aRenderCommandProperty.GpuProfilingLabel,
        _ => S13aRenderCommandProperty.Other,
    };

    internal static uint HashPropertyName(string? name)
    {
        if (name is null) return 0u;
        uint hash = 2166136261u;
        foreach (char character in name)
            hash = unchecked((hash ^ character) * 16777619u);
        return hash;
    }

    /// <summary>
    /// Copies up to 4096 retained events from the fixed ring. Gaps from wrap or
    /// contention are reported, so a trace cannot silently claim completeness.
    /// </summary>
    public static S13aPublicationTraceSnapshot CaptureTrace(long afterSequence = 0,
        int maxEvents = 2048, uint commandId = 0)
    {
        if (!TraceEnabled)
            return new(false, TracedCommandId, Stopwatch.Frequency, 0, 0, 0, 0, 0, 0, 0, []);

        int limit = Math.Clamp(maxEvents, 1, 4096);
        long latest = Interlocked.Read(ref s_traceSequence);
        long earliest = Math.Max(1L, latest - TraceCapacity + 1L);
        long next = Math.Max(afterSequence, earliest - 1L);
        long overwritten = Math.Max(0L, earliest - Math.Max(1L, afterSequence + 1L));
        S13aPublicationTraceEvent[] page = new S13aPublicationTraceEvent[limit];
        int count = 0;
        long droppedInPage = 0;
        for (long sequence = next + 1L; sequence <= latest; sequence++)
        {
            int slot = (int)((sequence - 1L) & (TraceCapacity - 1));
            if (Interlocked.CompareExchange(ref s_traceGates[slot], 1, 0) != 0)
                break;
            bool available = false;
            bool dropped = false;
            try
            {
                S13aPublicationTraceEvent entry = s_traceEvents[slot];
                available = entry.Sequence == sequence;
                dropped = Volatile.Read(ref s_traceDroppedSequences[slot]) == sequence;
                if (available &&
                    (commandId == 0 || entry.CommandId == commandId || entry.CommandId == 0))
                    page[count++] = entry;
            }
            finally
            {
                Volatile.Write(ref s_traceGates[slot], 0);
            }
            if (!available && !dropped)
                break; // The producer reserved this sequence but has not filled it yet.
            if (dropped) droppedInPage++;
            next = sequence;
            if (count == limit) break;
        }

        if (count != page.Length)
            Array.Resize(ref page, count);
        return new(true, TracedCommandId, Stopwatch.Frequency, TraceCapacity, latest, earliest,
            next, overwritten, Interlocked.Read(ref s_traceBusyDrops), droppedInPage, page);
    }

    private static long _dirtyIdentityNotifications;
    private static long _dirtyOtherNotifications;
    private static long _dirtyAlreadySet;
    private static long _manualDirty;
    private static long _queueAdds;
    private static long _queueDuplicates;
    private static long _queueClean;
    private static long _swapCount;
    private static long _swapQueued;
    private static long _swapCallbacks;
    private static long _swapAuthorityYields;
    private static long _meshUpdateCalls;
    private static long _meshUpdateChanged;
    private static long _meshUpdateUnchanged;
    private static long _meshUpdateFailed;
    private static long _meshUpdateWaitTicks;
    private static long _meshUpdateBodyTicks;
    private static long _meshUpdateHeldTicks;
    private static long _meshUpdateMaterialTicks;
    private static long _meshUpdateRegistrationTicks;
    private static long _meshUpdateWriteTicks;
    private static long _meshUpdateAllocationBytes;
    private static long _meshUpdateSubmeshes;
    private static long _meshUpdateRegistrationAttempts;
    private static long _meshUpdateRegistrationHits;
    private static long _meshUpdateRegistrationRebuilds;
    private static long _meshUpdateAtlasEnsureCalls;
    private static long _meshUpdateLogicalTableWrites;
    private static long _meshUpdateMembershipAllocatedBytes;
    private static long _meshUpdateLookupAllocatedBytes;
    private static long _meshUpdateMaterialAllocatedBytes;
    private static long _meshUpdateRegistrationAllocatedBytes;
    private static long _meshUpdateMetadataAllocatedBytes;
    private static long _meshUpdateStateClassAllocatedBytes;
    private static long _meshUpdateFlagsAllocatedBytes;
    private static long _meshUpdateTransparencyAllocatedBytes;
    private static long _meshUpdateBoundsCompareAllocatedBytes;
    private static long _meshUpdateWriteAllocatedBytes;
    private static long _meshUpdateCommitAllocatedBytes;
    private static long _meshUpdateMetadataWrites;
    private static long _meshUpdateStateClassWrites;
    private static long _meshUpdateTransparencyWrites;
    private static long _meshUpdateBoundsWrites;
    private static long _meshUpdateTransformWrites;
    private static long _sceneSwaps;
    private static long _sceneSwapsContentDirty;
    private static long _sceneSwapsStreamsDirty;
    private static long _sceneSwapCullControlElements;
    private static long _sceneSwapBoundsElements;
    private static long _sceneSwapClassificationElements;
    private static long _sceneSwapVisibilityElements;
    private static long _sceneSwapTransformElements;
    private static long _sceneSwapPreviousTransformElements;
    private static long _sceneSwapMaterialStateElements;
    private static long _sceneSwapAabbElements;
    private static long _sceneSwapTransparencyBytes;
    private static long _advancedFamilyPreparations;
    private static long _advancedFamilyStages;
    private static long _advancedScenePublicationPrepareCalls;
    private static long _advancedScenePublicationAttempts;
    private static long _advancedSceneSlotHits;
    private static long _advancedSceneSlotRealizations;
    private static long _advancedScenePublicationReuses;
    private static long _advancedScenePublicationPrepareTicks;
    private static long _advancedScenePublicationFailures;
    private static long _advancedPlanDiscoveries;
    private static long _advancedPlanOperations;
    private static long _advancedPlanAdvancedOperations;
    private static long _advancedPlanFamilies;
    private static long _advancedPlanDiscoveryVisits;
    private static long _advancedPlanFamilyScanVisits;
    private static long _advancedPlanLeaseChecks;
    private static long _advancedPlanDiscoveryTicks;
    private static long _advancedPlanPreparationTicks;
    private static long _advancedPlanGateWaitTicks;
    private static long _advancedPlanAllocatedBytes;
    private static long _advancedPlanSealCollections;
    private static long _advancedPlanSealVisits;
    /// <summary>Number of <see cref="S13aAdvancedFamilyStep"/> values.</summary>
    public const int AdvancedFamilyStepCount = 28;
    private static readonly long[] s_advancedFamilyStepCalls = new long[AdvancedFamilyStepCount];
    private static readonly long[] s_advancedFamilyStepTicks = new long[AdvancedFamilyStepCount];
    private static readonly long[] s_advancedFamilyStepBytes = new long[AdvancedFamilyStepCount];
    private static long _advancedBinFreezeSorts;
    private static long _advancedBinFreezeRecords;
    private static long _advancedBinFreezeOrderViolations;
    private static long _publicationReused;
    private static long _publicationMissing;
    private static long _publicationExpired;
    private static long _publicationResourceMutation;
    private static long _publicationMaterialMutation;
    private static long _publicationCommandMutation;
    private static long _publicationTemporalMutation;
    private static long _publicationRegistrationRemoval;
    private static long _snapshotSequence;

    internal static void DirtyNotification(bool identity, bool alreadyDirty)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref identity ? ref _dirtyIdentityNotifications : ref _dirtyOtherNotifications);
        if (alreadyDirty) Interlocked.Increment(ref _dirtyAlreadySet);
    }

    internal static void ManualDirty()
    {
        if (Enabled) Interlocked.Increment(ref _manualDirty);
    }

    internal static void QueueDecision(bool needsPublish, bool added)
    {
        if (!Enabled) return;
        if (!needsPublish) Interlocked.Increment(ref _queueClean);
        else Interlocked.Increment(ref added ? ref _queueAdds : ref _queueDuplicates);
    }

    internal static void Swap(int queued, int callbacks, int authorityYields)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _swapCount);
        Interlocked.Add(ref _swapQueued, queued);
        Interlocked.Add(ref _swapCallbacks, callbacks);
        Interlocked.Add(ref _swapAuthorityYields, authorityYields);
    }

    internal static void MeshUpdate(bool changed, bool completed, long waitTicks, long bodyTicks,
        long allocatedBytes, int submeshes, int registrationAttempts, int metadataWrites,
        int stateClassWrites, int transparencyWrites,
        long materialTicks, long registrationTicks, long writeTicks,
        int registrationHits, int registrationRebuilds, int atlasEnsureCalls, int logicalTableWrites,
        long membershipAllocatedBytes, long lookupAllocatedBytes, long materialAllocatedBytes,
        long registrationAllocatedBytes, long metadataAllocatedBytes, long stateClassAllocatedBytes,
        long flagsAllocatedBytes, long transparencyAllocatedBytes, long boundsCompareAllocatedBytes,
        long writeAllocatedBytes, long commitAllocatedBytes,
        int boundsWrites, int transformWrites)
    {
        if (!Enabled) return;
        Interlocked.Add(ref _meshUpdateBoundsWrites, boundsWrites);
        Interlocked.Add(ref _meshUpdateTransformWrites, transformWrites);
        Interlocked.Add(ref _meshUpdateRegistrationHits, registrationHits);
        Interlocked.Add(ref _meshUpdateRegistrationRebuilds, registrationRebuilds);
        Interlocked.Add(ref _meshUpdateAtlasEnsureCalls, atlasEnsureCalls);
        Interlocked.Add(ref _meshUpdateLogicalTableWrites, logicalTableWrites);
        Interlocked.Add(ref _meshUpdateMembershipAllocatedBytes, membershipAllocatedBytes);
        Interlocked.Add(ref _meshUpdateLookupAllocatedBytes, lookupAllocatedBytes);
        Interlocked.Add(ref _meshUpdateMaterialAllocatedBytes, materialAllocatedBytes);
        Interlocked.Add(ref _meshUpdateRegistrationAllocatedBytes, registrationAllocatedBytes);
        Interlocked.Add(ref _meshUpdateMetadataAllocatedBytes, metadataAllocatedBytes);
        Interlocked.Add(ref _meshUpdateStateClassAllocatedBytes, stateClassAllocatedBytes);
        Interlocked.Add(ref _meshUpdateFlagsAllocatedBytes, flagsAllocatedBytes);
        Interlocked.Add(ref _meshUpdateTransparencyAllocatedBytes, transparencyAllocatedBytes);
        Interlocked.Add(ref _meshUpdateBoundsCompareAllocatedBytes, boundsCompareAllocatedBytes);
        Interlocked.Add(ref _meshUpdateWriteAllocatedBytes, writeAllocatedBytes);
        Interlocked.Add(ref _meshUpdateCommitAllocatedBytes, commitAllocatedBytes);
        Interlocked.Increment(ref _meshUpdateCalls);
        if (!completed) Interlocked.Increment(ref _meshUpdateFailed);
        else Interlocked.Increment(ref changed ? ref _meshUpdateChanged : ref _meshUpdateUnchanged);
        Interlocked.Add(ref _meshUpdateWaitTicks, waitTicks);
        Interlocked.Add(ref _meshUpdateBodyTicks, bodyTicks);
        Interlocked.Add(ref _meshUpdateMaterialTicks, materialTicks);
        Interlocked.Add(ref _meshUpdateRegistrationTicks, registrationTicks);
        Interlocked.Add(ref _meshUpdateWriteTicks, writeTicks);
        Interlocked.Add(ref _meshUpdateAllocationBytes, allocatedBytes);
        Interlocked.Add(ref _meshUpdateSubmeshes, submeshes);
        Interlocked.Add(ref _meshUpdateRegistrationAttempts, registrationAttempts);
        Interlocked.Add(ref _meshUpdateMetadataWrites, metadataWrites);
        Interlocked.Add(ref _meshUpdateStateClassWrites, stateClassWrites);
        Interlocked.Add(ref _meshUpdateTransparencyWrites, transparencyWrites);
    }

    /// <summary>
    /// Records one GPU scene command-buffer swap: the element counts of the stream
    /// dirty ranges it publishes to the render snapshot and the bytes of the full
    /// transparency copy a content-dirty swap performs.
    /// </summary>
    internal static void SceneSwap(bool contentDirty, bool streamsDirty,
        uint cullControlElements, uint boundsElements, uint classificationElements,
        uint visibilityElements, uint transformElements, uint previousTransformElements,
        uint materialStateElements, uint aabbElements, uint transparencyBytes)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _sceneSwaps);
        if (contentDirty) Interlocked.Increment(ref _sceneSwapsContentDirty);
        if (streamsDirty) Interlocked.Increment(ref _sceneSwapsStreamsDirty);
        Interlocked.Add(ref _sceneSwapCullControlElements, cullControlElements);
        Interlocked.Add(ref _sceneSwapBoundsElements, boundsElements);
        Interlocked.Add(ref _sceneSwapClassificationElements, classificationElements);
        Interlocked.Add(ref _sceneSwapVisibilityElements, visibilityElements);
        Interlocked.Add(ref _sceneSwapTransformElements, transformElements);
        Interlocked.Add(ref _sceneSwapPreviousTransformElements, previousTransformElements);
        Interlocked.Add(ref _sceneSwapMaterialStateElements, materialStateElements);
        Interlocked.Add(ref _sceneSwapAabbElements, aabbElements);
        Interlocked.Add(ref _sceneSwapTransparencyBytes, transparencyBytes);
    }

    /// <summary>
    /// Records one Vulkan Advanced visibility family preparation: the stages it
    /// iterated, how many scene publication preparations it ran, how many of those
    /// newly realized a native publication, how many stages reused the family
    /// result, and the ticks spent in scene publication preparation.
    /// </summary>
    internal static void AdvancedFamilyPreparation(int stages, int prepareCalls,
        int attempts, int reuses, long prepareTicks, int failures)
    {
        if (!Enabled) return;
        Interlocked.Add(ref _advancedScenePublicationFailures, failures);
        Interlocked.Increment(ref _advancedFamilyPreparations);
        Interlocked.Add(ref _advancedFamilyStages, stages);
        Interlocked.Add(ref _advancedScenePublicationPrepareCalls, prepareCalls);
        Interlocked.Add(ref _advancedScenePublicationAttempts, attempts);
        Interlocked.Add(ref _advancedScenePublicationReuses, reuses);
        Interlocked.Add(ref _advancedScenePublicationPrepareTicks, prepareTicks);
    }

    /// <summary>
    /// Records whether a scene-resource frame slot armed an existing native
    /// realization of a canonical publication or realized a new one.
    /// </summary>
    internal static void AdvancedSceneSlotPreparation(bool existing)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref existing ? ref _advancedSceneSlotHits : ref _advancedSceneSlotRealizations);
    }

    /// <summary>
    /// Records one primary-recording discovery of Advanced visibility families:
    /// the sealed operations scanned, the Advanced operations among them, the
    /// distinct families found, the headers the discovery scan visited, the ticks
    /// spent discovering and then preparing every family inside the storage gate,
    /// the ticks spent waiting for that gate, and the bytes the recording thread
    /// allocated across discovery and preparation.
    /// </summary>
    internal static void AdvancedPlanDiscovery(int operations, int advancedOperations, int families,
        int discoveryVisits, long discoveryTicks, long preparationTicks, long gateWaitTicks,
        long allocatedBytes)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _advancedPlanDiscoveries);
        Interlocked.Add(ref _advancedPlanOperations, operations);
        Interlocked.Add(ref _advancedPlanAdvancedOperations, advancedOperations);
        Interlocked.Add(ref _advancedPlanFamilies, families);
        Interlocked.Add(ref _advancedPlanDiscoveryVisits, discoveryVisits);
        Interlocked.Add(ref _advancedPlanDiscoveryTicks, discoveryTicks);
        Interlocked.Add(ref _advancedPlanPreparationTicks, preparationTicks);
        Interlocked.Add(ref _advancedPlanGateWaitTicks, gateWaitTicks);
        Interlocked.Add(ref _advancedPlanAllocatedBytes, allocatedBytes);
    }

    /// <summary>
    /// Records the sealed operation headers one family preparation visited while
    /// classifying and associating its stages, and the plan-lease checks it made.
    /// </summary>
    internal static void AdvancedPlanFamilyScan(int visits, int leaseChecks)
    {
        if (!Enabled) return;
        Interlocked.Add(ref _advancedPlanFamilyScanVisits, visits);
        Interlocked.Add(ref _advancedPlanLeaseChecks, leaseChecks);
    }

    /// <summary>
    /// Records one sealed frame plan collecting its Advanced output reservations
    /// and the operation headers that collection visited across every stream.
    /// </summary>
    internal static void AdvancedPlanSealCollection(int visits)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _advancedPlanSealCollections);
        Interlocked.Add(ref _advancedPlanSealVisits, visits);
    }

    /// <summary>
    /// Starts timing one family-preparation step on the recording thread. The
    /// returned probe is inert when observation is disabled.
    /// </summary>
    internal static StepProbe BeginAdvancedFamilyStep()
        => Enabled
            ? new StepProbe(true, Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread())
            : default;

    /// <summary>
    /// Accumulates elapsed ticks and bytes allocated by the current thread for
    /// one <see cref="S13aAdvancedFamilyStep"/> when the probe is active.
    /// </summary>
    internal readonly struct StepProbe(bool active, long started, long allocatedBefore)
    {
        public void End(S13aAdvancedFamilyStep step)
        {
            if (!active) return;
            int index = (int)step;
            Interlocked.Increment(ref s_advancedFamilyStepCalls[index]);
            Interlocked.Add(ref s_advancedFamilyStepTicks[index], Stopwatch.GetTimestamp() - started);
            Interlocked.Add(ref s_advancedFamilyStepBytes[index],
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
    }

    private static long[] CopyCounters(long[] source)
    {
        long[] copy = new long[source.Length];
        for (int index = 0; index < source.Length; index++)
            copy[index] = Interlocked.Read(ref source[index]);
        return copy;
    }

    /// <summary>
    /// Records one stable-bin freeze ordering: the records ordered and how many
    /// adjacent pairs violate the full record comparison afterwards (always zero
    /// for a correct sort).
    /// </summary>
    internal static void AdvancedBinFreezeOrder(int records, int violations)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _advancedBinFreezeSorts);
        Interlocked.Add(ref _advancedBinFreezeRecords, records);
        Interlocked.Add(ref _advancedBinFreezeOrderViolations, violations);
    }

    internal static LockBodyScope BeginLockBody() => new(Enabled ? Stopwatch.GetTimestamp() : 0L);

    internal readonly struct LockBodyScope(long started) : IDisposable
    {
        public void Dispose()
        {
            if (started != 0L)
                Interlocked.Add(ref _meshUpdateHeldTicks, Stopwatch.GetTimestamp() - started);
        }
    }

    internal static void PublicationReuse() { if (Enabled) Interlocked.Increment(ref _publicationReused); }
    internal static void PublicationMissing() { if (Enabled) Interlocked.Increment(ref _publicationMissing); }
    internal static void PublicationExpired() { if (Enabled) Interlocked.Increment(ref _publicationExpired); }
    internal static void PublicationResourceMutation() { if (Enabled) Interlocked.Increment(ref _publicationResourceMutation); }
    internal static void PublicationMaterialMutation() { if (Enabled) Interlocked.Increment(ref _publicationMaterialMutation); }
    internal static void PublicationCommandMutation() { if (Enabled) Interlocked.Increment(ref _publicationCommandMutation); }
    internal static void PublicationTemporalMutation() { if (Enabled) Interlocked.Increment(ref _publicationTemporalMutation); }
    internal static void PublicationRegistrationRemoval() { if (Enabled) Interlocked.Increment(ref _publicationRegistrationRemoval); }

    /// <summary>Returns independent atomic counter reads; callers should sample window deltas.</summary>
    public static S13aPublicationTelemetrySnapshot CaptureSnapshot() => new(
        Enabled, Stopwatch.Frequency, Interlocked.Increment(ref _snapshotSequence),
        Stopwatch.GetTimestamp(),
        Interlocked.Read(ref _dirtyIdentityNotifications), Interlocked.Read(ref _dirtyOtherNotifications),
        Interlocked.Read(ref _dirtyAlreadySet), Interlocked.Read(ref _manualDirty),
        Interlocked.Read(ref _queueAdds), Interlocked.Read(ref _queueDuplicates), Interlocked.Read(ref _queueClean),
        Interlocked.Read(ref _swapCount), Interlocked.Read(ref _swapQueued),
        Interlocked.Read(ref _swapCallbacks), Interlocked.Read(ref _swapAuthorityYields),
        Interlocked.Read(ref _meshUpdateCalls), Interlocked.Read(ref _meshUpdateChanged),
        Interlocked.Read(ref _meshUpdateUnchanged), Interlocked.Read(ref _meshUpdateFailed),
        Interlocked.Read(ref _meshUpdateWaitTicks),
        Interlocked.Read(ref _meshUpdateBodyTicks), Interlocked.Read(ref _meshUpdateHeldTicks),
        Interlocked.Read(ref _meshUpdateMaterialTicks), Interlocked.Read(ref _meshUpdateRegistrationTicks),
        Interlocked.Read(ref _meshUpdateWriteTicks), Interlocked.Read(ref _meshUpdateAllocationBytes),
        Interlocked.Read(ref _meshUpdateSubmeshes), Interlocked.Read(ref _meshUpdateRegistrationAttempts),
        Interlocked.Read(ref _meshUpdateRegistrationHits), Interlocked.Read(ref _meshUpdateRegistrationRebuilds),
        Interlocked.Read(ref _meshUpdateAtlasEnsureCalls), Interlocked.Read(ref _meshUpdateLogicalTableWrites),
        Interlocked.Read(ref _meshUpdateMembershipAllocatedBytes), Interlocked.Read(ref _meshUpdateLookupAllocatedBytes),
        Interlocked.Read(ref _meshUpdateMaterialAllocatedBytes), Interlocked.Read(ref _meshUpdateRegistrationAllocatedBytes),
        Interlocked.Read(ref _meshUpdateMetadataAllocatedBytes), Interlocked.Read(ref _meshUpdateStateClassAllocatedBytes),
        Interlocked.Read(ref _meshUpdateFlagsAllocatedBytes), Interlocked.Read(ref _meshUpdateTransparencyAllocatedBytes),
        Interlocked.Read(ref _meshUpdateBoundsCompareAllocatedBytes), Interlocked.Read(ref _meshUpdateWriteAllocatedBytes),
        Interlocked.Read(ref _meshUpdateCommitAllocatedBytes),
        Interlocked.Read(ref _meshUpdateMetadataWrites), Interlocked.Read(ref _meshUpdateStateClassWrites),
        Interlocked.Read(ref _meshUpdateTransparencyWrites), Interlocked.Read(ref _publicationReused),
        Interlocked.Read(ref _publicationMissing), Interlocked.Read(ref _publicationExpired),
        Interlocked.Read(ref _publicationResourceMutation),
        Interlocked.Read(ref _publicationMaterialMutation), Interlocked.Read(ref _publicationCommandMutation),
        Interlocked.Read(ref _publicationTemporalMutation), Interlocked.Read(ref _publicationRegistrationRemoval),
        Interlocked.Read(ref _meshUpdateBoundsWrites), Interlocked.Read(ref _meshUpdateTransformWrites),
        Interlocked.Read(ref _sceneSwaps), Interlocked.Read(ref _sceneSwapsContentDirty),
        Interlocked.Read(ref _sceneSwapsStreamsDirty), Interlocked.Read(ref _sceneSwapCullControlElements),
        Interlocked.Read(ref _sceneSwapBoundsElements), Interlocked.Read(ref _sceneSwapClassificationElements),
        Interlocked.Read(ref _sceneSwapVisibilityElements), Interlocked.Read(ref _sceneSwapTransformElements),
        Interlocked.Read(ref _sceneSwapPreviousTransformElements), Interlocked.Read(ref _sceneSwapMaterialStateElements),
        Interlocked.Read(ref _sceneSwapAabbElements), Interlocked.Read(ref _sceneSwapTransparencyBytes),
        Interlocked.Read(ref _advancedFamilyPreparations), Interlocked.Read(ref _advancedFamilyStages),
        Interlocked.Read(ref _advancedScenePublicationPrepareCalls),
        Interlocked.Read(ref _advancedScenePublicationAttempts),
        Interlocked.Read(ref _advancedScenePublicationReuses),
        Interlocked.Read(ref _advancedScenePublicationPrepareTicks),
        Interlocked.Read(ref _advancedScenePublicationFailures),
        Interlocked.Read(ref _advancedSceneSlotHits), Interlocked.Read(ref _advancedSceneSlotRealizations),
        Interlocked.Read(ref _advancedPlanDiscoveries), Interlocked.Read(ref _advancedPlanOperations),
        Interlocked.Read(ref _advancedPlanAdvancedOperations), Interlocked.Read(ref _advancedPlanFamilies),
        Interlocked.Read(ref _advancedPlanDiscoveryVisits), Interlocked.Read(ref _advancedPlanFamilyScanVisits),
        Interlocked.Read(ref _advancedPlanLeaseChecks), Interlocked.Read(ref _advancedPlanDiscoveryTicks),
        Interlocked.Read(ref _advancedPlanPreparationTicks), Interlocked.Read(ref _advancedPlanGateWaitTicks),
        Interlocked.Read(ref _advancedPlanAllocatedBytes), Interlocked.Read(ref _advancedPlanSealCollections),
        Interlocked.Read(ref _advancedPlanSealVisits),
        CopyCounters(s_advancedFamilyStepCalls), CopyCounters(s_advancedFamilyStepTicks),
        CopyCounters(s_advancedFamilyStepBytes),
        Interlocked.Read(ref _advancedBinFreezeSorts), Interlocked.Read(ref _advancedBinFreezeRecords),
        Interlocked.Read(ref _advancedBinFreezeOrderViolations));
}

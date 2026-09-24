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
    private static long _meshUpdateMetadataWrites;
    private static long _meshUpdateStateClassWrites;
    private static long _meshUpdateTransparencyWrites;
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
        long materialTicks, long registrationTicks, long writeTicks)
    {
        if (!Enabled) return;
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
        Interlocked.Read(ref _meshUpdateMetadataWrites), Interlocked.Read(ref _meshUpdateStateClassWrites),
        Interlocked.Read(ref _meshUpdateTransparencyWrites), Interlocked.Read(ref _publicationReused),
        Interlocked.Read(ref _publicationMissing), Interlocked.Read(ref _publicationExpired),
        Interlocked.Read(ref _publicationResourceMutation),
        Interlocked.Read(ref _publicationMaterialMutation), Interlocked.Read(ref _publicationCommandMutation),
        Interlocked.Read(ref _publicationTemporalMutation), Interlocked.Read(ref _publicationRegistrationRemoval));
}

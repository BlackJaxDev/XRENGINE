using Silk.NET.Vulkan;
using System.Diagnostics;
using XREngine.Execution;

namespace XREngine.Rendering.Vulkan;

/// <summary>Reusable command-runtime batch state shared by recording workers.</summary>
internal sealed class VulkanCommandChainRecordingBatch : IRenderWorkExecutor
{
    internal const int MeshCommandChainOperationKind = 1;

    /// <summary>Immutable encoder input published after the prepared frame is frozen.</summary>
    public VulkanPreparedWorkerRecordingContext PreparedWorkerContext { get; } = new();
    public readonly VulkanPreparedFrameRecording PreparedFrame = new();
    public VulkanCommandChainRecordingEntry[] Entries = new VulkanCommandChainRecordingEntry[16];
    public VulkanCommandChainRecordingDraw[] Draws = new VulkanCommandChainRecordingDraw[16];
    // Cold chain authority is indexed by compact queue entries. It remains on
    // the render thread except for the one lookup that establishes a worker's
    // native recording lease.
    private CommandChain[] _commandChainColdData = new CommandChain[16];
    // Merge-only native execute input; it is derived from Entries after all jobs complete.
    public CommandBuffer[] ExecutionBuffers = new CommandBuffer[16];
    private int[] _renderWorkEntryIndices = new int[16];
    private int[] _laneStarts = new int[4];
    private int[] _laneCounts = new int[4];
    private int[] _laneCursors = new int[4];
    private int[] _laneEstimatedCosts = new int[4];
    private VulkanCommandRuntime? _commandRuntime;
    public readonly VulkanCommandChainWorkerLocalStateBlocks WorkerLocalStates = new();
    public int StartIndex;
    public int EntryCount;
    public int DrawCount;
    public int CommandChainColdDataCount;
    public int JobCount;
    public uint ActiveWorkerMask;
    public Exception? Error;
    public long DispatchTimestamp;
    public int CancelRequested;
    public bool Abandoned;

    // Allocation-free batch telemetry. The runtime owns publication, while the
    // batch records the exact frozen queue and local-state layout it dispatched.
    public int QueueDepth;
    public int QueueBytes;
    public int QueueHighWaterDepth;
    public int QueueHighWaterBytes;
    public int WorkerLocalStateBlockBaseAlignmentRemainder;
    public int WorkerLocalStateBlockStride;
    public long LocalMergeElapsedTicks;
    public int LocalMergeBytes;
    public long ExecutionMergeElapsedTicks;
    public int ExecutionMergeBytes;

    public void EnsureCapacity(int entryCount, int drawCount)
    {
        if (Entries.Length < entryCount)
            Array.Resize(ref Entries, Math.Max(entryCount, Entries.Length * 2));
        if (Draws.Length < drawCount)
            Array.Resize(ref Draws, Math.Max(drawCount, Draws.Length * 2));
        if (ExecutionBuffers.Length < entryCount)
            Array.Resize(ref ExecutionBuffers, Math.Max(entryCount, ExecutionBuffers.Length * 2));
        if (_commandChainColdData.Length < entryCount)
            Array.Resize(ref _commandChainColdData, Math.Max(entryCount, _commandChainColdData.Length * 2));
        if (_renderWorkEntryIndices.Length < entryCount)
            Array.Resize(ref _renderWorkEntryIndices, Math.Max(entryCount, _renderWorkEntryIndices.Length * 2));
    }

    internal void PrepareRenderWork(VulkanCommandRuntime commandRuntime, int laneCount)
    {
        ArgumentNullException.ThrowIfNull(commandRuntime);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(laneCount);
        EnsureLaneCapacity(laneCount);
        Array.Clear(_laneStarts, 0, laneCount);
        Array.Clear(_laneCounts, 0, laneCount);
        Array.Clear(_laneCursors, 0, laneCount);
        Array.Clear(_laneEstimatedCosts, 0, laneCount);
        ActiveWorkerMask = 0;

        for (int entryIndex = 0; entryIndex < EntryCount; entryIndex++)
        {
            ref readonly VulkanCommandChainRecordingEntry entry = ref Entries[entryIndex];
            if (!entry.NeedsRecording || !entry.DispatchToRenderDomain)
                continue;
            if ((uint)entry.WorkerIndex >= (uint)laneCount)
                throw new InvalidOperationException($"Prepared command-chain entry {entryIndex} has invalid lane {entry.WorkerIndex}.");

            _laneCounts[entry.WorkerIndex]++;
            CommandChain chain = GetCommandChainColdData(entry.ColdDataIndex);
            _laneEstimatedCosts[entry.WorkerIndex] = AddSaturating(
                _laneEstimatedCosts[entry.WorkerIndex],
                chain.SourceCount);
            ActiveWorkerMask |= 1u << entry.WorkerIndex;
        }

        int workOffset = 0;
        for (int laneId = 0; laneId < laneCount; laneId++)
        {
            _laneStarts[laneId] = workOffset;
            _laneCursors[laneId] = workOffset;
            workOffset = checked(workOffset + _laneCounts[laneId]);
        }

        for (int entryIndex = 0; entryIndex < EntryCount; entryIndex++)
        {
            ref readonly VulkanCommandChainRecordingEntry entry = ref Entries[entryIndex];
            if (!entry.NeedsRecording || !entry.DispatchToRenderDomain)
                continue;

            _renderWorkEntryIndices[_laneCursors[entry.WorkerIndex]++] = entryIndex;
        }

        _commandRuntime = commandRuntime;
    }

    internal void GetLaneWork(
        int laneId,
        out int sourceStart,
        out int sourceCount,
        out int estimatedCost)
    {
        if ((uint)laneId >= (uint)_laneCounts.Length)
            throw new ArgumentOutOfRangeException(nameof(laneId));

        sourceStart = _laneStarts[laneId];
        sourceCount = _laneCounts[laneId];
        estimatedCost = Math.Max(1, _laneEstimatedCosts[laneId]);
    }

    public void Execute(in RenderWorkItem item, ref RenderWorkerContext context)
    {
        if (item.OperationKind != MeshCommandChainOperationKind)
            throw new InvalidOperationException($"Unsupported Vulkan command recording work kind {item.OperationKind}.");
        if (!context.TryGetBackendAttachment(out VulkanRenderLaneFrameAttachment? attachment) ||
            attachment is null)
            throw new InvalidOperationException($"Vulkan render lane {context.LaneId}:{context.FrameSlot} has no command-pool attachment.");
        if (attachment.LaneId != context.LaneId || attachment.FrameSlot != context.FrameSlot)
            throw new InvalidOperationException("The Vulkan command-pool attachment does not match its render-worker context.");
        if (item.SourceStart < 0 || item.SourceCount <= 0 ||
            item.SourceStart > _renderWorkEntryIndices.Length - item.SourceCount)
        {
            throw new InvalidOperationException("Vulkan command recording received an invalid frozen entry range.");
        }

        VulkanCommandRuntime runtime = _commandRuntime ??
            throw new InvalidOperationException("The Vulkan command recording executor is not configured.");
        long started = Stopwatch.GetTimestamp();
        WorkerLocalStates.Begin(context.LaneId, started, DispatchTimestamp);
        using VulkanRenderLaneExecutionScope laneScope = new(attachment);
        using VulkanLaneCommandFamilyArena.RecordingLease arenaLease =
            VulkanLaneCommandFamilyArena.EnterRecording(attachment.Graphics);
        try
        {
            int end = checked(item.SourceStart + item.SourceCount);
            for (int workIndex = item.SourceStart; workIndex < end; workIndex++)
            {
                int entryIndex = _renderWorkEntryIndices[workIndex];
                ref readonly VulkanCommandChainRecordingEntry entry = ref Entries[entryIndex];
                if (!entry.NeedsRecording ||
                    !entry.DispatchToRenderDomain ||
                    entry.WorkerIndex != context.LaneId)
                {
                    throw new InvalidOperationException(
                        $"Frozen Vulkan command range contains entry {entryIndex} owned by lane {entry.WorkerIndex}, " +
                        $"not executing lane {context.LaneId}.");
                }

                runtime.RecordPreparedMeshCommandChain(this, entryIndex);
            }
        }
        finally
        {
            WorkerLocalStates.Complete(context.LaneId, Stopwatch.GetTimestamp());
        }
    }

    public void QuarantineFaultedBatch(in RenderWorkBatchFaultContext context)
    {
        for (int entryIndex = 0; entryIndex < EntryCount; entryIndex++)
        {
            ref readonly VulkanCommandChainRecordingEntry entry = ref Entries[entryIndex];
            if (!entry.NeedsRecording || !entry.DispatchToRenderDomain)
                continue;

            GetCommandChainColdData(entry.ColdDataIndex).RecordedArtifact.MarkFailed();
        }
    }

    public int AddCommandChainColdData(CommandChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (CommandChainColdDataCount == _commandChainColdData.Length)
            Array.Resize(ref _commandChainColdData, Math.Max(16, _commandChainColdData.Length * 2));

        int index = CommandChainColdDataCount++;
        _commandChainColdData[index] = chain;
        return index;
    }

    public CommandChain GetCommandChainColdData(int index)
    {
        if ((uint)index >= (uint)CommandChainColdDataCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _commandChainColdData[index] ??
            throw new InvalidOperationException("Prepared command-chain cold authority was released before batch completion.");
    }

    public void ResetWorkerState(int workerCount)
    {
        WorkerLocalStates.Reset(workerCount);
        DispatchTimestamp = 0;
        CancelRequested = 0;
        Abandoned = false;
        QueueDepth = JobCount;
        QueueBytes = JobCount * VulkanCommandChainRecordingEntry.SizeInBytes;
        QueueHighWaterDepth = Math.Max(QueueHighWaterDepth, QueueDepth);
        QueueHighWaterBytes = Math.Max(QueueHighWaterBytes, QueueBytes);
        WorkerLocalStateBlockBaseAlignmentRemainder = WorkerLocalStates.BaseAlignmentRemainder;
        WorkerLocalStateBlockStride = WorkerLocalStates.Stride;
        LocalMergeElapsedTicks = 0;
        LocalMergeBytes = 0;
        ExecutionMergeElapsedTicks = 0;
        ExecutionMergeBytes = 0;
    }

    /// <summary>Publishes exact compact queue telemetry, including serial fallback jobs.</summary>
    public void PublishQueueTelemetry(int queuedJobCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(queuedJobCount);
        QueueDepth = queuedJobCount;
        QueueBytes = checked(queuedJobCount * VulkanCommandChainRecordingEntry.SizeInBytes);
        QueueHighWaterDepth = Math.Max(QueueHighWaterDepth, QueueDepth);
        QueueHighWaterBytes = Math.Max(QueueHighWaterBytes, QueueBytes);
    }

    public void ClearReferences()
    {
        Array.Clear(Entries, 0, EntryCount);
        Array.Clear(ExecutionBuffers, 0, EntryCount);
        Array.Clear(Draws, 0, DrawCount);
        Array.Clear(_commandChainColdData, 0, CommandChainColdDataCount);
        PreparedFrame.Reset();
        EntryCount = 0;
        DrawCount = 0;
        CommandChainColdDataCount = 0;
        JobCount = 0;
        ActiveWorkerMask = 0;
        Error = null;
        PreparedWorkerContext.Reset();
        _commandRuntime = null;
    }

    private void EnsureLaneCapacity(int laneCount)
    {
        if (_laneCounts.Length >= laneCount)
            return;

        int capacity = Math.Max(laneCount, _laneCounts.Length * 2);
        Array.Resize(ref _laneStarts, capacity);
        Array.Resize(ref _laneCounts, capacity);
        Array.Resize(ref _laneCursors, capacity);
        Array.Resize(ref _laneEstimatedCosts, capacity);
    }

    private static int AddSaturating(int left, int right)
        => left > int.MaxValue - right ? int.MaxValue : left + right;
}

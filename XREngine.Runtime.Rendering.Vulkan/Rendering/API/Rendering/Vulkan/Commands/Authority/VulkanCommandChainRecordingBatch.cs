using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Reusable command-runtime batch state shared by recording workers.</summary>
internal sealed class VulkanCommandChainRecordingBatch
{
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
    }
}

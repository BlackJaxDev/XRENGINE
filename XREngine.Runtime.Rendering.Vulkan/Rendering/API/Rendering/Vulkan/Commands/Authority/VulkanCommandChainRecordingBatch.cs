using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Reusable command-runtime batch state shared by recording workers.</summary>
internal sealed class VulkanCommandChainRecordingBatch
{
    /// <summary>
    /// Batch-scoped worker procedure. It is published only after the prepared
    /// frame is frozen and cleared before the batch is reused.
    /// </summary>
    public Action<CommandChainRecordingWorkerState>? WorkerProcedure;
    public readonly VulkanPreparedFrameRecording PreparedFrame = new();
    public CommandChain[] Chains = new CommandChain[16];
    public CommandBuffer[] SecondaryBuffers = new CommandBuffer[16];
    public int[] RecordJobChainIndices = new int[16];
    public int[] RecordJobWorkerIndices = new int[16];
    public int[] UniformSlots = new int[16];
    public int StartIndex;
    public int ChainCount;
    public int JobCount;
    public uint ActiveWorkerMask;
    public Exception? Error;
    public long DispatchTimestamp;
    public long FirstWorkerStartTimestamp;
    public long LastWorkerCompletionTimestamp;
    public long WorkerRecordTimestampTotal;
    public long MaximumQueueDelayTimestamp;
    public int WorkersStarted;
    public int WorkersCompleted;
    public int ConcurrentWorkers;
    public int PeakConcurrentWorkers;
    public int QueuedChains;
    public int CancelRequested;
    public bool Abandoned;

    public void EnsureCapacity(int count)
    {
        if (Chains.Length >= count)
            return;

        int capacity = Math.Max(count, Chains.Length * 2);
        Array.Resize(ref Chains, capacity);
        Array.Resize(ref SecondaryBuffers, capacity);
        Array.Resize(ref RecordJobChainIndices, capacity);
        Array.Resize(ref RecordJobWorkerIndices, capacity);
        Array.Resize(ref UniformSlots, capacity);
    }

    public void ResetTiming()
    {
        DispatchTimestamp = 0;
        FirstWorkerStartTimestamp = long.MaxValue;
        LastWorkerCompletionTimestamp = 0;
        WorkerRecordTimestampTotal = 0;
        MaximumQueueDelayTimestamp = 0;
        WorkersStarted = 0;
        WorkersCompleted = 0;
        ConcurrentWorkers = 0;
        PeakConcurrentWorkers = 0;
        QueuedChains = 0;
        CancelRequested = 0;
        Abandoned = false;
    }

    public void ClearReferences()
    {
        Array.Clear(Chains, 0, ChainCount);
        PreparedFrame.Reset();
        ChainCount = 0;
        JobCount = 0;
        ActiveWorkerMask = 0;
        Error = null;
        WorkerProcedure = null;
    }
}

namespace XREngine.Rendering.Vulkan;

/// <summary>Persistent worker synchronization state, isolated from renderer-owned recording logic.</summary>
internal sealed class VulkanCommandWorkerSynchronization
{
    internal object Gate { get; } = new();
    internal ManualResetEventSlim Idle { get; } = new(initialState: true);
    internal CountdownEvent Countdown { get; } = new(initialCount: 1);
    internal int Generation;
    internal int ActiveWorkerCount;
    internal int Faulted;
    internal VulkanCommandChainRecordingBatch Batch { get; set; } = new();
    internal CommandChainRecordingWorkerState[]? WorkerStates { get; set; }
}

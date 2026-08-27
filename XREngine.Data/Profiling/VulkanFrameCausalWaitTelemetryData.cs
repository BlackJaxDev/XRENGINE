using MemoryPack;

namespace XREngine.Data.Profiling;

/// <summary>Serializable causal payload for a Vulkan wait above the capture threshold.</summary>
[MemoryPackable]
public sealed partial class VulkanFrameCausalWaitTelemetryData
{
    public string Stage { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public double ElapsedMs { get; set; }
    public ulong FrameId { get; set; }
    public int FrameSlot { get; set; }
    public int ImageIndex { get; set; }
    public ulong SemaphoreTargetValue { get; set; }
    public ulong SemaphoreCompletedValue { get; set; }
    public uint QueueFamily { get; set; }
    public int PendingCommandCount { get; set; }
    public int ConcurrentWorkerActivity { get; set; }
}

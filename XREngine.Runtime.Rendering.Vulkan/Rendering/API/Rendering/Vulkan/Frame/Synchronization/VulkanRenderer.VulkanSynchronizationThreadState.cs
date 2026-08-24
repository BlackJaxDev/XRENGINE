using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Holds per-thread synchronization flags and focused native ABI scratch owners.
/// </summary>
internal sealed class VulkanSynchronizationThreadState : IDisposable
{
    internal readonly record struct ScratchTelemetry(
        long Reservations, long RequestedBytes, long HighWaterBytes);
    /// <summary>Scoped synchronization2 wait-semaphore ABI storage.</summary>
    public readonly VulkanNativeScratchArena<SemaphoreSubmitInfo> SubmitWaitInfoScratch = new();

    /// <summary>Scoped synchronization2 signal-semaphore ABI storage.</summary>
    public readonly VulkanNativeScratchArena<SemaphoreSubmitInfo> SubmitSignalInfoScratch = new();

    /// <summary>Scoped synchronization2 command-buffer ABI storage.</summary>
    public readonly VulkanNativeScratchArena<CommandBufferSubmitInfo> SubmitCommandBufferInfoScratch = new();

    /// <summary>Reusable synchronization2 memory-barrier ABI storage.</summary>
    public readonly VulkanNativeScratchArena<MemoryBarrier2> MemoryBarrier2Scratch = new();

    /// <summary>Reusable synchronization2 buffer-barrier ABI storage.</summary>
    public readonly VulkanNativeScratchArena<BufferMemoryBarrier2> BufferMemoryBarrier2Scratch = new();

    /// <summary>Reusable legacy buffer-barrier ABI storage.</summary>
    public readonly VulkanNativeScratchArena<BufferMemoryBarrier> BufferMemoryBarrierScratch = new();

    /// <summary>Reusable synchronization2 image-barrier ABI storage.</summary>
    public readonly VulkanNativeScratchArena<ImageMemoryBarrier2> ImageMemoryBarrier2Scratch = new();

    /// <summary>Reusable legacy image-barrier ABI storage.</summary>
    public readonly VulkanNativeScratchArena<ImageMemoryBarrier> ImageMemoryBarrierScratch = new();
    public readonly VulkanNativeScratchArena<ClearAttachment> ClearAttachmentScratch = new();
    public readonly VulkanNativeScratchArena<ClearValue> ClearValueScratch = new();
    public readonly VulkanNativeScratchArena<Format> FormatScratch = new();
    public readonly VulkanNativeScratchArena<uint> UIntScratch = new();
    /// <summary>Independent dynamic-rendering attachment-location column.</summary>
    public readonly VulkanNativeScratchArena<uint> AttachmentLocationScratch = new();
    /// <summary>Independent dynamic-rendering input-attachment-index column.</summary>
    public readonly VulkanNativeScratchArena<uint> InputAttachmentIndexScratch = new();

    internal ScratchTelemetry GetBarrierScratchTelemetry()
        => new(
            MemoryBarrier2Scratch.ReservationCount + BufferMemoryBarrier2Scratch.ReservationCount +
            ImageMemoryBarrier2Scratch.ReservationCount + BufferMemoryBarrierScratch.ReservationCount +
            ImageMemoryBarrierScratch.ReservationCount,
            MemoryBarrier2Scratch.RequestedBytes + BufferMemoryBarrier2Scratch.RequestedBytes +
            ImageMemoryBarrier2Scratch.RequestedBytes + BufferMemoryBarrierScratch.RequestedBytes +
            ImageMemoryBarrierScratch.RequestedBytes,
            Math.Max(Math.Max(MemoryBarrier2Scratch.HighWaterBytes, BufferMemoryBarrier2Scratch.HighWaterBytes),
                Math.Max(ImageMemoryBarrier2Scratch.HighWaterBytes,
                    Math.Max(BufferMemoryBarrierScratch.HighWaterBytes, ImageMemoryBarrierScratch.HighWaterBytes))));

    internal readonly record struct BarrierExecutionTelemetry(
        long Reservations, long RequestedBytes, long HighWaterBytes, int GraphEdgeCount);

    /// <summary>Aggregate export point; caller supplies the current frozen graph edge count.</summary>
    internal BarrierExecutionTelemetry GetBarrierExecutionTelemetry(int graphEdgeCount)
    {
        ScratchTelemetry scratch = GetBarrierScratchTelemetry();
        return new BarrierExecutionTelemetry(
            scratch.Reservations,
            scratch.RequestedBytes,
            scratch.HighWaterBytes,
            graphEdgeCount);
    }

    /// <summary>
    /// Restores default flags and releases references to arrays retained by
    /// the current thread.
    /// </summary>
    public void Reset()
    {
        SubmitWaitInfoScratch.Reset();
        SubmitSignalInfoScratch.Reset();
        SubmitCommandBufferInfoScratch.Reset();
        MemoryBarrier2Scratch.Reset();
        BufferMemoryBarrier2Scratch.Reset();
        BufferMemoryBarrierScratch.Reset();
        ImageMemoryBarrier2Scratch.Reset();
        ImageMemoryBarrierScratch.Reset();
        ClearAttachmentScratch.Reset();
        ClearValueScratch.Reset();
        FormatScratch.Reset();
        UIntScratch.Reset();
        AttachmentLocationScratch.Reset();
        InputAttachmentIndexScratch.Reset();
    }

    /// <summary>Deterministically releases the native scratch allocations owned by this thread state.</summary>
    public void Dispose()
    {
        SubmitWaitInfoScratch.Dispose();
        SubmitSignalInfoScratch.Dispose();
        SubmitCommandBufferInfoScratch.Dispose();
        MemoryBarrier2Scratch.Dispose();
        BufferMemoryBarrier2Scratch.Dispose();
        BufferMemoryBarrierScratch.Dispose();
        ImageMemoryBarrier2Scratch.Dispose();
        ImageMemoryBarrierScratch.Dispose();
        ClearAttachmentScratch.Dispose();
        ClearValueScratch.Dispose();
        FormatScratch.Dispose();
        UIntScratch.Dispose();
        AttachmentLocationScratch.Dispose();
        InputAttachmentIndexScratch.Dispose();
    }
}

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable post-submit ownership for multiple imported texture chunks recorded
/// in one graphics command buffer.  No contained lease may be released until the
/// batch fence completes.
/// </summary>
internal sealed class VulkanSubmittedImportedTextureUploadBatch(
    VulkanImportedTexturePendingUpload[] uploads,
    CommandBuffer commandBuffer,
    CommandPool commandPool,
    Fence fence,
    long submitTimestamp,
    long bytesInFlight,
    VulkanTextureUploadGpuTimestampLease gpuTimestampLease)
{
    private string? _terminalFailureReason;
    private string? _cancellationReason;
    private int _fenceCompletionProven;
    // 0 = owned and pending, 1 = native cleanup in progress, 2 = completed,
    // 3 = cleanup faulted and remains quarantined for device teardown.
    private int _nativeCompletionState;

    public VulkanImportedTexturePendingUpload[] Uploads { get; } = uploads;
    public CommandBuffer CommandBuffer { get; } = commandBuffer;
    public CommandPool CommandPool { get; } = commandPool;
    public Fence Fence { get; } = fence;
    public long SubmitTimestamp { get; } = submitTimestamp;
    public long BytesInFlight { get; } = bytesInFlight;
    /// <summary>
    /// A fixed query-pool pair remains batch-owned until both the fence has
    /// completed and the tracked command buffer has been released.
    /// </summary>
    public VulkanTextureUploadGpuTimestampLease GpuTimestampLease { get; } = gpuTimestampLease;
    public bool HasTerminalFailure => Volatile.Read(ref _terminalFailureReason) is not null;
    public string? TerminalFailureReason => Volatile.Read(ref _terminalFailureReason);
    public bool IsCancellationRequested => Volatile.Read(ref _cancellationReason) is not null;
    public string? CancellationReason => Volatile.Read(ref _cancellationReason);
    public bool IsFenceCompletionProven => Volatile.Read(ref _fenceCompletionProven) != 0;
    public bool IsNativeCompletionFinished => Volatile.Read(ref _nativeCompletionState) == 2;
    public bool IsNativeCompletionFaulted => Volatile.Read(ref _nativeCompletionState) == 3;
    public bool IsNativeCompletionInProgress => Volatile.Read(ref _nativeCompletionState) == 1;
    public bool TryMarkTerminalFailure(string reason)
        => Interlocked.CompareExchange(ref _terminalFailureReason, reason, null) is null;

    public bool TryMarkCancellationRequested(string reason)
        => Interlocked.CompareExchange(ref _cancellationReason, reason, null) is null;

    public void MarkFenceCompletionProven()
        => Volatile.Write(ref _fenceCompletionProven, 1);

    public bool TryBeginNativeCompletion()
        => IsFenceCompletionProven &&
           Interlocked.CompareExchange(ref _nativeCompletionState, 1, 0) == 0;

    public void MarkNativeCompletionFinished()
        => Volatile.Write(ref _nativeCompletionState, 2);

    public void MarkNativeCompletionFaulted()
        => Volatile.Write(ref _nativeCompletionState, 3);
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanSubmittedImportedTextureUpload(
    VulkanImportedTexturePendingUpload upload,
    CommandBuffer commandBuffer,
    CommandPool commandPool,
    Fence fence,
    long submitTimestamp,
    long bytesInFlight)
{
    private string? _terminalFailureReason;

    public VulkanImportedTexturePendingUpload Upload { get; } = upload;
    public CommandBuffer CommandBuffer { get; } = commandBuffer;
    public CommandPool CommandPool { get; } = commandPool;
    public Fence Fence { get; } = fence;
    public long SubmitTimestamp { get; } = submitTimestamp;
    public long BytesInFlight { get; } = bytesInFlight;

    public bool HasTerminalFailure =>
        Volatile.Read(ref _terminalFailureReason) is not null;

    public bool TryMarkTerminalFailure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return Interlocked.CompareExchange(
            ref _terminalFailureReason,
            reason,
            comparand: null) is null;
    }

    public bool TryGetTerminalFailure(out string reason)
    {
        string? published = Volatile.Read(ref _terminalFailureReason);
        reason = published ?? string.Empty;
        return published is not null;
    }
}


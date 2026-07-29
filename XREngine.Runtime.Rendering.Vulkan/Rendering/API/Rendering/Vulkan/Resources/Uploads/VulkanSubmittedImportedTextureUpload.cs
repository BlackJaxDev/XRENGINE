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
    bool requiresGraphicsAcquire,
    uint transferQueueFamily,
    uint graphicsQueueFamily,
    long submitTimestamp,
    long bytesInFlight)
{
    public VulkanImportedTexturePendingUpload Upload { get; } = upload;
    public CommandBuffer CommandBuffer { get; } = commandBuffer;
    public CommandPool CommandPool { get; } = commandPool;
    public Fence Fence { get; } = fence;
    public bool RequiresGraphicsAcquire { get; } = requiresGraphicsAcquire;
    public uint TransferQueueFamily { get; } = transferQueueFamily;
    public uint GraphicsQueueFamily { get; } = graphicsQueueFamily;
    public long SubmitTimestamp { get; } = submitTimestamp;
    public long BytesInFlight { get; } = bytesInFlight;
}


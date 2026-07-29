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

internal readonly record struct VulkanImportedTextureUploadResult(
    long SourceGeneration,
    VulkanImportedTextureUploadResultState State,
    Image Image,
    DeviceMemory Memory,
    ImageView ImageView,
    Sampler Sampler,
    ImageLayout FinalLayout,
    VulkanImportedTextureUploadMipRange ResidentMipRange,
    uint ResidentMaxDimension,
    long CommittedBytes,
    ulong DescriptorPublicationToken,
    string? FailureReason)
{
    public static VulkanImportedTextureUploadResult Canceled(
        long sourceGeneration,
        VulkanImportedTextureUploadMipRange mipRange,
        string reason)
        => new(
            sourceGeneration,
            VulkanImportedTextureUploadResultState.Canceled,
            default,
            default,
            default,
            default,
            ImageLayout.Undefined,
            mipRange,
            0u,
            0L,
            0UL,
            reason);

    public static VulkanImportedTextureUploadResult Failed(
        long sourceGeneration,
        VulkanImportedTextureUploadMipRange mipRange,
        string reason)
        => new(
            sourceGeneration,
            VulkanImportedTextureUploadResultState.Failed,
            default,
            default,
            default,
            default,
            ImageLayout.Undefined,
            mipRange,
            0u,
            0L,
            0UL,
            reason);
}


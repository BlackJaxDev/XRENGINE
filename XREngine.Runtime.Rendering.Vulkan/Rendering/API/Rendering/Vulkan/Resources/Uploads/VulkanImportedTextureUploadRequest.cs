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

internal readonly record struct VulkanImportedTextureUploadRequest(
    WeakReference<XRTexture2D> Texture,
    string? TextureName,
    string? SourcePath,
    uint TargetResidentMaxDimension,
    VulkanImportedTextureUploadMipRange MipRange,
    ESizedInternalFormat Format,
    string? ColorSpace,
    long EstimatedBytes,
    VulkanTextureUploadTicket Ticket,
    long StreamingGeneration,
    TextureUploadPriorityClass PriorityClass,
    CancellationToken CancellationToken)
{
    public bool TryGetTexture(out XRTexture2D? texture)
        => Texture.TryGetTarget(out texture);

    public TextureUploadKind UploadKind
        => TargetResidentMaxDimension <= XRTexture2D.ImportedPreviewMaxDimensionInternal
            ? TextureUploadKind.Preview
            : TextureUploadKind.Promotion;
}


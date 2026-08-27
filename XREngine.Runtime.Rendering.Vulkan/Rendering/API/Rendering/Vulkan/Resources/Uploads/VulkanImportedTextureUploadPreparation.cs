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

internal sealed class VulkanImportedTextureUploadPreparation(
    VulkanImportedTextureUploadRequest request,
    VulkanTextureUploadTicket ticket,
    VkTexture2D texture,
    TextureStreamingResidentData residentData,
    bool includeMipChain,
    ulong publicationToken,
    Func<bool>? shouldAcceptResult,
    Action<XRTexture2D>? onFinished,
    Action? onCanceled,
    Action<Exception>? onError,
    Format format,
    ImageAspectFlags aspectMask,
    ImageUsageFlags usage,
    ImageLayout finalLayout,
    Extent3D extent,
    uint mipLevels,
    uint arrayLayers,
    string debugName)
{
    public VulkanImportedTextureUploadRequest Request { get; } = request;
    public VulkanTextureUploadTicket Ticket { get; } = ticket;
    public VkTexture2D Texture { get; } = texture;
    public TextureStreamingResidentData ResidentData { get; } = residentData;
    public bool IncludeMipChain { get; } = includeMipChain;
    public ulong PublicationToken { get; } = publicationToken;
    public Func<bool>? ShouldAcceptResult { get; } = shouldAcceptResult;
    public Action<XRTexture2D>? OnFinished { get; } = onFinished;
    public Action? OnCanceled { get; } = onCanceled;
    public Action<Exception>? OnError { get; } = onError;
    public Format Format { get; } = format;
    public ImageAspectFlags AspectMask { get; } = aspectMask;
    public ImageUsageFlags Usage { get; } = usage;
    public ImageLayout FinalLayout { get; } = finalLayout;
    public Extent3D Extent { get; } = extent;
    public uint MipLevels { get; } = mipLevels;
    public uint ArrayLayers { get; } = arrayLayers;
    public string DebugName { get; } = debugName;
    public List<VulkanImportedTextureUploadStagingResource> StagingResources { get; } = new List<VulkanImportedTextureUploadStagingResource>(Math.Max(residentData.Mipmaps.Length, 1));
    public long PrepStartTimestamp { get; } = TextureRuntimeDiagnostics.StartTiming();
    public VulkanImportedTextureUploadPreparationStep Step { get; set; } = VulkanImportedTextureUploadPreparationStep.CreateImage;
    public int NextMipLevel { get; set; }
    public Image Image;
    public DeviceMemory Memory;
    public ImageView ImageView;
    public Sampler Sampler;
    public long CommittedBytes;

    public bool ShouldAccept()
        => !Request.CancellationToken.IsCancellationRequested
            && (ShouldAcceptResult is null || ShouldAcceptResult());
}


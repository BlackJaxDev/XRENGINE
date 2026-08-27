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

internal sealed class VulkanImportedTexturePendingUpload(
    VulkanImportedTextureUploadRequest request,
    VulkanTextureUploadTicket ticket,
    VkTexture2D texture,
    Image image,
    DeviceMemory memory,
    ImageView imageView,
    Sampler sampler,
    Format format,
    ImageAspectFlags aspectMask,
    ImageUsageFlags usage,
    ImageLayout finalLayout,
    Extent3D extent,
    uint mipLevels,
    uint arrayLayers,
    long committedBytes,
    ulong publicationToken,
    VulkanImportedTextureUploadStagingResource[] stagingResources,
    Func<bool>? shouldAcceptResult,
    Action<XRTexture2D>? onFinished,
    Action? onCanceled,
    Action<Exception>? onError)
{
    private int _preparedResourcesReleased;
    private int _stagingResourcesReleased;

    public VulkanImportedTextureUploadRequest Request { get; } = request;
    public VulkanTextureUploadTicket Ticket { get; } = ticket;
    public VkTexture2D Texture { get; } = texture;
    public Image Image { get; private set; } = image;
    public DeviceMemory Memory { get; private set; } = memory;
    public ImageView ImageView { get; private set; } = imageView;
    public Sampler Sampler { get; private set; } = sampler;
    public Format Format { get; } = format;
    public ImageAspectFlags AspectMask { get; } = aspectMask;
    public ImageUsageFlags Usage { get; } = usage;
    public ImageLayout FinalLayout { get; } = finalLayout;
    public AccessFlags FinalAccessMask { get; } =
        finalLayout == ImageLayout.General
            ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
            : AccessFlags.ShaderReadBit;
    public PipelineStageFlags FinalPipelineStages { get; } =
        PipelineStageFlags.VertexShaderBit |
        PipelineStageFlags.FragmentShaderBit |
        PipelineStageFlags.ComputeShaderBit;
    public Extent3D Extent { get; } = extent;
    public uint MipLevels { get; } = mipLevels;
    public uint ArrayLayers { get; } = arrayLayers;
    public long CommittedBytes { get; } = committedBytes;
    public ulong PublicationToken { get; } = publicationToken;
    public VulkanImportedTextureUploadStagingResource[] StagingResources { get; } = stagingResources;
    public Func<bool>? ShouldAcceptResult { get; } = shouldAcceptResult;
    public Action<XRTexture2D>? OnFinished { get; } = onFinished;
    public Action? OnCanceled { get; } = onCanceled;
    public Action<Exception>? OnError { get; } = onError;
    public long PreparedTimestamp { get; } = TextureRuntimeDiagnostics.StartTiming();
    public long RecordTimestamp { get; private set; }
    public long PublicationTimestamp { get; private set; }

    public bool TryGetTexture(out XRTexture2D? texture)
        => Request.TryGetTexture(out texture);

    public bool ShouldPublish()
        => !Request.CancellationToken.IsCancellationRequested
            && (ShouldAcceptResult is null || ShouldAcceptResult());

    public bool TryValidateCopyRegions(out string? failureReason)
        => VulkanImportedTextureUploadValidation.TryValidateCopyRegions(
            Request.TextureName,
            PublicationToken,
            Extent,
            MipLevels,
            ArrayLayers,
            StagingResources,
            out failureReason);

    public void MarkRecordStarted()
        => RecordTimestamp = TextureRuntimeDiagnostics.StartTiming();

    public void MarkPublished()
        => PublicationTimestamp = TextureRuntimeDiagnostics.StartTiming();

    public void DetachPublishedImageHandles()
    {
        // Publication has transferred ownership to the texture wrapper. Any
        // later cleanup path must be a no-op even if old-generation retirement
        // or telemetry reports an exception after the descriptor commit.
        Interlocked.Exchange(ref _preparedResourcesReleased, 1);
        Image = default;
        Memory = default;
        ImageView = default;
        Sampler = default;
    }

    /// <summary>Ensures failure/cancellation cleanup retires owned image and staging resources once.</summary>
    public bool TryMarkPreparedResourcesReleased()
        => Interlocked.Exchange(ref _preparedResourcesReleased, 1) == 0;

    /// <summary>Ensures pooled staging buffers are returned or retired exactly once.</summary>
    public bool TryMarkStagingResourcesReleased()
        => Interlocked.Exchange(ref _stagingResourcesReleased, 1) == 0;
}


using System;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal enum VulkanTextureUploadGenerationState
{
    Decoded,
    UploadQueued,
    UploadRecording,
    GpuUploadPending,
    Uploaded,
    DescriptorPublishPending,
    Published,
    Retired,
    Canceled,
    Failed,
}

internal enum VulkanImportedTextureUploadResultState
{
    Success,
    Canceled,
    Failed,
}

internal readonly record struct VulkanImportedTextureUploadMipRange(
    int FirstMipLevel,
    int MipLevelCount,
    uint ExpectedWidth,
    uint ExpectedHeight);

internal readonly record struct VulkanImportedTextureUploadRequest(
    WeakReference<XRTexture2D> Texture,
    string? TextureName,
    string? SourcePath,
    uint TargetResidentMaxDimension,
    VulkanImportedTextureUploadMipRange MipRange,
    ESizedInternalFormat Format,
    string? ColorSpace,
    long EstimatedBytes,
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

internal readonly record struct VulkanImportedTextureUploadStagingResource(
    Buffer Buffer,
    DeviceMemory Memory,
    BufferImageCopy CopyRegion,
    ulong SizeBytes);

internal sealed class VulkanImportedTexturePendingUpload
{
    public VulkanImportedTexturePendingUpload(
        VulkanImportedTextureUploadRequest request,
        VulkanRenderer.VkTexture2D texture,
        Image image,
        DeviceMemory memory,
        ImageView imageView,
        Sampler sampler,
        Format format,
        ImageAspectFlags aspectMask,
        Extent3D extent,
        uint mipLevels,
        long committedBytes,
        ulong publicationToken,
        VulkanImportedTextureUploadStagingResource[] stagingResources,
        Func<bool>? shouldAcceptResult,
        Action<XRTexture2D>? onFinished,
        Action? onCanceled,
        Action<Exception>? onError)
    {
        Request = request;
        Texture = texture;
        Image = image;
        Memory = memory;
        ImageView = imageView;
        Sampler = sampler;
        Format = format;
        AspectMask = aspectMask;
        Extent = extent;
        MipLevels = mipLevels;
        CommittedBytes = committedBytes;
        PublicationToken = publicationToken;
        StagingResources = stagingResources;
        ShouldAcceptResult = shouldAcceptResult;
        OnFinished = onFinished;
        OnCanceled = onCanceled;
        OnError = onError;
    }

    public VulkanImportedTextureUploadRequest Request { get; }
    public VulkanRenderer.VkTexture2D Texture { get; }
    public Image Image { get; private set; }
    public DeviceMemory Memory { get; private set; }
    public ImageView ImageView { get; private set; }
    public Sampler Sampler { get; private set; }
    public Format Format { get; }
    public ImageAspectFlags AspectMask { get; }
    public Extent3D Extent { get; }
    public uint MipLevels { get; }
    public long CommittedBytes { get; }
    public ulong PublicationToken { get; }
    public VulkanImportedTextureUploadStagingResource[] StagingResources { get; }
    public Func<bool>? ShouldAcceptResult { get; }
    public Action<XRTexture2D>? OnFinished { get; }
    public Action? OnCanceled { get; }
    public Action<Exception>? OnError { get; }
    public long PreparedTimestamp { get; } = TextureRuntimeDiagnostics.StartTiming();
    public long RecordTimestamp { get; private set; }
    public long PublicationTimestamp { get; private set; }

    public bool TryGetTexture(out XRTexture2D? texture)
        => Request.TryGetTexture(out texture);

    public bool ShouldPublish()
        => !Request.CancellationToken.IsCancellationRequested
            && (ShouldAcceptResult is null || ShouldAcceptResult());

    public void MarkRecordStarted()
        => RecordTimestamp = TextureRuntimeDiagnostics.StartTiming();

    public void MarkPublished()
        => PublicationTimestamp = TextureRuntimeDiagnostics.StartTiming();

    public void DetachPublishedImageHandles()
    {
        Image = default;
        Memory = default;
        ImageView = default;
        Sampler = default;
    }
}

/// <summary>
/// Synchronized Vulkan imported texture upload service. The service prepares
/// staging/new image resources on the render thread, then publishes them through
/// a frame-timeline operation so descriptor swaps are ordered with the copy.
/// </summary>
internal sealed class VulkanTextureUploadService
{
    public const string EnableSynchronizedImportedTextureUploadsEnvVar = "XRE_VULKAN_TEXTURE_UPLOAD_SERVICE";

    private static int s_synchronizedImportedTextureStreamingAvailable = 1;
    private long _nextDescriptorPublicationToken;

    public static bool IsSynchronizedImportedTextureStreamingAvailable
        => Volatile.Read(ref s_synchronizedImportedTextureStreamingAvailable) != 0;

    internal static void SetSynchronizedImportedTextureStreamingAvailable(bool available)
        => Volatile.Write(ref s_synchronizedImportedTextureStreamingAvailable, available ? 1 : 0);

    public static bool IsExplicitlyRequested()
        => string.Equals(
            Environment.GetEnvironmentVariable(EnableSynchronizedImportedTextureUploadsEnvVar),
            "1",
            StringComparison.OrdinalIgnoreCase);

    public bool ShouldAcceptResult(
        in VulkanImportedTextureUploadRequest request,
        long currentStreamingGeneration)
        => !request.CancellationToken.IsCancellationRequested
            && request.StreamingGeneration == currentStreamingGeneration;

    public VulkanImportedTextureUploadResult RejectStaleOrCanceledResult(
        in VulkanImportedTextureUploadRequest request,
        long currentStreamingGeneration)
    {
        string reason = request.CancellationToken.IsCancellationRequested
            ? "request cancellation token is canceled"
            : $"stale generation request={request.StreamingGeneration} current={currentStreamingGeneration}";

        TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadRejected(
            RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
            request.TextureName,
            request.SourcePath,
            request.StreamingGeneration,
            currentStreamingGeneration,
            reason);

        return VulkanImportedTextureUploadResult.Canceled(
            request.StreamingGeneration,
            request.MipRange,
            reason);
    }

    public ulong AllocateDescriptorPublicationToken()
        => unchecked((ulong)Interlocked.Increment(ref _nextDescriptorPublicationToken));

    public void RecordState(
        in VulkanImportedTextureUploadRequest request,
        VulkanTextureUploadGenerationState state,
        string? detail = null)
    {
        TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadState(
            RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
            request.TextureName,
            request.SourcePath,
            request.StreamingGeneration,
            request.TargetResidentMaxDimension,
            request.EstimatedBytes,
            request.PriorityClass,
            state,
            detail);
    }

    public bool TryQueueImportedTextureUpload(
        in VulkanImportedTextureUploadRequest request,
        long currentStreamingGeneration,
        out VulkanImportedTextureUploadResult immediateResult)
    {
        if (!ShouldAcceptResult(request, currentStreamingGeneration))
        {
            immediateResult = RejectStaleOrCanceledResult(request, currentStreamingGeneration);
            return false;
        }

        RecordState(request, VulkanTextureUploadGenerationState.UploadQueued, "service contract accepted request");
        immediateResult = default;
        return true;
    }

    public bool TryScheduleImportedTextureUpload(
        VulkanRenderer renderer,
        XRTexture2D texture,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        uint targetResidentMaxDimension,
        long streamingGeneration,
        TextureUploadPriorityClass priorityClass,
        Func<bool>? shouldAcceptResult,
        Action<XRTexture2D>? onFinished,
        Action? onCanceled,
        Action<Exception>? onError,
        CancellationToken cancellationToken)
    {
        if (renderer.IsDeviceLost)
        {
            onCanceled?.Invoke();
            return false;
        }

        long estimatedBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);
        VulkanImportedTextureUploadRequest request = new(
            new WeakReference<XRTexture2D>(texture),
            texture.Name,
            texture.FilePath,
            targetResidentMaxDimension,
            new VulkanImportedTextureUploadMipRange(
                0,
                residentData.Mipmaps.Length,
                residentData.Mipmaps.Length > 0 ? residentData.Mipmaps[0].Width : 0u,
                residentData.Mipmaps.Length > 0 ? residentData.Mipmaps[0].Height : 0u),
            ESizedInternalFormat.Rgba8,
            null,
            estimatedBytes,
            streamingGeneration,
            priorityClass,
            cancellationToken);

        if ((shouldAcceptResult is not null && !shouldAcceptResult())
            || !TryQueueImportedTextureUpload(request, streamingGeneration, out _))
        {
            RecordState(request, VulkanTextureUploadGenerationState.Canceled, "stale or canceled before resource preparation");
            onCanceled?.Invoke();
            return false;
        }

        if (renderer.GetOrCreateAPIRenderObject(texture, generateNow: false) is not VulkanRenderer.VkTexture2D vkTexture)
        {
            InvalidOperationException ex = new("Vulkan texture wrapper could not be resolved for imported texture upload.");
            RecordState(request, VulkanTextureUploadGenerationState.Failed, ex.Message);
            onError?.Invoke(ex);
            return false;
        }

        ulong publicationToken = AllocateDescriptorPublicationToken();
        if (!vkTexture.TryCreateSynchronizedImportedUpload(
                request,
                residentData,
                includeMipChain,
                publicationToken,
                shouldAcceptResult,
                onFinished,
                onCanceled,
                onError,
                out VulkanImportedTexturePendingUpload? pendingUpload,
                out string? failureReason)
            || pendingUpload is null)
        {
            RecordState(request, VulkanTextureUploadGenerationState.Failed, failureReason ?? "failed to prepare Vulkan upload resources");
            onError?.Invoke(new InvalidOperationException(failureReason ?? "Failed to prepare Vulkan upload resources."));
            return false;
        }

        RecordState(request, VulkanTextureUploadGenerationState.GpuUploadPending, "queued frame-timeline upload op");
        renderer.EnqueueImportedTextureUpload(pendingUpload);
        return true;
    }
}

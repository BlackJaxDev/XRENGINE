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
    TextureStreamingResidentData residentData,
    bool includeMipChain,
    Func<bool>? shouldAcceptResult,
    Action<XRTexture2D>? onFinished,
    Action? onCanceled,
    Action<Exception>? onError)
{
    private int _preparedResourcesReleased;
    private VulkanImportedTextureUploadStagingResource[] _stagingResources = [];
    private int _nextMipLevel;
    private uint _nextMipRow;

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
    /// <summary>
    /// Immutable decoded source retained by the ticket.  Staging is deliberately
    /// not built here: one worker-admitted chunk owns one bounded staging lease.
    /// </summary>
    public TextureStreamingResidentData ResidentData { get; } = residentData;
    public bool IncludeMipChain { get; } = includeMipChain;
    public VulkanImportedTextureUploadStagingResource[] StagingResources => Volatile.Read(ref _stagingResources);
    public int NextMipLevel => Volatile.Read(ref _nextMipLevel);
    public uint NextMipRow => Volatile.Read(ref _nextMipRow);
    public bool HasRecordedChunk { get; private set; }
    public bool CurrentChunkIsFinal { get; private set; }
    internal VulkanTextureUploadService.VulkanImportedTextureUploadJob? OwnerJob { get; set; }
    public Func<bool>? ShouldAcceptResult { get; } = shouldAcceptResult;
    public Action<XRTexture2D>? OnFinished { get; } = onFinished;
    public Action? OnCanceled { get; } = onCanceled;
    public Action<Exception>? OnError { get; } = onError;
    public long PreparedTimestamp { get; } = TextureRuntimeDiagnostics.StartTiming();
    public long RecordTimestamp { get; private set; }
    public long PublicationTimestamp { get; private set; }

    internal bool IsPreparedResourcesReleased
        => Volatile.Read(ref _preparedResourcesReleased) != 0;

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

    /// <summary>
    /// Validates the immutable native ownership captured for one recorded
    /// chunk. This is deliberately checked immediately before command
    /// recording: retirement or a stale yielded result must fail closed rather
    /// than pass a null/recycled handle to the Vulkan driver.
    /// </summary>
    internal bool TryValidateTransferOwnership(
        VulkanResourceRuntime resources,
        out string? failureReason)
    {
        failureReason = null;
        if (IsPreparedResourcesReleased || Image.Handle == 0 || Memory.Handle == 0 ||
            ImageView.Handle == 0)
        {
            failureReason =
                $"Imported texture upload '{Request.TextureName ?? "<unnamed>"}' token={PublicationToken} lost its destination native ownership before transfer recording.";
            return false;
        }

        ulong imageGeneration = resources.GetPublishedGeneration(ObjectType.Image, Image.Handle);
        ulong viewGeneration = resources.GetPublishedGeneration(ObjectType.ImageView, ImageView.Handle);
        // Some image-backed texture configurations deliberately omit a sampler;
        // only validate its generation when this upload captured one.
        ulong samplerGeneration = Sampler.Handle == 0
            ? 1UL
            : resources.GetPublishedGeneration(ObjectType.Sampler, Sampler.Handle);
        if (imageGeneration == 0 || viewGeneration == 0 || samplerGeneration == 0)
        {
            failureReason =
                $"Imported texture upload '{Request.TextureName ?? "<unnamed>"}' token={PublicationToken} has an unpublished destination generation before transfer recording.";
            return false;
        }

        VulkanImportedTextureUploadStagingResource[] staging = StagingResources;
        if (staging.Length == 0)
        {
            failureReason =
                $"Imported texture upload '{Request.TextureName ?? "<unnamed>"}' token={PublicationToken} has no prepared staging chunk.";
            return false;
        }

        for (int index = 0; index < staging.Length; index++)
        {
            VulkanImportedTextureUploadStagingResource resource = staging[index];
            if (resource.Buffer.Handle == 0 || resource.Memory.Handle == 0 ||
                resource.AllocationGeneration == 0 ||
                resources.GetPublishedGeneration(ObjectType.Buffer, resource.Buffer.Handle) != resource.AllocationGeneration)
            {
                failureReason =
                    $"Imported texture upload '{Request.TextureName ?? "<unnamed>"}' token={PublicationToken} staging chunk {index} no longer has its captured buffer generation.";
                return false;
            }
        }

        return TryValidateCopyRegions(out failureReason);
    }

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

    internal void SetPreparedChunk(
        VulkanImportedTextureUploadStagingResource staging,
        int nextMipLevel,
        uint nextMipRow,
        bool isFinal)
    {
        if (StagingResources.Length != 0)
            throw new InvalidOperationException("An imported texture ticket already owns a staging chunk.");

        Volatile.Write(ref _stagingResources, [staging]);
        Volatile.Write(ref _nextMipLevel, nextMipLevel);
        Volatile.Write(ref _nextMipRow, nextMipRow);
        CurrentChunkIsFinal = isFinal;
    }

    internal VulkanImportedTextureUploadStagingResource[] DetachPreparedChunk()
    {
        VulkanImportedTextureUploadStagingResource[] staging = Interlocked.Exchange(ref _stagingResources, []);
        if (staging.Length != 0)
            HasRecordedChunk = true;
        return staging;
    }
}


using System;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly VulkanTextureUploadService _textureUploadService = new();

    internal VulkanTextureUploadService TextureUploadService => _textureUploadService;

    internal void RecordTextureUploadProgress(in TextureUploadTelemetry telemetry)
    {
        // Hook reserved for the Vulkan image uploader. OpenGL owns the first runtime
        // implementation, but the shared telemetry contract above the backend is now
        // renderer-neutral.
    }

    internal void EnqueueImportedTextureUpload(VulkanImportedTexturePendingUpload upload)
        => EnqueueFrameOp(new TextureUploadFrameOp(upload, CaptureFrameOpContextOrLastActive()));

    internal bool TryScheduleImportedTextureResidencyTransition(
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
        => _textureUploadService.TryScheduleImportedTextureUpload(
            this,
            texture,
            residentData,
            includeMipChain,
            targetResidentMaxDimension,
            streamingGeneration,
            priorityClass,
            shouldAcceptResult,
            onFinished,
            onCanceled,
            onError,
            cancellationToken);

    internal bool TryScheduleTextureResidencyTransition(
        in TextureResidencyTelemetry residency,
        in TextureUploadTelemetry upload)
        => VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable;
}

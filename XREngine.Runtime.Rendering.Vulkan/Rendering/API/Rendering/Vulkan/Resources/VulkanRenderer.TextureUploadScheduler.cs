namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer : IVulkanTextureUploadScheduler
{
    bool IVulkanTextureUploadScheduler.IsSynchronizedUploadAvailable
        => VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable;

    bool IVulkanTextureUploadScheduler.TryScheduleImportedTextureUpload(
        XRTexture2D target,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        uint maxResidentDimension,
        long streamingGeneration,
        TextureUploadPriorityClass priority,
        Func<bool>? shouldAcceptResult,
        Action<XRTexture2D>? onFinished,
        Action? onCanceled,
        Action<Exception>? onError,
        CancellationToken cancellationToken)
        => TryScheduleImportedTextureResidencyTransition(
            target,
            residentData,
            includeMipChain,
            maxResidentDimension,
            streamingGeneration,
            priority,
            shouldAcceptResult,
            onFinished,
            onCanceled,
            onError,
            cancellationToken);
}

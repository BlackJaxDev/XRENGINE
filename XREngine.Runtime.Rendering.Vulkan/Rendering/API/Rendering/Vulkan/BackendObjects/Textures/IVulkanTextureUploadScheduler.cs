namespace XREngine.Rendering.Vulkan;

/// <summary>Renderer-scoped Vulkan upload scheduling service consumed by texture streaming.</summary>
internal interface IVulkanTextureUploadScheduler
{
    bool IsSynchronizedUploadAvailable { get; }

    /// <summary>
    /// Attempts to queue an imported texture upload. A rejected request returns
    /// <see langword="false"/> after invoking exactly one terminal callback.
    /// </summary>
    bool TryScheduleImportedTextureUpload(
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
        CancellationToken cancellationToken);
}

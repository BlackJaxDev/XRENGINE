using System;
using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Submits retained resident mips through the Vulkan upload service that owns
/// the current renderer, preserving its admission result and terminal callbacks.
/// </summary>
internal delegate bool VulkanResidentRehydrationUploadScheduler(
    TextureStreamingResidentData residentData,
    bool includeMipChain,
    uint targetResidentMaxDimension,
    long streamingGeneration,
    Func<bool> isCurrentTransition,
    CancellationToken cancellationToken,
    Action<XRTexture2D> onFinished,
    Action<Exception> onError,
    Action onCanceled);

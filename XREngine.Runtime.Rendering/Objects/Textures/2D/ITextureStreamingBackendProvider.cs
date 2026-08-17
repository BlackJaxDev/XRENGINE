using System.Text;

namespace XREngine.Rendering;

/// <summary>
/// Supplies imported-texture residency and upload services without exposing a concrete
/// graphics backend to the rendering kernel.
/// </summary>
internal interface ITextureStreamingBackendProvider
{
    ITextureResidencyBackend DefaultBackend { get; }
    ITextureResidencyBackend SparseBackend { get; }
    TextureStreamingBackendActivity GetActivity();
    bool IsDenseBackend(ITextureResidencyBackend backend);
    string GetDisplayName(ITextureResidencyBackend backend);
    bool IsSynchronizedUploadAvailable { get; }
    bool TryDescribeActiveUploadWork(out string reason);
    bool TryDescribeBlockingOpenXrEyeUploadWork(out string reason);
    void AppendProfilerSummary(StringBuilder builder);

    /// <summary>
    /// Attempts to handle a synchronized upload request. A <see langword="true"/>
    /// result transfers terminal-callback ownership to the provider, even when it
    /// rejects the request synchronously. A <see langword="false"/> result means the
    /// provider did not handle the request and invoked no callback.
    /// </summary>
    bool TryScheduleSynchronizedUpload(
        XRTexture2D target,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        uint maxResidentDimension,
        long streamingGeneration,
        TextureUploadPriorityClass priority,
        Func<bool>? shouldAcceptResult,
        CancellationToken cancellationToken,
        Action<XRTexture2D>? onFinished,
        Action<Exception>? onError,
        Action? onCanceled);
}

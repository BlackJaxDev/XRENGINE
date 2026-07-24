using System.Text;

namespace XREngine.Rendering.OpenGL;

internal sealed class OpenGlTextureStreamingBackendProvider : ITextureStreamingBackendProvider
{
    public static OpenGlTextureStreamingBackendProvider Instance { get; } = new();

    private readonly ITextureResidencyBackend _defaultBackend = new GLTieredTextureResidencyBackend();
    private readonly ITextureResidencyBackend _sparseBackend = new GLSparseTextureResidencyBackend();

    private OpenGlTextureStreamingBackendProvider()
    {
    }

    public ITextureResidencyBackend DefaultBackend => _defaultBackend;
    public ITextureResidencyBackend SparseBackend => _sparseBackend;
    public bool IsSynchronizedUploadAvailable => false;

    public bool IsDenseBackend(ITextureResidencyBackend backend) => false;
    public string GetDisplayName(ITextureResidencyBackend backend) => backend.Name;

    public bool TryDescribeActiveUploadWork(out string reason)
    {
        reason = string.Empty;
        return false;
    }

    public bool TryDescribeBlockingOpenXrEyeUploadWork(out string reason)
    {
        reason = string.Empty;
        return false;
    }

    public void AppendProfilerSummary(StringBuilder builder)
    {
    }

    public bool TryScheduleSynchronizedUpload(
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
        Action? onCanceled)
        => false;
}

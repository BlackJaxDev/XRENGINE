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

    public TextureStreamingBackendActivity GetActivity()
        => TextureStreamingBackendActivity.Capture(_defaultBackend, _sparseBackend);

    public bool IsDenseBackend(ITextureResidencyBackend backend) => false;
    public string GetDisplayName(ITextureResidencyBackend backend) => backend.Name;

    public bool TryDescribeActiveUploadWork(out string reason)
    {
        reason = string.Empty;
        return false;
    }

    public bool TryDescribeBlockingOpenXrEyeUploadWork(out string reason)
    {
        int activeGpuUploads = GetActivity().ActiveGpuUploadCount;
        if (activeGpuUploads <= 0)
        {
            reason = string.Empty;
            return false;
        }

        reason = $"OpenGL texture uploads have render-blocking work (activeGpuUploads={activeGpuUploads})";
        return true;
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

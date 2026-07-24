using System.Text;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanTextureStreamingBackendProvider : ITextureStreamingBackendProvider
{
    public static VulkanTextureStreamingBackendProvider Instance { get; } = new();

    private readonly ITextureResidencyBackend _backend = new VulkanDenseTextureResidencyBackend();

    private VulkanTextureStreamingBackendProvider()
    {
    }

    public ITextureResidencyBackend DefaultBackend => _backend;
    public ITextureResidencyBackend SparseBackend => _backend;
    public bool IsSynchronizedUploadAvailable => VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable;

    public bool IsDenseBackend(ITextureResidencyBackend backend) => ReferenceEquals(backend, _backend);
    public string GetDisplayName(ITextureResidencyBackend backend)
        => IsDenseBackend(backend)
            ? "Vulkan dense residency (compat, synchronized upload)"
            : backend.Name;

    public bool TryDescribeActiveUploadWork(out string reason)
        => VulkanTextureUploadService.TryDescribeActiveUploadWork(out reason);

    public bool TryDescribeBlockingOpenXrEyeUploadWork(out string reason)
        => VulkanTextureUploadService.TryDescribeBlockingOpenXrEyeUploadWork(out reason);

    public void AppendProfilerSummary(StringBuilder builder)
        => VulkanTextureUploadService.AppendProfilerSummary(builder);

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
    {
        if ((RuntimeRenderingHostServices.FrameTiming.CurrentRenderer ?? AbstractRenderer.Current) is not VulkanRenderer renderer)
        {
            onError?.Invoke(new InvalidOperationException("Vulkan imported texture upload service could not resolve the active Vulkan renderer."));
            return true;
        }

        if (!IsSynchronizedUploadAvailable)
        {
            onError?.Invoke(new InvalidOperationException("Vulkan synchronized imported texture upload service is not available."));
            return true;
        }

        return renderer.TryScheduleImportedTextureResidencyTransition(
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
}

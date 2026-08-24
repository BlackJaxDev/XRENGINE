using System.Text;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanTextureStreamingBackendProvider : ITextureStreamingBackendProvider
{
    public static VulkanTextureStreamingBackendProvider Instance { get; } = new();

    private readonly ITextureResidencyBackend _backend = new VulkanDenseTextureResidencyBackend();
    private IVulkanTextureUploadScheduler? _scheduler;

    private VulkanTextureStreamingBackendProvider()
    {
    }

    public ITextureResidencyBackend DefaultBackend => _backend;
    public ITextureResidencyBackend SparseBackend => _backend;
    public bool IsSynchronizedUploadAvailable => VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable;

    public TextureStreamingBackendActivity GetActivity()
        => TextureStreamingBackendActivity.Capture(
            _backend,
            _backend,
            VulkanTextureUploadService.HasActiveUploadWork);

    internal void BindScheduler(IVulkanTextureUploadScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        Interlocked.Exchange(ref _scheduler, scheduler);
    }

    internal void UnbindScheduler(IVulkanTextureUploadScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        Interlocked.CompareExchange(ref _scheduler, null, scheduler);
    }

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
        IVulkanTextureUploadScheduler? scheduler = Volatile.Read(ref _scheduler);
        if (scheduler is null)
        {
            onError?.Invoke(new InvalidOperationException("Vulkan imported texture upload service is not bound to a Vulkan renderer."));
            return true;
        }

        if (!scheduler.IsSynchronizedUploadAvailable)
        {
            onError?.Invoke(new InvalidOperationException("Vulkan synchronized imported texture upload service is not available."));
            return true;
        }

        _ = scheduler.TryScheduleImportedTextureUpload(
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
        return true;
    }
}

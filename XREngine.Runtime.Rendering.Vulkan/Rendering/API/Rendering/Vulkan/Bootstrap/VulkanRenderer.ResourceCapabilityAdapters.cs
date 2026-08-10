namespace XREngine.Rendering.Vulkan;

/// <summary>Renderer-facing capability adapters over resource-owned state.</summary>
public sealed unsafe partial class VulkanRenderer :
    IRenderResourceRetirementBackendCapability,
    IVulkanTextureUploadScheduler,
    IVulkanAllocatorStreamingBackendCapability
{
    public void PrepareForPhysicalResourceDestruction(string reason)
    {
        if (IsDeviceLost)
            return;

        ResourceRuntime.ReleaseDescriptorReferencesForPhysicalResourceDestruction(
            _commandRuntime,
            reason);
    }

    bool IVulkanTextureUploadScheduler.IsSynchronizedUploadAvailable
        => VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable;

    bool IVulkanTextureUploadScheduler.TryScheduleImportedTextureUpload(
        XRTexture2D target, TextureStreamingResidentData residentData, bool includeMipChain,
        uint maxResidentDimension, long streamingGeneration, TextureUploadPriorityClass priority,
        Func<bool>? shouldAcceptResult, Action<XRTexture2D>? onFinished, Action? onCanceled,
        Action<Exception>? onError, CancellationToken cancellationToken)
        => TryScheduleImportedTextureResidencyTransition(target, residentData, includeMipChain,
            maxResidentDimension, streamingGeneration, priority, shouldAcceptResult, onFinished,
            onCanceled, onError, cancellationToken);

    bool IVulkanAllocatorStreamingBackendCapability.TryGetAllocatorBudgetSnapshot(
        double budgetRatio, long reserveBytes, out long allocatedBytes, out long budgetBytes,
        out long largestHeapBytes, out int activeAllocationCount)
        => TryGetVulkanAllocatorBudgetSnapshot(budgetRatio, reserveBytes, out allocatedBytes,
            out budgetBytes, out largestHeapBytes, out activeAllocationCount);

    bool IVulkanAllocatorStreamingBackendCapability.IsExpectedImageAllocationDeferral(Exception exception)
        => IsExpectedVulkanImageAllocationDeferral(exception);
}

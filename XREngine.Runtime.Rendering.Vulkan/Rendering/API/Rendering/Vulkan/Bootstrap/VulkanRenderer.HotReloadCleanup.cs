namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Releases managed caches whose storage is associated with renderer worker threads.
    /// Collectible backend generations must not leave backend objects in reusable thread-local
    /// storage after the renderer has crossed its GPU-idle teardown boundary.
    /// </summary>
    private void ReleaseHotReloadManagedCaches()
    {
        ReleaseCurrentThreadStateTrackingCaches();
        ReleaseCurrentThreadOpenXrCaches();
        ReleaseCurrentThreadFrameOpCaptureCaches();
        ReleaseCurrentThreadSynchronizationScratch();
        _renderGraphCompiler.ReleaseCaches();
        VulkanFramePlanner.PassMetadataSignatureCache.Clear();
        ReleasePooledExternalResourcePlannerReadbackScopes();

        if (_commandRuntime.ThreadLocalScratchDisposed)
            return;

        _commandRuntime.ThreadLocalScratchDisposed = true;
        _commandBufferRecordingScratch.Dispose();
        ResourceRuntime.Lifetime.Tracker.ChangedDescriptorSetsScratch.Dispose();
        ResourceRuntime.Lifetime.Tracker.DescriptorReferencesScratch.Dispose();
        ResourceRuntime.Lifetime.Tracker.DescriptorPinnedReferencesScratch.Dispose();
    }
}

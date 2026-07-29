namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private bool _threadLocalScratchDisposed;

    /// <summary>
    /// Releases managed caches whose storage is associated with renderer worker threads.
    /// Collectible backend generations must not leave backend objects in runtime thread-static
    /// storage after the renderer has crossed its GPU-idle teardown boundary.
    /// </summary>
    private void ReleaseHotReloadManagedCaches()
    {
        ReleaseCurrentThreadStateTrackingCaches();
        ReleaseCurrentThreadOpenXrCaches();
        ReleaseCurrentThreadFrameOpCaptureCaches();
        ReleaseCurrentThreadSynchronizationScratch();
        _renderGraphCompiler.ReleaseCaches();
        PassMetadataSignatureCache.Clear();
        PooledExternalResourcePlannerReadbackScope.ReleaseCurrentThreadPool();
        FrameOp.ReleaseCurrentThreadPools();
        VkMeshRenderer.ReleaseCurrentThreadDescriptorScratch();

        if (_threadLocalScratchDisposed)
            return;

        _threadLocalScratchDisposed = true;
        _commandBufferRecordingScratch.Dispose();
        _resourceLifetimeTracker.ChangedDescriptorSetsScratch.Dispose();
        _resourceLifetimeTracker.DescriptorReferencesScratch.Dispose();
    }
}

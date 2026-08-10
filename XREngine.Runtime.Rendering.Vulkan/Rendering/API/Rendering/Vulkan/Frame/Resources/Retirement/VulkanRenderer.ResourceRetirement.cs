using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer boundary for deferred resource retirement. Queue ownership and drain
/// decisions live in the resource and command authorities.
/// </summary>
internal sealed unsafe partial class VulkanFrameLoop
{
    internal void DrainRetiredCommandPools(int frameSlot, int maxItems = 16)
        => _commandRuntime.DrainRetiredCommandPools(
            Api,
            _deviceContext.Device,
            ResourceRuntime,
            frameSlot,
            maxItems);

    internal void DrainRetiredCommandBuffers(int frameSlot, int maxItems = 128)
        => _commandRuntime.DrainRetiredCommandBuffers(
            Api,
            _deviceContext.Device,
            ResourceRuntime,
            frameSlot,
            maxItems);

    internal void DrainRetiredPipelines(int frameSlot, int maxItems = 8)
        => ResourceRuntime.DrainRetiredPipelines(
            Api,
            _deviceContext.Device,
            frameSlot,
            maxItems);

    internal void RetireDescriptorPool(DescriptorPool descriptorPool)
        => ResourceRuntime.DescriptorLifetime.RetireDescriptorPool(descriptorPool);

    internal void DrainRetiredDescriptorPools(int frameSlot, int maxItems = int.MaxValue)
        => ResourceRuntime.DrainRetiredDescriptorPools(
            Api,
            _deviceContext.Device,
            frameSlot,
            maxItems);

    internal void DrainRetiredDescriptorSets(int frameSlot, int maxItems = int.MaxValue)
        => ResourceRuntime.DrainRetiredDescriptorSets(
            Api,
            _deviceContext.Device,
            frameSlot,
            maxItems);

    internal void RetireQueryPool(QueryPool queryPool)
        => ResourceRuntime.RetireQueryPool(queryPool, "VulkanFrameLoop.QueryPool");

    internal void DrainRetiredQueryPools(int frameSlot, int maxItems = 32)
        => ResourceRuntime.DrainRetiredQueryPools(
            Api,
            _deviceContext.Device,
            frameSlot,
            maxItems);

    internal void DrainRetiredBufferViews(int frameSlot, int maxItems = 64)
        => ResourceRuntime.DrainRetiredBufferViews(
            Api,
            _deviceContext.Device,
            frameSlot,
            maxItems);

    internal void ReleaseDescriptorReferencesForPhysicalResourceDestruction(string reason)
        => ResourceRuntime.ReleaseDescriptorReferencesForPhysicalResourceDestruction(reason);

    internal void DrainRetiredFramebuffers(int frameSlot, int maxItems = 64)
        => ResourceRuntime.DrainRetiredFramebuffers(
            Api,
            _deviceContext.Device,
            frameSlot,
            maxItems);

    internal void RetireBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
        => ResourceRuntime.Buffers.Retire(buffer, memory, "VulkanFrameLoop.Buffer");

    internal void DrainRetiredBuffers(int frameSlot, int maxItems = 256)
    {
        int pooledBuffers = ResourceRuntime.DrainRetiredBuffers(
            Api,
            _deviceContext.Device,
            _telemetry,
            frameSlot,
            maxItems);
        if (pooledBuffers != 0)
            ResourceRuntime.Allocations.Staging.Trim(
                ResourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
                    "The Vulkan backend object context is not initialized."));
    }

    internal void WaitForAllInFlightWork()
    {
        ulong[]? frameSlotValues = _commandRuntime.Synchronization._frameSlotTimelineValues;
        Silk.NET.Vulkan.Semaphore timeline =
            _commandRuntime.Synchronization._graphicsTimelineSemaphore;
        if (frameSlotValues is null || timeline.Handle == 0)
            return;

        RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
            new FrameOutputWorkTelemetry(GlobalInFlightWaits: 1));
        for (int frameSlot = 0; frameSlot < frameSlotValues.Length; frameSlot++)
            if (frameSlotValues[frameSlot] > 0)
                WaitForTimelineValue(timeline, frameSlotValues[frameSlot]);
    }

    internal void ForceFlushAllRetiredResources()
    {
        RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
            new FrameOutputWorkTelemetry(ForceFlushes: 1));
        ResourceRuntime.BeginForcedRetirementDrain();
        try
        {
            ForceFlushCompletedNonImageRetiredResources();
            for (int pass = 0; pass < FrameSlotCount; pass++)
            for (int frameSlot = 0; frameSlot < FrameSlotCount; frameSlot++)
                ResourceRuntime.DrainRetiredImages(
                    Api,
                    _deviceContext.Device,
                    frameSlot,
                    int.MaxValue);
        }
        finally
        {
            ResourceRuntime.EndForcedRetirementDrain();
        }

        ResourceRuntime.LogLifetimeDiagnostics(
            IsDeviceLost ? "device-loss-force-destroy" : "force-flush-completed");
    }

    internal void ForceFlushAllRetiredResourcesAfterWaiting(string reason)
    {
        if (IsDeviceLost)
            return;
        WaitForAllInFlightWork();
        ForceFlushAllRetiredResources();
    }

    internal void ForceFlushCompletedNonImageRetiredResources()
    {
        for (int frameSlot = 0; frameSlot < FrameSlotCount; frameSlot++)
        {
            DrainRetiredCommandBuffers(frameSlot, int.MaxValue);
            DrainRetiredCommandPools(frameSlot, int.MaxValue);
            DrainRetiredDescriptorSets(frameSlot, int.MaxValue);
            DrainRetiredDescriptorPools(frameSlot, int.MaxValue);
            DrainRetiredPipelines(frameSlot, int.MaxValue);
            ResourceRuntime.DrainRetiredPipelineLayouts(
                Api, _deviceContext.Device, frameSlot, int.MaxValue);
            ResourceRuntime.DrainRetiredDescriptorSetLayouts(
                Api, _deviceContext.Device, frameSlot, int.MaxValue);
            DrainRetiredQueryPools(frameSlot, int.MaxValue);
            DrainRetiredBufferViews(frameSlot, int.MaxValue);
            DrainRetiredFramebuffers(frameSlot, int.MaxValue);
            DrainRetiredBuffers(frameSlot, int.MaxValue);
        }
    }

    private static int GetRetiredResourceDrainCount(int queuedCount, int maxItems)
        => queuedCount <= 0 || maxItems <= 0
            ? 0
            : Math.Min(queuedCount, maxItems);

    private void ReportRetiredResourceBacklog(
        string resourceKind,
        int frameSlot,
        int remaining)
        => ResourceRuntime.ReportRetiredResourceBacklog(
            resourceKind,
            frameSlot,
            remaining);
}

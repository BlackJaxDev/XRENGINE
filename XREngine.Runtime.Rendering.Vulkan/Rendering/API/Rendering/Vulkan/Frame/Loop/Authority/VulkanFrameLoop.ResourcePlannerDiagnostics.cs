using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Describes every retained resource-planner state: the published switching table and each
    /// OpenXR planner state with its nested switching table. Every state owns one allocator holding a
    /// complete physical image set, so the listing attributes render-target memory to the planner keys
    /// that keep it alive. The switching tables are mutated by frame recording, so this must run on
    /// the render thread between frames.
    /// </summary>
    internal object CaptureResourcePlannerStateDiagnostics()
    {
        Dictionary<long, long> allocatorImageBytes = [];
        List<object> published;
        lock (_framePlanner.PlannerReadbackGate)
        {
            FrameOpResourcePlannerSwitchingState switchingState =
                _framePlanner.GetPublishedResourcePlannerGeneration().State.FrameOpResourcePlannerSwitchingState ??
                _framePlanner.MutableState.DefaultSwitchingState;
            published = DescribeSwitchingStates(switchingState, allocatorImageBytes);
        }

        List<object> openXr = [];
        lock (OutputRuntime.OpenXrBackend.ResourcePlannerStatesLock)
        {
            foreach ((VulkanOpenXrViewResourcePlannerContextKey key, ResourcePlannerRuntimeState state) in
                     OpenXrResourcePlannerStates)
            {
                openXr.Add(new
                {
                    key = DescribeOpenXrResourcePlannerContextKey(key),
                    state = DescribePlannerState(state, allocatorImageBytes),
                    nested = state.FrameOpResourcePlannerSwitchingState is { } switchingState
                        ? DescribeSwitchingStates(switchingState, allocatorImageBytes)
                        : [],
                });
            }
        }

        long totalBytes = 0L;
        foreach (long bytes in allocatorImageBytes.Values)
            totalBytes += bytes;

        return new
        {
            liveAllocatorCount = allocatorImageBytes.Count,
            liveAllocatorImageMiB = totalBytes / (1024.0 * 1024.0),
            published,
            openXr,
        };
    }

    private List<object> DescribeSwitchingStates(
        FrameOpResourcePlannerSwitchingState switchingState,
        Dictionary<long, long> allocatorImageBytes)
    {
        List<object> states = new(switchingState.States.Count);
        foreach ((VulkanFrameOpPlannerStateKey key, ResourcePlannerRuntimeState state) in switchingState.States)
        {
            states.Add(new
            {
                key.ContextKind,
                key.PipelineIdentity,
                key.ViewportIdentity,
                internalExtent = $"{key.InternalWidth}x{key.InternalHeight}",
                key.LogicalViewId,
                key.ResourceGeneration,
                key.DescriptorGeneration,
                key.ResourceRegistrySignature,
                key.ResourceRegistryInstanceRevision,
                key.PassMetadataSignature,
                lastUsedSerial = switchingState.LastUsedSerials.TryGetValue(key, out ulong serial) ? serial : 0UL,
                active = switchingState.ActiveKeys.Contains(key),
                state = DescribePlannerState(state, allocatorImageBytes),
            });
        }

        return states;
    }

    private object DescribePlannerState(
        in ResourcePlannerRuntimeState state,
        Dictionary<long, long> allocatorImageBytes)
    {
        VulkanResourceAllocator? allocator = state.ResourceAllocator;
        if (allocator is null)
            return new { allocatorId = 0L };

        int physicalImages = 0;
        long imageBytes = 0L;
        foreach (VulkanPhysicalImageGroup group in allocator.EnumeratePhysicalGroups())
        {
            if (!group.IsAllocated || group.IsBorrowedExternal)
                continue;

            physicalImages++;
            if (_resourceRuntime.Allocations.Images.DebugInfo.TryGetValue(
                    group.Image.Handle,
                    out VulkanImageAllocationDebugInfo info))
            {
                imageBytes += info.SizeBytes;
            }
        }

        if (!allocator.IsRetired)
            allocatorImageBytes[allocator.OwnershipId] = imageBytes;

        return new
        {
            allocatorId = allocator.OwnershipId,
            retired = allocator.IsRetired,
            ownsAllocator = state.HasLiveAllocatorOwnership,
            logicalTextures = allocator.LogicalTextureAllocations.Count,
            physicalImages,
            imageMiB = imageBytes / (1024.0 * 1024.0),
        };
    }
}

using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Coordinates prepared planner generations across backend wrappers, resource
/// lifetime state, and planner publication without retaining the renderer facade.
/// </summary>
internal sealed class VulkanResourceGenerationTransactionService
{
    private const int MaxPlannerStates = 12;
    private readonly VulkanDeviceContext _device;
    private readonly VulkanFramePlanner _planner;
    private readonly VulkanResourceRuntime _resources;
    private readonly VulkanResourcePlannerSessionService _sessions;

    internal VulkanResourceGenerationTransactionService(
        VulkanDeviceContext device,
        VulkanFramePlanner planner,
        VulkanResourceRuntime resources,
        VulkanResourcePlannerSessionService sessions)
    {
        _device = device;
        _planner = planner;
        _resources = resources;
        _sessions = sessions;
    }

    internal IRenderResourceGenerationTransaction Create(
        VulkanBackendObjectContext backendContext,
        in ResourcePlannerRuntimeState previousState,
        in ResourcePlannerRuntimeState pendingState,
        in VulkanFrameOpPlannerStateKey pendingKey,
        VulkanPreparedResourceGenerationManifest preparedManifest)
        => new Transaction(
            this,
            backendContext,
            previousState,
            pendingState,
            pendingKey,
            preparedManifest);

    private bool TryValidateManifest(
        VulkanBackendObjectContext backendContext,
        VulkanPreparedResourceGenerationManifest manifest,
        out string? failureReason)
    {
        if (manifest.Registry.DescriptorSignature != manifest.DescriptorSignature)
        {
            failureReason = "The pending Vulkan resource registry descriptor payload changed before commit.";
            return false;
        }

        for (int index = 0; index < manifest.ImageCount; index++)
        {
            VulkanPreparedResourceGenerationManifest.ImageEntry entry = manifest.GetImage(index);
            if (!ReferenceEquals(backendContext.Resources.BackendObjects.Get(entry.Texture), entry.Source) ||
                !entry.Source.TryGetDescriptorSnapshot(
                    requestedViewType: null,
                    requestedAspectMask: null,
                    "pending Vulkan resource generation commit",
                    allowSynchronousUpload: false,
                    out VkImageDescriptorSnapshot current) ||
                current != entry.Snapshot ||
                _resources.GetPublishedGeneration(Silk.NET.Vulkan.ObjectType.Image, current.Image.Handle) != entry.ImageGeneration ||
                _resources.GetPublishedGeneration(Silk.NET.Vulkan.ObjectType.ImageView, current.View.Handle) != entry.ViewGeneration ||
                _resources.GetPublishedGeneration(Silk.NET.Vulkan.ObjectType.Sampler, current.Sampler.Handle) != entry.SamplerGeneration)
            {
                failureReason = $"Vulkan image-view/descriptor payload for '{entry.Name}' changed before generation commit.";
                return false;
            }
        }

        for (int index = 0; index < manifest.FrameBufferCount; index++)
        {
            VulkanPreparedResourceGenerationManifest.FrameBufferEntry entry = manifest.GetFrameBuffer(index);
            if (!ReferenceEquals(backendContext.Resources.BackendObjects.Get(entry.FrameBuffer), entry.Wrapper) ||
                !entry.Wrapper.TryCaptureRecordedRenderTargetSnapshot(out VulkanRecordedRenderTargetSnapshot current) ||
                current != entry.Snapshot)
            {
                failureReason = $"Vulkan framebuffer/dynamic-attachment payload for '{entry.Name}' changed before generation commit.";
                return false;
            }
        }

        for (int index = 0; index < manifest.BufferCount; index++)
        {
            VulkanPreparedResourceGenerationManifest.BufferEntry entry = manifest.GetBuffer(index);
            if (_resources.GetPublishedGeneration(Silk.NET.Vulkan.ObjectType.Buffer, entry.Buffer.Handle) != entry.Generation)
            {
                failureReason = $"Vulkan buffer 0x{entry.Buffer.Handle:X} changed before generation commit.";
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    private FrameOpResourcePlannerSwitchingState Publish(
        VulkanBackendObjectContext backendContext,
        ref ResourcePlannerRuntimeState state,
        in VulkanFrameOpPlannerStateKey key,
        VulkanPreparedResourceGenerationManifest manifest)
    {
        if (!_device.IsOperational)
        {
            throw new InvalidOperationException(
                $"Cannot publish Vulkan resource-planner generation while device state is {_device.State}.");
        }

        if (!_planner.TryFreezeResourcePlannerRenderGraphPlan(
                ref state,
                backendContext,
                backendContext.Resources.AllowSynchronousResourceUploads,
                out string freezeFailureReason))
        {
            throw new InvalidOperationException(
                $"Vulkan prepared resource generation cannot publish: {freezeFailureReason}");
        }

        List<VulkanResourceAllocator> retiredAllocators = [];
        FrameOpResourcePlannerSwitchingState switchingState;
        lock (_planner.PlannerReadbackGate)
        {
            ResourcePlannerRuntimeState publishedState =
                _planner.GetPublishedResourcePlannerGeneration().State;
            switchingState = CloneSwitchingState(
                publishedState.FrameOpResourcePlannerSwitchingState ??
                _planner.MutableState.DefaultSwitchingState);
            state.FrameOpResourcePlannerSwitchingState = switchingState;
            state.PreparedGenerationManifest = manifest;
            switchingState.States[key] = state;
            VulkanResourcePlannerSessionService.MarkStateUsed(switchingState, key);
            CollectEvictedAllocators(switchingState, retiredAllocators);

            state.ResourceAllocator.CommitReusedPhysicalImageMetadata();
            _planner.PublishResourcePlannerGeneration(
                new ResourcePlannerRuntimeGeneration(state));
            _planner.PublishPlan(state.RenderGraphPlan);
        }

        for (int index = 0; index < retiredAllocators.Count; index++)
            _ = retiredAllocators[index].TryRetirePhysicalResources(backendContext);
        return switchingState;
    }

    private void RestoreFramebufferWrappers(
        VulkanPreparedResourceGenerationManifest manifest,
        in ResourcePlannerRuntimeState state)
    {
        ResourcePlannerRuntimeState previous =
            _sessions.CaptureRuntimeState();
        try
        {
            _sessions.RestoreRuntimeState(state);
            for (int index = 0; index < manifest.FrameBufferCount; index++)
                manifest.GetFrameBuffer(index).Wrapper.EnsureCurrent();
        }
        finally
        {
            _sessions.RestoreRuntimeState(previous);
        }
    }

    private static FrameOpResourcePlannerSwitchingState CloneSwitchingState(
        FrameOpResourcePlannerSwitchingState source)
    {
        FrameOpResourcePlannerSwitchingState clone = new()
        {
            UsageSerial = source.UsageSerial,
            SwitchingActive = source.SwitchingActive,
            MergedPlanActive = source.MergedPlanActive,
            RecordingScopeActive = source.RecordingScopeActive,
            HasActiveKey = source.HasActiveKey,
            ActiveKey = source.ActiveKey,
            HasActiveContext = source.HasActiveContext,
            ActiveContext = source.ActiveContext,
            PreparationState = source.PreparationState,
            HasPreparationState = source.HasPreparationState,
            PreparedFrameOpsSignature = source.PreparedFrameOpsSignature,
            PreparedPlanRevision = source.PreparedPlanRevision,
            HasPreparedPlan = source.HasPreparedPlan,
        };
        foreach ((VulkanFrameOpPlannerStateKey key, ResourcePlannerRuntimeState state) in source.States)
            clone.States[key] = state;
        foreach ((VulkanFrameOpPlannerStateKey key, ulong serial) in source.LastUsedSerials)
            clone.LastUsedSerials[key] = serial;
        foreach (VulkanFrameOpPlannerStateKey key in source.ActiveKeys)
            clone.ActiveKeys.Add(key);
        return clone;
    }

    private static void CollectEvictedAllocators(
        FrameOpResourcePlannerSwitchingState switchingState,
        List<VulkanResourceAllocator> retiredAllocators)
    {
        while (switchingState.States.Count > MaxPlannerStates)
        {
            VulkanFrameOpPlannerStateKey oldestKey = default;
            ulong oldestSerial = ulong.MaxValue;
            bool found = false;
            foreach (VulkanFrameOpPlannerStateKey candidate in switchingState.States.Keys)
            {
                if (switchingState.ActiveKeys.Contains(candidate))
                    continue;

                ulong serial = switchingState.LastUsedSerials.TryGetValue(candidate, out ulong value)
                    ? value
                    : 0UL;
                if (found && serial >= oldestSerial)
                    continue;
                found = true;
                oldestKey = candidate;
                oldestSerial = serial;
            }

            if (!found || !switchingState.States.Remove(oldestKey, out ResourcePlannerRuntimeState removed))
                break;

            switchingState.LastUsedSerials.Remove(oldestKey);
            if (!IsAllocatorOwned(switchingState, removed.ResourceAllocator))
                retiredAllocators.Add(removed.ResourceAllocator);
        }
    }

    private static bool IsAllocatorOwned(
        FrameOpResourcePlannerSwitchingState switchingState,
        VulkanResourceAllocator allocator)
    {
        foreach (ResourcePlannerRuntimeState state in switchingState.States.Values)
        {
            if (ReferenceEquals(state.ResourceAllocator, allocator))
                return true;
        }

        return switchingState.HasPreparationState &&
            ReferenceEquals(switchingState.PreparationState.ResourceAllocator, allocator);
    }

    private sealed class Transaction(
        VulkanResourceGenerationTransactionService owner,
        VulkanBackendObjectContext backendContext,
        ResourcePlannerRuntimeState previousState,
        ResourcePlannerRuntimeState pendingState,
        VulkanFrameOpPlannerStateKey pendingKey,
        VulkanPreparedResourceGenerationManifest manifest) : IRenderResourceGenerationTransaction
    {
        private bool _committed;

        public void Commit()
        {
            if (_committed)
                return;

            HashSet<VulkanPhysicalImageGroup>? reusedImageGroups =
                pendingState.ResourceAllocator.CapturePendingReusedImageGroups();
            ResourcePlannerRuntimeState validationPrevious =
                owner._sessions.CaptureRuntimeState();
            try
            {
                owner._sessions.RestoreRuntimeState(pendingState);
                if (!owner.TryValidateManifest(backendContext, manifest, out string? failureReason))
                    throw new InvalidOperationException(failureReason);
            }
            finally
            {
                owner._sessions.RestoreRuntimeState(validationPrevious);
            }

            FrameOpResourcePlannerSwitchingState switchingState =
                owner.Publish(backendContext, ref pendingState, pendingKey, manifest);
            _committed = true;
            try
            {
                if (!ReferenceEquals(previousState.ResourceAllocator, pendingState.ResourceAllocator) &&
                    !IsAllocatorOwned(switchingState, previousState.ResourceAllocator))
                {
                    _ = previousState.ResourceAllocator.TryRetirePhysicalResources(
                        backendContext,
                        exceptImageGroups: reusedImageGroups);
                }
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning(
                    "[VulkanResourcePlanner] Generation {0} published, but post-commit retirement failed: {1}",
                    pendingKey.ResourceGeneration,
                    ex.Message);
            }
        }

        public void Dispose()
        {
            if (_committed || pendingState.ResourceAllocator.IsRetired)
                return;

            owner.RestoreFramebufferWrappers(manifest, previousState);
            _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(
                backendContext,
                exceptImageGroups: pendingState.ResourceAllocator.CapturePendingReusedImageGroups(),
                immediate: true);
        }
    }
}

using Silk.NET.Vulkan;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    internal bool TryEnsurePhysicalImageForTextureResource(
        string? resourceName,
        out VulkanPhysicalImageGroup? group)
        => TryEnsurePhysicalImageForTextureResource(resourceName, out group, out _);

    internal bool TryEnsurePhysicalImageForTextureResource(
        string? resourceName,
        out VulkanPhysicalImageGroup? group,
        out string? failureReason)
    {
        group = null;
        failureReason = null;
        if (string.IsNullOrWhiteSpace(resourceName))
            return false;

        if (ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group?.IsAllocated == true)
        {
            return true;
        }

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        if (context.ResourceRegistry is null ||
            !context.ResourceRegistry.TextureRecords.ContainsKey(resourceName))
        {
            group = null;
            return false;
        }

        if (_commandRuntime.Recorder.IsRecording)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.LazyRebuildDuringRecord.{resourceName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Deferring lazy physical-image plan rebuild for '{0}' during command-buffer recording.",
                resourceName);
            failureReason = "resource planner rebuild is deferred during command-buffer recording";
            group = null;
            return false;
        }

        if (IsCommandChainResourcePlanFrozen)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.LazyRebuildDuringFrozenCommandChainPlan.{resourceName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Refusing lazy physical-image plan rebuild for '{0}' while command-chain readers are using frozen plan revision {1}.",
                resourceName,
                _framePlanner.FrozenResourcePlanRevision);
            failureReason = $"resource planner rebuild is deferred while command-chain readers are using frozen plan revision {_framePlanner.FrozenResourcePlanRevision}";
            group = null;
            return false;
        }

        if (VulkanFrameDiagnosticsTraceEnabled)
        {
            ResourcePlannerRuntimeState plannerState = CaptureResourcePlannerRuntimeState();
            Debug.Vulkan(
                "[VulkanResourcePlanner] Lazy physical-image rebuild resource='{0}' registry=0x{1:X8} owner={2} revision={3} textures={4} buffers={5}.",
                resourceName,
                VulkanFramePlanner.ResolveFrameOpContextResourceRegistrySignature(context),
                plannerState.ResourceAllocator.OwnershipId,
                plannerState.ResourcePlannerRevision,
                plannerState.ResourceAllocator.LogicalTextureAllocations.Count,
                plannerState.ResourceAllocator.LogicalBufferAllocations.Count);
        }

        UpdateResourcePlannerFromContext(context);

        ResourcePlannerRuntimeState updatedPlannerState = CaptureResourcePlannerRuntimeState();
        if (updatedPlannerState.ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group is not null)
        {
            if (!group.TryEnsureAllocated(BackendObjectContext, out string allocationFailureReason))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.ResourcePlanner.LazyPhysicalImageAllocationFailed.{resourceName}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanResourcePlanner] Lazy physical-image allocation failed for '{0}': {1}",
                    resourceName,
                    allocationFailureReason);
                failureReason = allocationFailureReason;
                group = null;
                return false;
            }

            return group.IsAllocated;
        }

        group = null;
        return false;
    }

    internal void DestroyFrameOpResourcePlannerStates()
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (switchingState.States.Count == 0 && !switchingState.HasPreparationState)
            return;

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        HashSet<VulkanResourceAllocator> retiredAllocators = new(ReferenceEqualityComparer.Instance);
        foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
        {
            RetireResourcePlannerRuntimeStateAllocator(
                pair.Value,
                retiredAllocators,
                $"FrameOpResourcePlannerStateDestroy.pipe{pair.Key.PipelineIdentity}.vp{pair.Key.ViewportIdentity}");
        }

        if (switchingState.HasPreparationState)
        {
            RetireResourcePlannerRuntimeStateAllocator(
                switchingState.PreparationState,
                retiredAllocators,
                "FrameOpResourcePlannerPreparationStateDestroy");
        }

        switchingState.States.Clear();
        switchingState.LastUsedSerials.Clear();
        switchingState.ActiveKeys.Clear();
        switchingState.SwitchingActive = false;
        switchingState.RecordingScopeActive = false;
        switchingState.HasActiveKey = false;
        switchingState.HasActiveContext = false;
        switchingState.PreparationState = default;
        switchingState.HasPreparationState = false;
        if (previousState.ResourceAllocator is not null && previousState.ResourceAllocator.IsRetired)
            previousState = ResourcePlannerRuntimeState.CreateEmpty();
        RestoreResourcePlannerRuntimeState(previousState);
    }

    private Extent2D ResolveFrameOpContextFallbackExtent()
        => TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent)
            ? externalExtent
            : OutputRuntime.Desktop.Extent;

    internal bool TryResolveExternalSwapchainTargetExtent(out Extent2D extent)
    {
        if (TryGetExternalSwapchainTargetRegion(out BoundingRectangle region) &&
            region.Width > 0 &&
            region.Height > 0)
        {
            extent = new Extent2D(
                (uint)region.Width,
                (uint)region.Height);
            return true;
        }

        if (IsRenderingExternalSwapchainTarget)
            throw new InvalidOperationException("OpenXR external swapchain rendering is active, but no valid external target extent is bound.");

        extent = default;
        return false;
    }
}

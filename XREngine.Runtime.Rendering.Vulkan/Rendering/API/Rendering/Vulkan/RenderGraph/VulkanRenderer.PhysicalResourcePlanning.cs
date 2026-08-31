using System.Diagnostics;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Owns physical resource allocation, replacement, retry, and history
/// preservation for immutable planner generations.
/// </summary>
internal sealed partial class VulkanFramePlanner
{
    private static readonly TimeSpan ResourceAllocationFailureRetryDelay =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan OpenXrResourceAllocationFailureRetryDelay =
        TimeSpan.FromSeconds(10);

    private VulkanPhysicalImageGroup? _activeAutoExposureHistoryGroup;

    internal void TrackAutoExposureHistory(VulkanPhysicalImageGroup group)
    {
        if (IsUsableAutoExposureHistoryGroup(group))
            _activeAutoExposureHistoryGroup = group;
    }

    internal VulkanPhysicalPlanningResult ApplyPhysicalResourcePlan(
        ref ResourcePlannerRuntimeState state,
        in VulkanPhysicalPlanningRequest request)
    {
        VulkanResourcePlanner pendingPlanner = request.PendingPlanner;
        VulkanResourceAllocator oldAllocator = state.ResourceAllocator;
        VulkanResourceAllocator? pendingAllocator = null;
        HashSet<VulkanPhysicalImageGroup>? reusedImageGroups = null;
        int retiredImageCount = 0;
        int retiredBufferCount = 0;
        bool allocationChanged = request.AllocationSignature != state.ResourceAllocationSignature;

        if (allocationChanged)
        {
            if (TryDescribeRecentResourceAllocationFailure(
                    in state,
                    request.IsOpenXrOrVr,
                    out string recentAllocationFailureReason))
            {
                Debug.VulkanEvery(
                    $"Vulkan.ResourcePlanner.DeferRecentAllocationRetry.{request.Context.PipelineIdentity}.{request.Context.ViewportIdentity}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanResourcePlanner] Deferring physical resource plan after recent allocation failure. Planner=0x{0:X16} Allocation=0x{1:X16}. Reason={2}",
                    request.PlannerSignature,
                    request.AllocationSignature,
                    recentAllocationFailureReason);
                return VulkanPhysicalPlanningResult.Deferred;
            }

            if (!TryBuildPhysicalAllocator(
                    in state,
                    in request,
                    out pendingAllocator,
                    out reusedImageGroups,
                    out retiredImageCount,
                    out retiredBufferCount))
            {
                RecordResourceAllocationPlanFailure(
                    ref state,
                    request.PlannerSignature,
                    request.AllocationSignature);
                return VulkanPhysicalPlanningResult.Deferred;
            }

            ClearResourceAllocationPlanFailure(
                ref state,
                request.PlannerSignature,
                request.AllocationSignature);
        }

        state.ResourcePlanner = pendingPlanner;
        if (pendingAllocator is not null)
        {
            state.ResourceAllocator = pendingAllocator;
            state.AllocatorOwnershipId = pendingAllocator.OwnershipId;
            if (!request.DeferReusedImageMetadataCommit)
                pendingAllocator.CommitReusedPhysicalImageMetadata();
        }

        CommitPhysicalAllocatorPlan(
            ref state,
            in request,
            allocationChanged,
            oldAllocator,
            reusedImageGroups,
            retiredImageCount,
            retiredBufferCount);

        state.CompiledRenderGraph = request.CompiledGraph;
        BarrierPlanFastPathKey barrierKey = new(
            request.CompiledGraph,
            request.PlannerSignature,
            request.AllocationSignature,
            request.QueueOwnership);
        if (!state.HasBarrierPlanFastPathKey ||
            !barrierKey.Matches(state.BarrierPlanFastPathKey))
        {
            state.BarrierPlanner.Rebuild(
                request.ActivePassMetadata,
                state.ResourcePlanner,
                state.ResourceAllocator,
                state.CompiledRenderGraph.Synchronization,
                request.QueueOwnership);
            state.BarrierPlanFastPathKey = barrierKey;
            state.HasBarrierPlanFastPathKey = true;
        }

        state.ResourcePlannerSignature = request.PlannerSignature;
        state.ResourceAllocationSignature = request.AllocationSignature;
        state.ResourcePlannerSignatureBreakdown = request.SignatureBreakdown;
        state.ResourcePlannerRevision++;
        state.LastActiveFrameOpContext = request.Context;

        return new VulkanPhysicalPlanningResult(
            Updated: true,
            AliasReuseCount: reusedImageGroups?.Count ?? 0,
            RetiredImageCount: retiredImageCount,
            RetiredBufferCount: retiredBufferCount);
    }

    internal static bool TryDescribeRecentResourceAllocationFailure(
        in ResourcePlannerRuntimeState state,
        bool isOpenXrOrVr,
        out string reason)
    {
        reason = string.Empty;
        if (state.FailedResourceAllocationTimestamp == 0)
            return false;

        TimeSpan elapsed = Stopwatch.GetElapsedTime(state.FailedResourceAllocationTimestamp);
        TimeSpan retryDelay = isOpenXrOrVr
            ? OpenXrResourceAllocationFailureRetryDelay
            : ResourceAllocationFailureRetryDelay;
        if (elapsed >= retryDelay)
            return false;

        reason =
            $"Vulkan resource planner is backing off after a failed physical allocation ({elapsed.TotalMilliseconds:F0}/{retryDelay.TotalMilliseconds:F0} ms, planner=0x{state.FailedResourcePlannerSignature:X16}, allocation=0x{state.FailedResourceAllocationSignature:X16})";
        return true;
    }

    internal static bool IsExpectedVulkanImageAllocationDeferral(Exception exception)
        => IsExpectedVulkanImageAllocationDeferral(exception.Message);

    internal static bool IsExpectedVulkanImageAllocationDeferral(string failureReason)
        => failureReason.Contains("Vulkan image allocation deferred under", StringComparison.OrdinalIgnoreCase) ||
            failureReason.Contains("allocation deferred under allocator pressure", StringComparison.OrdinalIgnoreCase);

    internal bool TryPreserveTrackedAutoExposureHistory(
        VulkanResourceAllocator newAllocator,
        VulkanResourceRuntime resources,
        VulkanBackendObjectContext backendContext,
        in VulkanAutoExposureHistoryCommandCapability commands,
        bool skipPreservation)
    {
        if (skipPreservation ||
            !TryGetAutoExposurePhysicalGroup(newAllocator, out VulkanPhysicalImageGroup? newGroup) ||
            newGroup is null)
        {
            return false;
        }

        return commands.TryCopy(_activeAutoExposureHistoryGroup, newGroup, "tracked-active-plan");
    }

    internal VulkanResourceAllocator? ResolveAutoExposureHistoryAllocator(
        VulkanResourceAllocator preferredAllocator,
        VulkanResourceAllocator excludedAllocator,
        FrameOpResourcePlannerSwitchingState switchingState)
    {
        if (!ReferenceEquals(preferredAllocator, excludedAllocator) &&
            TryGetAutoExposurePhysicalGroup(preferredAllocator, out _))
        {
            return preferredAllocator;
        }

        foreach (VulkanFrameOpPlannerStateKey key in switchingState.ActiveKeys)
        {
            if (switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state) &&
                !ReferenceEquals(state.ResourceAllocator, excludedAllocator) &&
                TryGetAutoExposurePhysicalGroup(state.ResourceAllocator, out _))
            {
                return state.ResourceAllocator;
            }
        }

        foreach (ResourcePlannerRuntimeState state in switchingState.States.Values)
        {
            if (!ReferenceEquals(state.ResourceAllocator, excludedAllocator) &&
                TryGetAutoExposurePhysicalGroup(state.ResourceAllocator, out _))
            {
                return state.ResourceAllocator;
            }
        }

        return null;
    }

    internal VulkanPhysicalImageGroup? PreserveAutoExposureHistory(
        VulkanResourceAllocator oldAllocator,
        VulkanResourceAllocator newAllocator,
        VulkanResourceRuntime resources,
        VulkanBackendObjectContext backendContext,
        in VulkanAutoExposureHistoryCommandCapability commands,
        bool skipPreservation)
    {
        if (skipPreservation)
            return null;

        bool hasOldGroup = TryGetAutoExposurePhysicalGroup(oldAllocator, out VulkanPhysicalImageGroup? oldGroup);
        bool hasNewGroup = TryGetAutoExposurePhysicalGroup(newAllocator, out VulkanPhysicalImageGroup? newGroup);
        if (hasOldGroup && hasNewGroup && ReferenceEquals(oldGroup, newGroup))
            return null;

        if (hasNewGroup && newGroup is not null)
        {
            if (commands.TryCopy(oldGroup, newGroup, "active-plan"))
            {
                DestroyRetainedAutoExposureHistory(resources, backendContext, "superseded by active-plan copy");
                return null;
            }

            if (commands.TryCopy(resources.RetainedAutoExposureHistoryGroup, newGroup, "retained-plan-gap"))
            {
                DestroyRetainedAutoExposureHistory(resources, backendContext, "restored into active plan");
                return null;
            }

            DestroyRetainedAutoExposureHistory(resources, backendContext, "new active plan could not use retained history");
            return null;
        }

        if (hasOldGroup && IsUsableAutoExposureHistoryGroup(oldGroup))
            return RetainAutoExposureHistory(resources, backendContext, oldGroup!);

        return null;
    }

    internal static void DestroyRetainedAutoExposureHistory(
        VulkanResourceRuntime resources,
        VulkanBackendObjectContext backendContext,
        string reason)
    {
        VulkanPhysicalImageGroup? group = resources.RetainedAutoExposureHistoryGroup;
        if (group is null)
            return;

        group.Destroy(backendContext);
        resources.RetainedAutoExposureHistoryGroup = null;
        Debug.VulkanEvery(
            "Vulkan.AutoExposure.HistoryRetainedDestroy",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Destroyed retained auto exposure history ({0}).",
            reason);
    }

    private bool TryBuildPhysicalAllocator(
        in ResourcePlannerRuntimeState state,
        in VulkanPhysicalPlanningRequest request,
        out VulkanResourceAllocator? pendingAllocator,
        out HashSet<VulkanPhysicalImageGroup>? reusedImageGroups,
        out int retiredImageCount,
        out int retiredBufferCount)
    {
        pendingAllocator = new VulkanResourceAllocator();
        reusedImageGroups = null;
        retiredImageCount = 0;
        retiredBufferCount = 0;

        try
        {
            VulkanTransientAttachmentPlan transientAttachmentPlan = VulkanTransientAttachmentPlan.Build(
                request.PendingPlanner.CurrentPlan,
                request.CompiledGraph.Plan,
                request.PendingPlanner,
                request.Context,
                request.IsOpenXrOrVr);
            pendingAllocator.UpdatePlan(
                request.PendingPlanner.CurrentPlan,
                transientAttachmentPlan);
            pendingAllocator.RebuildPhysicalPlan(
                request.BackendContext,
                request.SupportsTransformFeedback,
                request.ActivePassMetadata,
                request.PendingPlanner,
                request.ExtentContext);
            int reusedImageCount = pendingAllocator.ReuseCompatiblePhysicalImagesFrom(
                state.ResourceAllocator,
                out reusedImageGroups);
            if (reusedImageCount > 0)
            {
                Debug.VulkanEvery(
                    "Vulkan.ResourcePlanner.PhysicalImageReuse",
                    TimeSpan.FromSeconds(1),
                    "[VulkanResourcePlanner] Reused {0} compatible physical image groups from active plan before allocating pending plan.",
                    reusedImageCount);
            }

            if (!pendingAllocator.TryAllocatePhysicalImages(request.BackendContext, out string failureReason))
            {
                if (IsExpectedVulkanImageAllocationDeferral(failureReason))
                {
                    Debug.VulkanEvery(
                        "Vulkan.ResourcePlanner.PhysicalImageAllocationDeferred",
                        TimeSpan.FromSeconds(1),
                        "[VulkanResourcePlanner] Deferred pending physical image allocation. Keeping active plan revision={0}. Reason={1}",
                        state.ResourcePlannerRevision,
                        failureReason);
                }
                else
                {
                    Debug.VulkanWarning(
                        "[VulkanResourcePlanner] Pending physical image allocation failed. Keeping active plan revision={0}. Reason={1}",
                        state.ResourcePlannerRevision,
                        failureReason);
                }

                pendingAllocator.DestroyPhysicalImagesImmediate(request.BackendContext, reusedImageGroups);
                pendingAllocator.DestroyPhysicalBuffersImmediate(request.BackendContext);
                pendingAllocator = null;
                return false;
            }

            pendingAllocator.AllocatePhysicalBuffers(request.BackendContext);
        }
        catch (Exception exception)
        {
            pendingAllocator?.DestroyPhysicalImagesImmediate(request.BackendContext, reusedImageGroups);
            pendingAllocator?.DestroyPhysicalBuffersImmediate(request.BackendContext);
            pendingAllocator = null;
            Debug.VulkanWarning(
                "[VulkanResourcePlanner] Pending physical resource plan failed. Keeping active plan revision={0}. Reason={1}",
                state.ResourcePlannerRevision,
                exception.Message);
            return false;
        }

        HashSet<VulkanPhysicalImageGroup>? reusedGroups = reusedImageGroups;
        retiredImageCount = state.ResourceAllocator
            .EnumeratePhysicalGroups()
            .Count(group => group.IsAllocated && (reusedGroups is null || !reusedGroups.Contains(group)));
        retiredBufferCount = state.ResourceAllocator
            .EnumeratePhysicalBufferGroups()
            .Count(static group => group.IsAllocated);
        return true;
    }

    private void CommitPhysicalAllocatorPlan(
        ref ResourcePlannerRuntimeState state,
        in VulkanPhysicalPlanningRequest request,
        bool physicalPlanChanged,
        VulkanResourceAllocator oldAllocator,
        HashSet<VulkanPhysicalImageGroup>? reusedImageGroups,
        int retiredImageCount,
        int retiredBufferCount)
    {
        if (!physicalPlanChanged)
            return;

        if (retiredImageCount > 0 || retiredBufferCount > 0)
        {
            LastResourcePlanReplacementRevision = state.ResourcePlannerRevision + 1;
            LastResourcePlanReplacementSignature = request.PlannerSignature;
            LastResourcePlanReplacementAllocationSignature = request.AllocationSignature;
            LastResourcePlanReplacementRetiredImageCount = retiredImageCount;
            LastResourcePlanReplacementRetiredBufferCount = retiredBufferCount;
            if (!request.IsDeviceLost)
            {
                Debug.VulkanEvery(
                    "Vulkan.ResourcePlanner.PlanReplacementDeferredRetirement",
                    TimeSpan.FromSeconds(2),
                    "[VulkanResourcePlanner] Deferring replaced physical resource plan retirement through frame-slot/timeline completion. revision={0} oldPlan=0x{1:X16} newPlan=0x{2:X16} oldAllocation=0x{3:X16} newAllocation=0x{4:X16} images={5} buffers={6}",
                    state.ResourcePlannerRevision + 1,
                    state.ResourcePlannerSignature,
                    request.PlannerSignature,
                    state.ResourceAllocationSignature,
                    request.AllocationSignature,
                    retiredImageCount,
                    retiredBufferCount);
            }
        }

        if (request.IsDeviceLost)
            return;

        VulkanPhysicalImageGroup? retainedAutoExposureGroup = PreserveAutoExposureHistory(
            oldAllocator,
            state.ResourceAllocator,
            request.Resources,
            request.BackendContext,
            request.Commands,
            request.IsOpenXrOrVr);
        EvictFrameOpResourcePlannerStatesReferencingAllocator(
            request.SwitchingState,
            oldAllocator);
        _ = oldAllocator.TryRetirePhysicalResources(
            request.BackendContext,
            retainedAutoExposureGroup,
            reusedImageGroups);
    }

    private void EvictFrameOpResourcePlannerStatesReferencingAllocator(
        FrameOpResourcePlannerSwitchingState switchingState,
        VulkanResourceAllocator allocator)
    {
        List<VulkanFrameOpPlannerStateKey> staleKeys = MutableState.PlannerStateEvictionScratch;
        staleKeys.Clear();
        foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
            if (ReferenceEquals(pair.Value.ResourceAllocator, allocator))
                staleKeys.Add(pair.Key);

        for (int index = 0; index < staleKeys.Count; index++)
        {
            VulkanFrameOpPlannerStateKey key = staleKeys[index];
            switchingState.States.Remove(key);
            switchingState.LastUsedSerials.Remove(key);
            switchingState.ActiveKeys.Remove(key);
            if (switchingState.HasActiveKey && switchingState.ActiveKey.Equals(key))
            {
                switchingState.HasActiveKey = false;
                switchingState.HasActiveContext = false;
                switchingState.ActiveKey = default;
            }
        }

        bool preparationReferencedAllocator = switchingState.HasPreparationState &&
            ReferenceEquals(switchingState.PreparationState.ResourceAllocator, allocator);
        if (preparationReferencedAllocator)
        {
            switchingState.PreparationState = default;
            switchingState.HasPreparationState = false;
        }

        switchingState.SwitchingActive = switchingState.ActiveKeys.Count > 1;
        staleKeys.Clear();
    }

    private VulkanPhysicalImageGroup RetainAutoExposureHistory(
        VulkanResourceRuntime resources,
        VulkanBackendObjectContext backendContext,
        VulkanPhysicalImageGroup oldGroup)
    {
        if (!ReferenceEquals(resources.RetainedAutoExposureHistoryGroup, oldGroup))
            DestroyRetainedAutoExposureHistory(resources, backendContext, "replaced by newer active history");

        resources.RetainedAutoExposureHistoryGroup = oldGroup;
        Debug.VulkanEvery(
            "Vulkan.AutoExposure.HistoryRetain",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Retained auto exposure history while switching to a planner context without AutoExposureTex: image=0x{0:X} layout={1}.",
            oldGroup.Image.Handle,
            oldGroup.LastKnownLayout);
        return oldGroup;
    }

    private static bool TryGetAutoExposurePhysicalGroup(
        VulkanResourceAllocator allocator,
        out VulkanPhysicalImageGroup? group)
        => allocator.TryGetPhysicalGroupForResource(
               DefaultRenderPipeline.AutoExposureTextureName,
               out group) &&
           group is not null;

    internal static bool IsUsableAutoExposureHistoryGroup(VulkanPhysicalImageGroup? group)
        => group is not null &&
           group.IsAllocated &&
           group.Image.Handle != 0 &&
           group.LastKnownLayout != Silk.NET.Vulkan.ImageLayout.Undefined;

    private static void RecordResourceAllocationPlanFailure(
        ref ResourcePlannerRuntimeState state,
        ulong plannerSignature,
        ulong allocationSignature)
    {
        state.FailedResourcePlannerSignature = plannerSignature;
        state.FailedResourceAllocationSignature = allocationSignature;
        state.FailedResourceAllocationTimestamp = Stopwatch.GetTimestamp();
    }

    private static void ClearResourceAllocationPlanFailure(
        ref ResourcePlannerRuntimeState state,
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (state.FailedResourcePlannerSignature != plannerSignature ||
            state.FailedResourceAllocationSignature != allocationSignature)
        {
            return;
        }

        state.FailedResourcePlannerSignature = ulong.MaxValue;
        state.FailedResourceAllocationSignature = ulong.MaxValue;
        state.FailedResourceAllocationTimestamp = 0;
    }
}

using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanFrameLoop
{
    private FrameOpContext PrepareResourcePlannerForFrameOps(
        FrameOp[] operations,
        ulong frameOperationsSignature = 0)
    {
        if (operations.Length == 0)
        {
            ResourcePlannerRuntimeState state = _resourcePlannerSessions.CaptureRuntimeState();
            return state.LastActiveFrameOpContext ?? default;
        }

        FrameOpContext context = VulkanFramePlanner.SelectPrimaryPlannerContext(operations);
        UpdateResourcePlannerFromContext(context);
        return context;
    }

    internal void UpdateResourcePlannerFromContext(
        in FrameOpContext context,
        HashSet<int>? activePassIndices = null,
        HashSet<string>? activeFrameBufferNames = null,
        int activeResourceSetSignature = 0,
        bool constrainToActivePassSet = false,
        bool deferReusedImageMetadataCommit = false)
    {
        if (!HasThreadResourcePlannerRuntimeState)
        {
            ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
            ResourcePlannerRuntimeState pendingState;
            using (VulkanResourcePlannerSessionService.RuntimeStateScope scope =
                   _resourcePlannerSessions.EnterRuntimeStateScope(in previousState))
            {
                UpdateResourcePlannerFromContextCore(
                    context,
                    activePassIndices,
                    activeFrameBufferNames,
                    activeResourceSetSignature,
                    constrainToActivePassSet,
                    deferReusedImageMetadataCommit: true);
                pendingState = scope.CaptureCurrent(
                    CaptureResourcePlannerRuntimeState(),
                    ActiveFrameOpResourcePlannerSwitchingState);
            }

            if (ReferenceEquals(pendingState.ResourcePlanner, previousState.ResourcePlanner) &&
                ReferenceEquals(pendingState.ResourceAllocator, previousState.ResourceAllocator) &&
                ReferenceEquals(pendingState.BarrierPlanner, previousState.BarrierPlanner) &&
                ReferenceEquals(pendingState.CompiledRenderGraph, previousState.CompiledRenderGraph) &&
                pendingState.ResourcePlannerSignature == previousState.ResourcePlannerSignature &&
                pendingState.ResourceAllocationSignature == previousState.ResourceAllocationSignature &&
                pendingState.ResourcePlannerRevision == previousState.ResourcePlannerRevision &&
                pendingState.FailedResourcePlannerSignature == previousState.FailedResourcePlannerSignature &&
                pendingState.FailedResourceAllocationSignature == previousState.FailedResourceAllocationSignature &&
                pendingState.FailedResourceAllocationTimestamp == previousState.FailedResourceAllocationTimestamp &&
                pendingState.HasResourcePlannerFastPathKey == previousState.HasResourcePlannerFastPathKey &&
                pendingState.HasBarrierPlanFastPathKey == previousState.HasBarrierPlanFastPathKey)
            {
                return;
            }

            if (!_framePlanner.TryFreezeResourcePlannerRenderGraphPlan(
                    ref pendingState,
                    BackendObjectContext,
                    AllowSynchronousResourceUploads,
                    out string freezeFailureReason))
            {
                throw new InvalidOperationException(
                    $"Vulkan resource-plan publication failed: {freezeFailureReason}");
            }

            PublishResourcePlannerRuntimeState(pendingState, commitReusedImageMetadata: true);
            _framePlanner.PublishPlan(pendingState.RenderGraphPlan);
            return;
        }

        UpdateResourcePlannerFromContextCore(
            context,
            activePassIndices,
            activeFrameBufferNames,
            activeResourceSetSignature,
            constrainToActivePassSet,
            deferReusedImageMetadataCommit);
    }

    private void UpdateResourcePlannerFromContextCore(
        in FrameOpContext context,
        HashSet<int>? activePassIndices,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature,
        bool constrainToActivePassSet,
        bool deferReusedImageMetadataCommit)
    {
        if (!_deviceContext.IsOperational)
            return;

        if (IsCommandChainResourcePlanFrozen)
            throw new InvalidOperationException(
                $"Resource planner cannot be replaced while command-chain readers are using frozen plan revision {_framePlanner.FrozenResourcePlanRevision}.");

        int activePassSetSignature = VulkanFramePlanner.ComputeActivePassSetSignature(activePassIndices);
        ResourcePlanningInputs planningInputs = PrepareResourcePlanningInputs(
            context,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature,
            constrainToActivePassSet);

        if (CanReuseResourcePlannerFastPath(planningInputs.FastPathKey))
        {
            RecordPhysicalPlanCacheTelemetry(hit: true, planningInputs.CompiledGraph.Plan.Generation);
            return;
        }

        ulong plannerSignature = VulkanFramePlanner.ComputeResourcePlannerSignature(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);
        if (plannerSignature == ActiveResourcePlannerSignature)
        {
            RecordPhysicalPlanCacheTelemetry(hit: true, planningInputs.CompiledGraph.Plan.Generation);
            RememberResourcePlannerFastPath(planningInputs.FastPathKey);
            return;
        }

        RecordPhysicalPlanCacheTelemetry(hit: false, planningInputs.CompiledGraph.Plan.Generation);
        ResourcePlannerSignatureBreakdown signatureBreakdown = VulkanFramePlanner.ComputeResourcePlannerSignatureBreakdown(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);

        VulkanResourcePlanner pendingPlanner = new();
        pendingPlanner.Sync(context.ResourceRegistry, context.OutputFrameBufferName);
        VulkanFramePlanner.ValidateVulkanResourcePlanMetadata(planningInputs.ActivePassMetadata, pendingPlanner);
        VulkanResourceExtentContext extentContext = BuildResourceExtentContext(context);
        ulong allocationSignature = VulkanFramePlanner.ComputeResourceAllocationSignature(
            context,
            pendingPlanner,
            planningInputs.ActivePassMetadata,
            extentContext,
            SupportsTransformFeedback);

        ResourcePlannerRuntimeState state = CaptureResourcePlannerRuntimeState();
        VulkanPhysicalPlanningRequest request = new(
            context,
            planningInputs.ActivePassMetadata,
            planningInputs.CompiledGraph,
            planningInputs.QueueOwnership,
            pendingPlanner,
            extentContext,
            plannerSignature,
            allocationSignature,
            signatureBreakdown,
            BackendObjectContext,
            ResourceRuntime,
            ActiveFrameOpResourcePlannerSwitchingState,
            new VulkanAutoExposureHistoryCommandCapability(_commandRuntime),
            SupportsTransformFeedback,
            IsDeviceLost,
            RuntimeRenderingHostServices.Presentation.IsOpenXRActive || RuntimeRenderingHostServices.Presentation.IsInVR,
            deferReusedImageMetadataCommit);
        VulkanPhysicalPlanningResult result = _framePlanner.ApplyPhysicalResourcePlan(ref state, in request);
        if (!result.Updated)
            return;

        state.ResourcePlannerFastPathKey = planningInputs.FastPathKey;
        state.HasResourcePlannerFastPathKey = true;
        if (!_framePlanner.TryFreezeResourcePlannerRenderGraphPlan(
                ref state,
                BackendObjectContext,
                AllowSynchronousResourceUploads,
                out string freezeFailureReason))
        {
            throw new InvalidOperationException(
                $"Vulkan resource-plan publication failed: {freezeFailureReason}");
        }

        if (HasThreadResourcePlannerRuntimeState)
            StoreThreadResourcePlannerRuntimeState(in state);
        else
        {
            PublishResourcePlannerRuntimeState(state, commitReusedImageMetadata: false);
            _framePlanner.PublishPlan(state.RenderGraphPlan);
        }

        RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
            new FrameOutputWorkTelemetry(
                PhysicalPlanGenerations: 1,
                PhysicalPlanAliasReuses: result.AliasReuseCount,
                PlannerArenaHighWater: ActiveFrameOpResourcePlannerSwitchingState.States.Count,
                RenderGraphPlanGeneration: ClampGenerationToInt64(planningInputs.CompiledGraph.Plan.Generation)));
    }

    private ResourcePlanningInputs PrepareResourcePlanningInputs(
        in FrameOpContext context,
        HashSet<int>? activePassIndices,
        int activePassSetSignature,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature,
        bool constrainToActivePassSet)
    {
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata = _framePlanner.FilterActivePassMetadata(
            context.PassMetadata,
            context.ResourceRegistry,
            context.ResourceRegistry?.DescriptorRevision ?? 0,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature,
            constrainToActivePassSet);
        VulkanCompiledRenderGraph compiledGraph = _framePlanner.Compiler.Compile(activePassMetadata);
        VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership = _framePlanner.BuildQueueOwnershipConfig(
            _deviceContext,
            activePassMetadata,
            VulkanFeatureProfile.ActiveProfile);
        ResourcePlannerFastPathKey fastPathKey = new(
            context.ResourceRegistry,
            context.ResourceRegistry?.DescriptorRevision ?? 0,
            activePassMetadata,
            VulkanFramePlanner.ComputePassMetadataRevisionStamp(activePassMetadata),
            activePassSetSignature,
            activeResourceSetSignature,
            VulkanFramePlanner.ComputeOutputFrameBufferIdentity(context.OutputFrameBufferName),
            VulkanFramePlanner.ResolveResourcePlanOutputTargetIdentity(context),
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            queueOwnership,
            SupportsTransformFeedback);
        return new ResourcePlanningInputs(activePassMetadata, compiledGraph, queueOwnership, fastPathKey);
    }

    private bool CanReuseResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
        => ActiveHasResourcePlannerFastPathKey &&
           ActiveResourcePlannerSignature != ulong.MaxValue &&
           key.Matches(ActiveResourcePlannerFastPathKey);

    private void RememberResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
    {
        ActiveResourcePlannerFastPathKey = key;
        ActiveHasResourcePlannerFastPathKey = true;
    }

    internal void RecordPhysicalPlanCacheTelemetry(bool hit, ulong renderGraphPlanGeneration)
        => RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
            new FrameOutputWorkTelemetry(
                PhysicalPlanCacheHits: hit ? 1 : 0,
                PhysicalPlanCacheMisses: hit ? 0 : 1,
                PlannerArenaHighWater: ActiveFrameOpResourcePlannerSwitchingState.States.Count,
                RenderGraphPlanGeneration: ClampGenerationToInt64(renderGraphPlanGeneration)));

    private static long ClampGenerationToInt64(ulong generation)
        => generation > long.MaxValue ? long.MaxValue : (long)generation;

    internal static bool IsExpectedVulkanImageAllocationDeferral(Exception exception)
        => VulkanFramePlanner.IsExpectedVulkanImageAllocationDeferral(exception);

    internal bool TryPreserveTrackedAutoExposureHistory(VulkanResourceAllocator newAllocator)
        => _framePlanner.TryPreserveTrackedAutoExposureHistory(
            newAllocator,
            ResourceRuntime,
            BackendObjectContext,
            new VulkanAutoExposureHistoryCommandCapability(_commandRuntime),
            IsDeviceLost || RuntimeRenderingHostServices.Presentation.IsInVR);

    internal VulkanResourceAllocator? ResolveAutoExposureHistoryAllocator(
        VulkanResourceAllocator preferredAllocator,
        VulkanResourceAllocator excludedAllocator)
        => _framePlanner.ResolveAutoExposureHistoryAllocator(
            preferredAllocator,
            excludedAllocator,
            ActiveFrameOpResourcePlannerSwitchingState);

    internal VulkanPhysicalImageGroup? PreserveAutoExposureHistory(
        VulkanResourceAllocator oldAllocator,
        VulkanResourceAllocator? newAllocator = null)
        => _framePlanner.PreserveAutoExposureHistory(
            oldAllocator,
            newAllocator ?? ResourceAllocator,
            ResourceRuntime,
            BackendObjectContext,
            new VulkanAutoExposureHistoryCommandCapability(_commandRuntime),
            IsDeviceLost || RuntimeRenderingHostServices.Presentation.IsInVR);

    internal void DestroyRetainedAutoExposureHistory(string reason)
        => VulkanFramePlanner.DestroyRetainedAutoExposureHistory(
            ResourceRuntime,
            BackendObjectContext,
            reason);

}

using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanFrameLoop
{
    private VulkanFrameOperationQueue _frameOperationQueue => _framePlanner.Operations;

    internal bool TryBeginOrderedComputeBatch()
        => _frameOperationQueue.TryBeginOrderedBatch();

    internal void CommitOrderedComputeBatch()
        => _frameOperationQueue.CommitOrderedBatch();

    internal void RollbackOrderedComputeBatch()
        => _frameOperationQueue.RollbackOrderedBatch();
    private VulkanFramePlannerMutableState<
        VulkanFrameOpPlannerStateKey,
        FrameOpResourcePlannerSwitchingState,
        VulkanQueueOwnershipConfigCacheEntry,
        MergedFrameOpRegistryCacheEntry,
        FrameOpRegistryCacheSource,
        ActivePassMetadataFilterCacheEntry> PlannerMutableState
        => _framePlanner.MutableState;
    private List<VulkanFrameOpPlannerStateKey> _frameOpPlannerStateKeyScratch
        => PlannerMutableState.PlannerStateKeyScratch;
    private List<VulkanFrameOpPlannerStateKey> _frameOpPlannerStateEvictionScratch
        => PlannerMutableState.PlannerStateEvictionScratch;
    private Dictionary<VulkanFrameOpPlannerStateKey, FrameOp[]> _frameOpPlannerPartitionCache
        => PlannerMutableState.PartitionCache;
    private VulkanFrameOpPlannerStateKey[] _frameOpPlannerPartitionKeyBuffer
        => PlannerMutableState.PartitionKeyBuffer;
    private ref ulong _frameOpPlannerPartitionSignature
        => ref PlannerMutableState.PartitionSignature;

    internal FrameOpContext CaptureFrameOpContextOrLastActive()
        => _resourcePlannerSessions.CaptureRuntimeState().LastActiveFrameOpContext ?? default;

    private void EnqueueFrameOp(FrameOp operation)
        => _commandRuntime.EnqueueFrameOperation(
            _frameOperationQueue,
            operation,
            VulkanCommandRuntime.EnsureValidPassIndex(
                operation.PassIndex,
                VulkanFrameOperationSemantics.GetFrameOpDiagnosticName(operation),
                operation.Context.PassMetadata));

    private FrameOp[] DrainFrameOps()
        => _frameOperationQueue.DrainPending();

    private bool HasThreadResourcePlannerRuntimeState
        => ReferenceEquals(
               _commandRuntime.ThreadWorkspace.Current.ResourcePlannerRuntimeStateOwner,
               _commandRuntime) &&
           _commandRuntime.ThreadWorkspace.Current.ResourcePlannerRuntimeState.HasValue;
    private FrameOpResourcePlannerSwitchingState ActiveFrameOpResourcePlannerSwitchingState
        => _resourcePlannerSessions.ResolveActiveSwitchingState();
    private VulkanBackendObjectContext BackendObjectContext
        => _resourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
            "The Vulkan backend object context has not been published.");
    private bool IsCommandChainResourcePlanFrozen => _framePlanner.IsResourcePlanFrozen;
    private bool SupportsTransformFeedback => BackendObjectContext.SupportsTransformFeedback;
    private VulkanResourceAllocator ResourceAllocator
        => _resourcePlannerSessions.CaptureRuntimeState().ResourceAllocator;

    private void StoreThreadResourcePlannerRuntimeState(in ResourcePlannerRuntimeState state)
        => _resourcePlannerSessions.RestoreRuntimeState(state);

    private void PublishResourcePlannerRuntimeState(
        in ResourcePlannerRuntimeState state,
        bool commitReusedImageMetadata)
    {
        if (commitReusedImageMetadata)
            state.ResourceAllocator.CommitReusedPhysicalImageMetadata();
        _resourcePlannerSessions.RestoreRuntimeState(state);
    }

    private ResourcePlannerFastPathKey ActiveResourcePlannerFastPathKey
    {
        get => _resourcePlannerSessions.CaptureRuntimeState().ResourcePlannerFastPathKey;
        set
        {
            ResourcePlannerRuntimeState state = _resourcePlannerSessions.CaptureRuntimeState();
            state.ResourcePlannerFastPathKey = value;
            _resourcePlannerSessions.RestoreRuntimeState(state);
        }
    }

    private bool ActiveHasResourcePlannerFastPathKey
    {
        get => _resourcePlannerSessions.CaptureRuntimeState().HasResourcePlannerFastPathKey;
        set
        {
            ResourcePlannerRuntimeState state = _resourcePlannerSessions.CaptureRuntimeState();
            state.HasResourcePlannerFastPathKey = value;
            _resourcePlannerSessions.RestoreRuntimeState(state);
        }
    }
    private VulkanCompiledRenderGraph CompiledRenderGraph
        => _resourcePlannerSessions.CaptureRuntimeState().CompiledRenderGraph;

    private FrameOp[] CaptureFrameOpsExcludingTextureUploads(Action emitFrameOps, out ulong signature)
    {
        FrameOp[] operations = _commandRuntime.CaptureFrameOperations(
            _framePlanner.Operations,
            emitFrameOps,
            excludeTextureUploads: true);
        signature = operations.Length == 0
            ? 0
            : VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations);
        return operations;
    }

    private FrameOp[] CaptureFrameOpsExcludingTextureUploads(
        IOpenXrEyeFrameOpEmitter emitter,
        in OpenXrEyeFrameOpEmission emission,
        out ulong signature)
    {
        FrameOp[] operations = _framePlanner.Operations.Capture(
            emitter,
            emission,
            excludeTextureUploads: true);
        signature = operations.Length == 0
            ? 0
            : VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations);
        return operations;
    }

    private FrameOp[] DrainFrameOpsExcludingTextureUploads(
        out ulong signature,
        bool computeSignature = true)
    {
        FrameOp[] operations = _commandRuntime.DrainFrameOperations(
            _framePlanner.Operations,
            excludeTextureUploads: true);
        signature = computeSignature && operations.Length != 0
            ? VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations)
            : 0;
        return operations;
    }

    internal ResourcePlannerRuntimeState CaptureResourcePlannerRuntimeState()
        => _resourcePlannerSessions.CaptureRuntimeState();

    private void RestoreResourcePlannerRuntimeState(in ResourcePlannerRuntimeState state)
        => _resourcePlannerSessions.RestoreRuntimeState(state);

    private void RetireResourcePlannerRuntimeStateAllocators(
        in ResourcePlannerRuntimeState state,
        HashSet<VulkanResourceAllocator> retiredAllocators,
        string reason)
    {
        RetireResourcePlannerRuntimeStateAllocator(state, retiredAllocators, reason);
        FrameOpResourcePlannerSwitchingState? switchingState = state.FrameOpResourcePlannerSwitchingState;
        if (switchingState is null)
            return;

        foreach (ResourcePlannerRuntimeState nestedState in switchingState.States.Values)
            RetireResourcePlannerRuntimeStateAllocator(nestedState, retiredAllocators, reason);
        if (switchingState.HasPreparationState)
            RetireResourcePlannerRuntimeStateAllocator(
                switchingState.PreparationState,
                retiredAllocators,
                reason);
    }

    private void RetireResourcePlannerRuntimeStateAllocator(
        in ResourcePlannerRuntimeState state,
        HashSet<VulkanResourceAllocator> retiredAllocators,
        string reason)
    {
        VulkanResourceAllocator allocator = state.ResourceAllocator;
        if (allocator is null || allocator.IsRetired || !retiredAllocators.Add(allocator))
            return;

        _resourcePlannerSessions.RestoreRuntimeState(state);
        VulkanBackendObjectContext backendContext =
            _resourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
                $"Cannot retire planner allocator for '{reason}' before backend context publication.");
        _ = allocator.TryRetirePhysicalResources(backendContext);
    }

    internal bool TryDescribeRecentResourceAllocationFailure(out string reason)
        => VulkanFramePlanner.TryDescribeRecentResourceAllocationFailure(
            _resourcePlannerSessions.CaptureRuntimeState(),
            RuntimeEngine.VRState.IsOpenXRActive,
            out reason);

    private VulkanResourceExtentContext BuildResourceExtentContext(in FrameOpContext context)
    {
        if (TryGetExternalSwapchainTargetRegion(out var region) && region.Width > 0 && region.Height > 0)
        {
            var dimensions = VulkanFramePlanner.ResolveExternalFrameOpResourceDimensions(
                new Silk.NET.Vulkan.Extent2D((uint)region.Width, (uint)region.Height),
                context.PipelineInstance?.ResourceInternalWidth,
                context.PipelineInstance?.ResourceInternalHeight,
                null,
                null,
                context.InternalWidth,
                context.InternalHeight);
            return new VulkanResourceExtentContext(
                dimensions.DisplayWidth,
                dimensions.DisplayHeight,
                dimensions.InternalWidth,
                dimensions.InternalHeight);
        }

        uint displayWidth = context.DisplayWidth > 0 ? context.DisplayWidth : Math.Max(_outputRuntime.Desktop.Extent.Width, 1u);
        uint displayHeight = context.DisplayHeight > 0 ? context.DisplayHeight : Math.Max(_outputRuntime.Desktop.Extent.Height, 1u);
        return new VulkanResourceExtentContext(
            displayWidth,
            displayHeight,
            context.InternalWidth > 0 ? context.InternalWidth : displayWidth,
            context.InternalHeight > 0 ? context.InternalHeight : displayHeight);
    }

    private ExternalResourcePlannerReadbackScope EnterFrameOpResourcePlannerReadbackScope(
        in FrameOpContext context)
        => _resourcePlannerSessions.CreateReadbackScope(_deviceContext, _outputRuntime, context);

    /// <summary>Uses the planner's renderer-free admission predicate for frame-loop sequencing.</summary>
    private static bool FrameOpContextHasPlannerResources(in FrameOpContext context)
        => VulkanFramePlanner.FrameOpContextHasPlannerResources(context);

    private static ulong ResolveFrameOpContextDescriptorGeneration(RenderResourceRegistry? registry)
        => VulkanFramePlanner.ResolveFrameOpContextDescriptorGeneration(registry);

    private static FrameOpContext RefreshFrameOpContextRecordingFingerprint(in FrameOpContext context)
        => VulkanFramePlanner.RefreshFrameOpContextRecordingFingerprint(context);
}

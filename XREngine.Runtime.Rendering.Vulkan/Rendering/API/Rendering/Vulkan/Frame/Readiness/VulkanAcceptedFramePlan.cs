using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Slot-owned immutable producer snapshot accepted before swapchain acquisition.
/// Authoring operations live here only until the native numeric plan is sealed.
/// All storage is allocated with the arena, never while executing an accepted
/// foreground frame.
/// </summary>
internal sealed class VulkanAcceptedFramePlan
{
    internal const int TerminalCapacity = 512;
    internal const int UiCapacity = 1024;
    internal const int MainSceneCapacity = 8192;
    internal const int ShadowCapacity = 4096;
    internal const int UploadCapacity = 4096;
    internal const int DependencyCapacity = 16384;
    internal const int StaticCapacity = TerminalCapacity + MainSceneCapacity + ShadowCapacity;

    private readonly FrameOp[] _staticOperations = new FrameOp[StaticCapacity];
    private readonly FrameOp[] _dynamicUiOperations = new FrameOp[UiCapacity];
    private readonly FrameOp[] _textureUploadOperations = new FrameOp[UploadCapacity];
    private readonly VulkanFrameDependencyTicket[] _dependencies =
        new VulkanFrameDependencyTicket[DependencyCapacity];

    internal VulkanPreparedMeshIngress PreparedMeshIngress { get; } = new();
    internal VulkanTextureUploadManifest RequiredTextureUploads { get; } = new();
    internal ShadowAtlasReadinessManifest ShadowReadiness { get; set; }
    internal ShadowAtlasReadinessResult ShadowReadinessResult { get; set; }
    internal ulong FrameId { get; private set; }
    internal ulong SceneEpoch { get; private set; }
    internal int FrameSlot { get; private set; } = -1;
    internal int StaticOperationCount { get; private set; }
    internal int DynamicUiOperationCount { get; private set; }
    internal int TextureUploadOperationCount { get; private set; }
    internal int TerminalOperationCount { get; private set; }
    internal int MainSceneOperationCount { get; private set; }
    internal int ShadowOperationCount { get; private set; }
    internal int DependencyCount { get; private set; }
    internal RenderOutputRequest OutputContract { get; private set; }
    internal VulkanPresentNowTargetCompatibilityKey TargetCompatibility { get; private set; }
    internal ulong LogicalPlanGeneration { get; private set; }
    internal ResourcePlannerRuntimeState PlannerState { get; private set; }
    internal VulkanFramePlanningSnapshot FrozenPlanningSnapshot { get; private set; }
    internal bool IsSealed { get; private set; }

    internal FrameOp[] StaticOperations => _staticOperations;
    internal FrameOp[] DynamicUiOperations => _dynamicUiOperations;
    internal FrameOp[] TextureUploadOperations => _textureUploadOperations;

    internal void Begin(
        int frameSlot,
        ulong frameId,
        ulong sceneEpoch,
        in VulkanPresentNowTargetCompatibilityKey targetCompatibility)
    {
        Reset();
        FrameSlot = frameSlot;
        FrameId = frameId;
        SceneEpoch = sceneEpoch;
        TargetCompatibility = targetCompatibility;
    }

    internal void CaptureOperations(
        ReadOnlySpan<FrameOp> staticOperations,
        ReadOnlySpan<FrameOp> dynamicUiOperations,
        ReadOnlySpan<FrameOp> textureUploadOperations)
    {
        if (IsSealed)
            throw new InvalidOperationException("The accepted frame plan is already sealed.");

        for (int index = 0; index < staticOperations.Length; index++)
        {
            FrameOp operation = staticOperations[index];
            EVulkanAcceptedFrameLane lane = ClassifyStaticOperation(operation);
            int laneCount = lane switch
            {
                EVulkanAcceptedFrameLane.Terminal => ++TerminalOperationCount,
                EVulkanAcceptedFrameLane.Shadow => ++ShadowOperationCount,
                _ => ++MainSceneOperationCount,
            };
            int laneCapacity = lane switch
            {
                EVulkanAcceptedFrameLane.Terminal => TerminalCapacity,
                EVulkanAcceptedFrameLane.Shadow => ShadowCapacity,
                _ => MainSceneCapacity,
            };
            if (laneCount > laneCapacity)
                throw new VulkanAcceptedFramePlanCapacityException(
                    lane,
                    laneCapacity,
                    laneCount);
            if (StaticOperationCount >= _staticOperations.Length)
                throw new VulkanAcceptedFramePlanCapacityException(
                    lane,
                    _staticOperations.Length,
                    StaticOperationCount + 1);
            _staticOperations[StaticOperationCount++] = operation;
        }

        if (dynamicUiOperations.Length > _dynamicUiOperations.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Ui,
                _dynamicUiOperations.Length,
                dynamicUiOperations.Length);
        dynamicUiOperations.CopyTo(_dynamicUiOperations);
        DynamicUiOperationCount = dynamicUiOperations.Length;

        if (textureUploadOperations.Length > _textureUploadOperations.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _textureUploadOperations.Length,
                textureUploadOperations.Length);
        textureUploadOperations.CopyTo(_textureUploadOperations);
        TextureUploadOperationCount = textureUploadOperations.Length;
    }

    internal ref VulkanFrameDependencyTicket AddDependency(
        EVulkanFrameDependencyKind kind,
        ulong resourceKey,
        ulong generation)
    {
        if (IsSealed)
            throw new InvalidOperationException("The accepted frame plan is already sealed.");
        if (DependencyCount >= _dependencies.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Dependency,
                _dependencies.Length,
                DependencyCount + 1);

        ref VulkanFrameDependencyTicket ticket = ref _dependencies[DependencyCount++];
        ticket.Declare(kind, resourceKey, generation);
        return ref ticket;
    }

    internal Span<VulkanFrameDependencyTicket> Dependencies
        => _dependencies.AsSpan(0, DependencyCount);

    internal void Seal(
        in RenderOutputRequest outputContract,
        ulong logicalPlanGeneration,
        in ResourcePlannerRuntimeState plannerState,
        in VulkanFramePlanningSnapshot frozenPlanningSnapshot)
    {
        OutputContract = outputContract;
        LogicalPlanGeneration = logicalPlanGeneration;
        PlannerState = plannerState;
        FrozenPlanningSnapshot = frozenPlanningSnapshot;
        IsSealed = true;
    }

    /// <summary>
    /// Rebinds only format/output compatibility while no WSI image is owned.
    /// The accepted camera, visibility, operation, and dependency snapshot stays
    /// unchanged.
    /// </summary>
    internal void UpdateTargetCompatibility(
        in VulkanPresentNowTargetCompatibilityKey compatibility)
    {
        if (!IsSealed)
            throw new InvalidOperationException(
                "Only a sealed accepted plan may rebind target compatibility.");
        TargetCompatibility = compatibility;
    }

    internal void Reset()
    {
        _staticOperations.AsSpan(0, StaticOperationCount).Clear();
        _dynamicUiOperations.AsSpan(0, DynamicUiOperationCount).Clear();
        _textureUploadOperations.AsSpan(0, TextureUploadOperationCount).Clear();
        for (int index = 0; index < DependencyCount; index++)
            _dependencies[index].Clear();
        PreparedMeshIngress.Clear();
        RequiredTextureUploads.BeginCapture();
        ShadowReadiness = default;
        ShadowReadinessResult = default;
        FrameId = 0UL;
        SceneEpoch = 0UL;
        FrameSlot = -1;
        StaticOperationCount = 0;
        DynamicUiOperationCount = 0;
        TextureUploadOperationCount = 0;
        TerminalOperationCount = 0;
        MainSceneOperationCount = 0;
        ShadowOperationCount = 0;
        DependencyCount = 0;
        OutputContract = default;
        TargetCompatibility = default;
        LogicalPlanGeneration = 0UL;
        PlannerState = default;
        FrozenPlanningSnapshot = default;
        IsSealed = false;
    }

    private static EVulkanAcceptedFrameLane ClassifyStaticOperation(FrameOp operation)
    {
        EVulkanFrameOpContextKind kind = operation.Context.ContextKind;
        if (kind == EVulkanFrameOpContextKind.Shadow)
            return EVulkanAcceptedFrameLane.Shadow;
        if (kind is EVulkanFrameOpContextKind.UiPreview or
            EVulkanFrameOpContextKind.OpenXrMirror ||
            operation.Target is null && operation.PassIndex == (int)EDefaultRenderPass.OnTopForward)
        {
            return EVulkanAcceptedFrameLane.Terminal;
        }
        return EVulkanAcceptedFrameLane.MainScene;
    }
}

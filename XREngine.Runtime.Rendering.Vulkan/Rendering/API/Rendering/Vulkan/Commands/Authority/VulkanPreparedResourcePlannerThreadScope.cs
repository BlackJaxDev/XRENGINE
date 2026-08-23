namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Installs the exact prepared resource-planner generation while a frozen
/// command stream is recorded, then restores the worker's prior planner state.
/// </summary>
internal readonly struct VulkanPreparedResourcePlannerThreadScope : IDisposable
{
    private readonly VulkanCommandThreadContext _context;
    private readonly VulkanCommandRuntime _owner;
    private readonly VulkanCommandRuntime? _previousPlannerOwner;
    private readonly ResourcePlannerRuntimeState? _previousPlannerState;
    private readonly ResourcePlannerRuntimeGeneration? _previousPlannerGeneration;
    private readonly VulkanCommandRuntime? _previousSwitchingOwner;
    private readonly FrameOpResourcePlannerSwitchingState? _previousSwitchingState;

    internal VulkanPreparedResourcePlannerThreadScope(
        VulkanCommandThreadContext context,
        VulkanCommandRuntime owner,
        in ResourcePlannerRuntimeState preparedState)
    {
        if (!ReferenceEquals(context.Owner, owner))
        {
            throw new InvalidOperationException(
                "A prepared planner scope must use its command runtime's thread workspace.");
        }

        ResourcePlannerRuntimeState scopedState = preparedState;
        scopedState.FrameOpResourcePlannerSwitchingState ??=
            new FrameOpResourcePlannerSwitchingState();

        _context = context;
        _owner = owner;
        _previousPlannerOwner = context.ResourcePlannerRuntimeStateOwner;
        _previousPlannerState = context.ResourcePlannerRuntimeState;
        _previousPlannerGeneration = context.ResourcePlannerRuntimeGeneration;
        _previousSwitchingOwner =
            context.FrameOpResourcePlannerSwitchingStateOwner;
        _previousSwitchingState = context.FrameOpResourcePlannerSwitchingState;

        context.ResourcePlannerRuntimeStateOwner = owner;
        context.ResourcePlannerRuntimeState = scopedState;
        context.ResourcePlannerRuntimeGeneration =
            new ResourcePlannerRuntimeGeneration(scopedState);
        context.FrameOpResourcePlannerSwitchingStateOwner = owner;
        context.FrameOpResourcePlannerSwitchingState =
            scopedState.FrameOpResourcePlannerSwitchingState;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_context.ResourcePlannerRuntimeStateOwner, _owner))
        {
            throw new InvalidOperationException(
                "The prepared planner scope was replaced before it was restored.");
        }

        _context.FrameOpResourcePlannerSwitchingStateOwner =
            _previousSwitchingOwner;
        _context.FrameOpResourcePlannerSwitchingState =
            _previousSwitchingState;
        _context.ResourcePlannerRuntimeStateOwner = _previousPlannerOwner;
        _context.ResourcePlannerRuntimeState = _previousPlannerState;
        _context.ResourcePlannerRuntimeGeneration = _previousPlannerGeneration;
    }
}

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Restores OpenXR planner identity and publishes the thread's updated planner
/// state without retaining a <see cref="VulkanRenderer"/>.
/// </summary>
internal readonly struct VulkanOpenXrResourcePlannerThreadScope : IDisposable
{
    private readonly VulkanOpenXrResourcePlannerThreadData _data;
    private readonly VulkanOpenXrViewResourcePlannerContextKey _contextKey;
    private readonly VulkanOpenXrThreadExecutionState _executionState;
    private readonly VulkanOpenXrViewResourcePlannerContextKey _previousScopeKey;
    private readonly int _previousScopeDepth;
    private readonly ResourcePlannerRuntimeState? _previousPlannerState;
    private readonly ResourcePlannerRuntimeGeneration? _previousPlannerGeneration;
    private readonly VulkanCommandRuntime? _previousPlannerOwner;
    private readonly FrameOpResourcePlannerSwitchingState? _previousSwitchingState;
    private readonly VulkanCommandRuntime? _previousSwitchingOwner;
    private readonly bool _ownsThreadScopes;

    internal VulkanOpenXrResourcePlannerThreadScope(
        in VulkanOpenXrResourcePlannerThreadData data,
        in VulkanOpenXrViewResourcePlannerContextKey contextKey)
    {
        if (!ReferenceEquals(data.ThreadContext.Owner, data.Owner))
            throw new InvalidOperationException("The OpenXR planner scope must use its command runtime's thread workspace.");

        _data = data;
        _contextKey = contextKey;
        _executionState = data.Session.ExecutionState;
        _previousScopeKey = _executionState.ResourcePlannerKey;
        _previousScopeDepth = _executionState.ResourcePlannerDepth;
        bool reentrant = _previousScopeDepth > 0 && _previousScopeKey.Equals(contextKey);
        _executionState.ResourcePlannerKey = contextKey;
        _executionState.ResourcePlannerDepth = reentrant ? _previousScopeDepth + 1 : 1;
        _ownsThreadScopes = !reentrant;
        _previousPlannerState = default;
        _previousPlannerGeneration = null;
        _previousPlannerOwner = null;
        _previousSwitchingState = null;
        _previousSwitchingOwner = null;
        if (reentrant)
            return;

        ResourcePlannerRuntimeState state;
        lock (data.Session.StateGate)
        {
            state = data.Session.States.TryGetValue(contextKey, out ResourcePlannerRuntimeState existing)
                ? existing
                : ResourcePlannerRuntimeState.CreateEmpty();
        }

        state.FrameOpResourcePlannerSwitchingState ??= new FrameOpResourcePlannerSwitchingState();
        _previousPlannerState = data.ThreadContext.ResourcePlannerRuntimeState;
        _previousPlannerGeneration = data.ThreadContext.ResourcePlannerRuntimeGeneration;
        _previousPlannerOwner = data.ThreadContext.ResourcePlannerRuntimeStateOwner;
        _previousSwitchingState = data.ThreadContext.FrameOpResourcePlannerSwitchingState;
        _previousSwitchingOwner = data.ThreadContext.FrameOpResourcePlannerSwitchingStateOwner;
        data.ThreadContext.ResourcePlannerRuntimeStateOwner = data.Owner;
        data.ThreadContext.ResourcePlannerRuntimeState = state;
        data.ThreadContext.ResourcePlannerRuntimeGeneration = new ResourcePlannerRuntimeGeneration(state);
        data.ThreadContext.FrameOpResourcePlannerSwitchingStateOwner = data.Owner;
        data.ThreadContext.FrameOpResourcePlannerSwitchingState = state.FrameOpResourcePlannerSwitchingState;
    }

    public void Dispose()
    {
        if (_ownsThreadScopes)
        {
            ResourcePlannerRuntimeState state =
                _data.ThreadContext.ResourcePlannerRuntimeState ??
                ResourcePlannerRuntimeState.CreateEmpty();
            state.FrameOpResourcePlannerSwitchingState =
                _data.ThreadContext.FrameOpResourcePlannerSwitchingState ??
                state.FrameOpResourcePlannerSwitchingState;
            if (_data.Owner.IsDeviceOperational)
            {
                lock (_data.Session.StateGate)
                    _data.Session.States[_contextKey] = state;
            }

            _data.ThreadContext.FrameOpResourcePlannerSwitchingStateOwner = _previousSwitchingOwner;
            _data.ThreadContext.FrameOpResourcePlannerSwitchingState = _previousSwitchingState;
            _data.ThreadContext.ResourcePlannerRuntimeStateOwner = _previousPlannerOwner;
            _data.ThreadContext.ResourcePlannerRuntimeState = _previousPlannerState;
            _data.ThreadContext.ResourcePlannerRuntimeGeneration = _previousPlannerGeneration;
        }

        _executionState.ResourcePlannerKey = _previousScopeKey;
        _executionState.ResourcePlannerDepth = _previousScopeDepth;
    }
}

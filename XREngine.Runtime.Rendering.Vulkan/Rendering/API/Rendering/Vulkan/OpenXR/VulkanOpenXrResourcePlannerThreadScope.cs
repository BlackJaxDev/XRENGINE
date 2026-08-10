namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Explicit command-side data needed to install an OpenXR planner state on one
/// recording thread. This keeps the scope independent from the renderer facade.
/// </summary>
internal readonly record struct VulkanOpenXrResourcePlannerThreadData(
    VulkanOpenXrBackend Backend,
    VulkanDeviceContext Device,
    Dictionary<VulkanOpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> States,
    VulkanCommandThreadContext<
        VulkanStateTracker,
        ResourcePlannerRuntimeState,
        FrameOpResourcePlannerSwitchingState,
        XRFrameBuffer,
        EReadBufferMode> ThreadContext,
    object OwnerToken);

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
    private readonly object? _previousPlannerOwner;
    private readonly FrameOpResourcePlannerSwitchingState? _previousSwitchingState;
    private readonly object? _previousSwitchingOwner;
    private readonly bool _ownsThreadScopes;

    internal VulkanOpenXrResourcePlannerThreadScope(
        in VulkanOpenXrResourcePlannerThreadData data,
        in VulkanOpenXrViewResourcePlannerContextKey contextKey)
    {
        _data = data;
        _contextKey = contextKey;
        _executionState = data.Backend.CurrentThreadExecutionState;
        _previousScopeKey = _executionState.ResourcePlannerKey;
        _previousScopeDepth = _executionState.ResourcePlannerDepth;
        bool reentrant = _previousScopeDepth > 0 && _previousScopeKey.Equals(contextKey);
        _executionState.ResourcePlannerKey = contextKey;
        _executionState.ResourcePlannerDepth = reentrant ? _previousScopeDepth + 1 : 1;
        _ownsThreadScopes = !reentrant;
        _previousPlannerState = default;
        _previousPlannerOwner = null;
        _previousSwitchingState = null;
        _previousSwitchingOwner = null;
        if (reentrant)
            return;

        ResourcePlannerRuntimeState state;
        lock (data.Backend.ResourcePlannerStatesLock)
        {
            state = data.States.TryGetValue(contextKey, out ResourcePlannerRuntimeState existing)
                ? existing
                : ResourcePlannerRuntimeState.CreateEmpty();
        }

        state.FrameOpResourcePlannerSwitchingState ??= new FrameOpResourcePlannerSwitchingState();
        _previousPlannerState = data.ThreadContext.ResourcePlannerRuntimeState;
        _previousPlannerOwner = data.ThreadContext.ResourcePlannerRuntimeStateOwner;
        _previousSwitchingState = data.ThreadContext.FrameOpResourcePlannerSwitchingState;
        _previousSwitchingOwner = data.ThreadContext.FrameOpResourcePlannerSwitchingStateOwner;
        data.ThreadContext.ResourcePlannerRuntimeStateOwner = data.OwnerToken;
        data.ThreadContext.ResourcePlannerRuntimeState = state;
        data.ThreadContext.FrameOpResourcePlannerSwitchingStateOwner = data.OwnerToken;
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
            if (_data.Device.IsOperational)
            {
                lock (_data.Backend.ResourcePlannerStatesLock)
                    _data.States[_contextKey] = state;
            }

            _data.ThreadContext.FrameOpResourcePlannerSwitchingStateOwner = _previousSwitchingOwner;
            _data.ThreadContext.FrameOpResourcePlannerSwitchingState = _previousSwitchingState;
            _data.ThreadContext.ResourcePlannerRuntimeStateOwner = _previousPlannerOwner;
            _data.ThreadContext.ResourcePlannerRuntimeState = _previousPlannerState;
        }

        _executionState.ResourcePlannerKey = _previousScopeKey;
        _executionState.ResourcePlannerDepth = _previousScopeDepth;
    }
}

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    internal readonly struct OpenXrResourcePlannerThreadScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly VulkanOpenXrViewResourcePlannerContextKey _contextKey;
        private readonly VulkanRenderer.ThreadResourcePlannerRuntimeStateScope _threadScope;
        private readonly VulkanRenderer.ThreadFrameOpResourcePlannerSwitchingStateScope _frameOpThreadScope;
        private readonly VulkanOpenXrThreadExecutionState _executionState;
        private readonly VulkanOpenXrViewResourcePlannerContextKey _previousScopeKey;
        private readonly int _previousScopeDepth;
        private readonly bool _ownsThreadScopes;

        public OpenXrResourcePlannerThreadScope(
            VulkanRenderer renderer,
            in VulkanOpenXrViewResourcePlannerContextKey contextKey)
        {
            _renderer = renderer;
            _contextKey = contextKey;
            _executionState = renderer.OutputRuntime.OpenXrBackend.CurrentThreadExecutionState;
            _previousScopeKey = _executionState.ResourcePlannerKey;
            _previousScopeDepth = _executionState.ResourcePlannerDepth;
            bool reentrant = _previousScopeDepth > 0 &&
                _previousScopeKey.Equals(contextKey);
            _executionState.ResourcePlannerKey = contextKey;
            _executionState.ResourcePlannerDepth = reentrant ? _previousScopeDepth + 1 : 1;
            _ownsThreadScopes = !reentrant;
            if (reentrant)
            {
                _threadScope = default;
                _frameOpThreadScope = default;
                return;
            }

            VulkanRenderer.ResourcePlannerRuntimeState openXrState;
            lock (renderer.OutputRuntime.OpenXrBackend.ResourcePlannerStatesLock)
            {
                openXrState = renderer.OpenXrResourcePlannerStates.TryGetValue(_contextKey, out VulkanRenderer.ResourcePlannerRuntimeState existingState)
                    ? existingState
                    : VulkanRenderer.ResourcePlannerRuntimeState.CreateEmpty();
            }
            openXrState.FrameOpResourcePlannerSwitchingState ??= new VulkanRenderer.FrameOpResourcePlannerSwitchingState();
            _threadScope = renderer.EnterThreadResourcePlannerRuntimeStateScope(in openXrState);
            _frameOpThreadScope = renderer.EnterThreadFrameOpResourcePlannerSwitchingStateScope(
                openXrState.FrameOpResourcePlannerSwitchingState);
        if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] enter thread planner context {0}",
                DescribeOpenXrResourcePlannerContextKey(in _contextKey));
            }
        }

        public void Dispose()
        {
            if (!_ownsThreadScopes)
            {
                RestorePreviousScopeIdentity();
                return;
            }

            VulkanRenderer.ResourcePlannerRuntimeState state = _threadScope.CaptureCurrent(_renderer);
            state.FrameOpResourcePlannerSwitchingState = _frameOpThreadScope.CaptureCurrent(_renderer);
            if (_renderer.DeviceContext.IsOperational)
            {
                lock (_renderer.OutputRuntime.OpenXrBackend.ResourcePlannerStatesLock)
                    _renderer.OpenXrResourcePlannerStates[_contextKey] = state;
            }
        if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] leave thread planner context {0}",
                DescribeOpenXrResourcePlannerContextKey(in _contextKey));
            }
            _frameOpThreadScope.Dispose();
            _threadScope.Dispose();
            RestorePreviousScopeIdentity();
        }

        private void RestorePreviousScopeIdentity()
        {
            _executionState.ResourcePlannerKey = _previousScopeKey;
            _executionState.ResourcePlannerDepth = _previousScopeDepth;
        }
    }
}

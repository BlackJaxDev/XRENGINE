namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly struct OpenXrResourcePlannerThreadScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly OpenXrViewResourcePlannerContextKey _contextKey;
        private readonly ThreadResourcePlannerRuntimeStateScope _threadScope;
        private readonly ThreadFrameOpResourcePlannerSwitchingStateScope _frameOpThreadScope;
        private readonly VulkanRenderer? _previousScopeRenderer;
        private readonly OpenXrViewResourcePlannerContextKey _previousScopeKey;
        private readonly int _previousScopeDepth;
        private readonly bool _ownsThreadScopes;

        public OpenXrResourcePlannerThreadScope(
            VulkanRenderer renderer,
            in OpenXrViewResourcePlannerContextKey contextKey)
        {
            _renderer = renderer;
            _contextKey = contextKey;
            _previousScopeRenderer = _threadOpenXrResourcePlannerScopeRenderer;
            _previousScopeKey = _threadOpenXrResourcePlannerScopeKey;
            _previousScopeDepth = _threadOpenXrResourcePlannerScopeDepth;
            bool reentrant = ReferenceEquals(_previousScopeRenderer, renderer) &&
                _previousScopeDepth > 0 &&
                _previousScopeKey.Equals(contextKey);
            _threadOpenXrResourcePlannerScopeRenderer = renderer;
            _threadOpenXrResourcePlannerScopeKey = contextKey;
            _threadOpenXrResourcePlannerScopeDepth = reentrant ? _previousScopeDepth + 1 : 1;
            _ownsThreadScopes = !reentrant;
            if (reentrant)
            {
                _threadScope = default;
                _frameOpThreadScope = default;
                return;
            }

            ResourcePlannerRuntimeState openXrState;
            lock (renderer._openXrResourcePlannerStatesLock)
            {
                openXrState = renderer._openXrResourcePlannerStates.TryGetValue(_contextKey, out ResourcePlannerRuntimeState existingState)
                    ? existingState
                    : ResourcePlannerRuntimeState.CreateEmpty();
            }
            openXrState.FrameOpResourcePlannerSwitchingState ??= new FrameOpResourcePlannerSwitchingState();
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

            ResourcePlannerRuntimeState state = _threadScope.CaptureCurrent(_renderer);
            state.FrameOpResourcePlannerSwitchingState = _frameOpThreadScope.CaptureCurrent(_renderer);
            if (_renderer.IsDeviceOperational)
            {
                lock (_renderer._openXrResourcePlannerStatesLock)
                    _renderer._openXrResourcePlannerStates[_contextKey] = state;
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
            _threadOpenXrResourcePlannerScopeRenderer = _previousScopeRenderer;
            _threadOpenXrResourcePlannerScopeKey = _previousScopeKey;
            _threadOpenXrResourcePlannerScopeDepth = _previousScopeDepth;
        }
    }
}

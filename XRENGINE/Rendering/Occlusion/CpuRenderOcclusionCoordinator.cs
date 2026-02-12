using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// CPU-render-path occlusion coordinator.
    ///
    /// Designed to be invoked from render-pass orchestration (not mesh-command internals):
    /// - Resolve previous-frame async query results
    /// - Apply temporal hysteresis per mesh command
    /// - Issue new hardware occlusion queries around CPU mesh draws
    ///
    /// Notes:
    /// - OpenGL path is implemented (begin/end query around draw).
    /// - Vulkan path currently remains conservative-visible because Begin/End query recording requires command-buffer integration.
    /// </summary>
    public sealed class CpuRenderOcclusionCoordinator
    {
        private sealed class QueryState
        {
            public XRRenderQuery Query = null!;
            public int ConsecutiveOccludedFrames;
            public ulong LastTouchedFrame;
            public bool LastAnySamplesPassed = true;
        }

        private sealed class PassState
        {
            public readonly Dictionary<uint, QueryState> Queries = new();
            public Vector3 LastCameraPosition;
            public Matrix4x4 LastProjection;
            public bool HasCameraState;
            public ulong LastResolvedFrameId;
            public uint LastSceneCommandCount;
        }

        private readonly object _lock = new();
        private readonly Dictionary<int, PassState> _passStates = new();
        private readonly AsyncOcclusionQueryManager _queryManager = new();

        private const int HysteresisFrames = 2;
        private const float CameraJumpDistance = 2.0f;
        private const float ProjectionDeltaThreshold = 0.125f;
        private const int StaleEvictionFrames = 120;

        public void BeginPass(int renderPass, XRCamera camera, uint sceneCommandCount)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass);

                if (state.LastSceneCommandCount != sceneCommandCount)
                {
                    state.Queries.Clear();
                    state.LastSceneCommandCount = sceneCommandCount;
                    state.HasCameraState = false;
                }

                if (HasSignificantCameraChange(state, camera))
                    ResetTemporalState(state);

                ResolveAvailableResults(state);
            }
        }

        public bool ShouldRender(int renderPass, uint sourceCommandIndex)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass);
                if (!state.Queries.TryGetValue(sourceCommandIndex, out QueryState? queryState))
                    return true;

                queryState.LastTouchedFrame = Engine.Rendering.State.RenderFrameId;

                if (queryState.LastAnySamplesPassed)
                {
                    queryState.ConsecutiveOccludedFrames = 0;
                    return true;
                }

                queryState.ConsecutiveOccludedFrames++;
                return queryState.ConsecutiveOccludedFrames < HysteresisFrames;
            }
        }

        public void BeginQuery(int renderPass, uint sourceCommandIndex)
        {
            lock (_lock)
            {
                if (AbstractRenderer.Current is not OpenGLRenderer gl)
                    return;

                PassState state = GetPassState(renderPass);
                QueryState queryState = GetOrCreateQueryState(state, sourceCommandIndex);

                GLRenderQuery? glQuery = gl.GenericToAPI<GLRenderQuery>(queryState.Query);
                if (glQuery is null)
                    return;

                glQuery.BeginQuery(EQueryTarget.AnySamplesPassedConservative);
            }
        }

        public void EndQuery(int renderPass, uint sourceCommandIndex)
        {
            lock (_lock)
            {
                if (AbstractRenderer.Current is not OpenGLRenderer gl)
                    return;

                PassState state = GetPassState(renderPass);
                if (!state.Queries.TryGetValue(sourceCommandIndex, out QueryState? queryState))
                    return;

                GLRenderQuery? glQuery = gl.GenericToAPI<GLRenderQuery>(queryState.Query);
                glQuery?.EndQuery();
            }
        }

        private PassState GetPassState(int renderPass)
        {
            if (!_passStates.TryGetValue(renderPass, out PassState? state))
            {
                state = new PassState();
                _passStates.Add(renderPass, state);
            }

            return state;
        }

        private QueryState GetOrCreateQueryState(PassState state, uint sourceCommandIndex)
        {
            if (state.Queries.TryGetValue(sourceCommandIndex, out QueryState? existing))
                return existing;

            var created = new QueryState
            {
                Query = _queryManager.Acquire(EQueryTarget.AnySamplesPassedConservative),
                ConsecutiveOccludedFrames = 0,
                LastTouchedFrame = Engine.Rendering.State.RenderFrameId,
                LastAnySamplesPassed = true,
            };

            state.Queries[sourceCommandIndex] = created;
            return created;
        }

        private void ResolveAvailableResults(PassState state)
        {
            ulong frameId = Engine.Rendering.State.RenderFrameId;
            if (state.LastResolvedFrameId == frameId)
                return;

            state.LastResolvedFrameId = frameId;

            // Resolve results and collect stale keys for eviction.
            List<uint>? staleKeys = null;
            foreach (var (key, queryState) in state.Queries)
            {
                if (_queryManager.TryGetAnySamplesPassed(queryState.Query, out bool anySamplesPassed))
                    queryState.LastAnySamplesPassed = anySamplesPassed;

                // Evict queries not touched for StaleEvictionFrames to prevent unbounded growth
                // when scene objects are added/removed without changing total count.
                if (frameId - queryState.LastTouchedFrame > StaleEvictionFrames)
                {
                    staleKeys ??= new();
                    staleKeys.Add(key);
                }
            }

            if (staleKeys is not null)
            {
                foreach (uint key in staleKeys)
                {
                    if (state.Queries.Remove(key, out QueryState? removed))
                        _queryManager.Release(removed.Query);
                }
            }
        }

        private static bool HasSignificantCameraChange(PassState state, XRCamera camera)
        {
            Vector3 position = camera.Transform.WorldTranslation;
            Matrix4x4 projection = camera.ProjectionMatrix;

            if (!state.HasCameraState)
            {
                state.HasCameraState = true;
                state.LastCameraPosition = position;
                state.LastProjection = projection;
                return false;
            }

            bool movedFar = Vector3.DistanceSquared(state.LastCameraPosition, position) > (CameraJumpDistance * CameraJumpDistance);
            float projectionDelta = MathF.Abs(state.LastProjection.M11 - projection.M11) + MathF.Abs(state.LastProjection.M22 - projection.M22);
            bool projectionChanged = projectionDelta > ProjectionDeltaThreshold;

            state.LastCameraPosition = position;
            state.LastProjection = projection;
            return movedFar || projectionChanged;
        }

        private static void ResetTemporalState(PassState state)
        {
            foreach (QueryState queryState in state.Queries.Values)
            {
                queryState.ConsecutiveOccludedFrames = 0;
                queryState.LastAnySamplesPassed = true;
            }
        }
    }
}

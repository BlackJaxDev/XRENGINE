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
            public bool QueryPending;
            public bool DiscardPendingResult;
            public bool PendingQueryWasVisibleDraw;

            // Per-frame decision cache. Multiple passes (depth-normal prepass + color pass)
            // can ask for the same command's decision within one frame. Without this the
            // hysteresis counter would tick twice per frame and a second Begin/End on the
            // same GL query would clobber the result.
            public ulong LastDecidedFrameId;
            public ECpuOcclusionDecision LastDecision = ECpuOcclusionDecision.Visible;
            public ulong QueryIssuedFrameId;

        }

        private sealed class PassState
        {
            public readonly Dictionary<uint, QueryState> Queries = new();
            public Vector3 LastCameraPosition;
            public Vector3 LastCameraForward;
            public Vector3 LastCameraUp;
            public Matrix4x4 LastProjection;
            public bool HasCameraState;
            public bool CameraMovedThisFrame;
            public bool VisibilityInvalidatedThisFrame;
            public ulong LastResolvedFrameId;
            public uint LastSceneCommandCount;
        }

        private enum ECameraVisibilityChange
        {
            None,
            SmallMotion,
            LargeInvalidation,
        }

        private readonly object _lock = new();
        private readonly Dictionary<int, PassState> _passStates = new();
        private readonly AsyncOcclusionQueryManager _queryManager = new();

        // With ProbeOnly retest (depth-only AABB), retest frames don't write color so
        // hysteresis no longer needs to mask flicker. Hysteresis of 1 still absorbs a
        // single-frame async-query latency hiccup without forcing extra visible draws.
        private const int HysteresisFrames = 1;
        // Default period at which a still-occluded object is forced to refresh + requery
        // so it can detect unocclusion. Without this, an object whose query returned 0
        // samples would be skipped → never requeried → LastAnySamplesPassed stays false
        // forever (the classic occlusion-query deadlock). Configurable via
        // RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRetestPeriodFrames. Worst-case
        // unoccluder visibility latency ≈ retest period + 1-2 frames of async query
        // latency. Per-command stagger via sourceCommandIndex keeps the per-frame
        // retest cost bounded.
        private const int DefaultOccludedRetestPeriodFrames = 6;
        private const float CameraMotionEpsilon = 0.0001f;
        private const float CameraOrientationDotThreshold = 0.999999f;
        private const float CameraLargeMotionDistance = 2.0f;
        private const float CameraLargeOrientationDotThreshold = 0.9659258f; // cos(15 degrees)
        private const float ProjectionDeltaThreshold = 0.001f;
        private const float ProjectionInvalidationDeltaThreshold = 0.125f;
        private const int StaleEvictionFrames = 120;

        private static int GetOccludedRetestPeriodFrames()
        {
            int period = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRetestPeriodFrames;
            return period > 0 ? period : DefaultOccludedRetestPeriodFrames;
        }

        public bool BeginPass(int renderPass, XRCamera camera, uint sceneCommandCount)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass);

                // C-CPU-4: with stable per-command identity (RenderCommand.StableQueryKey),
                // a scene mutation (add/remove) no longer invalidates other commands' query
                // results — only the removed command's QueryState becomes orphaned, and the
                // existing StaleEvictionFrames-based eviction inside ResolveAvailableResults
                // reclaims it.
                state.LastSceneCommandCount = sceneCommandCount;

                ECameraVisibilityChange cameraChange = UpdateCameraVisibilityState(state, camera);
                state.CameraMovedThisFrame = cameraChange != ECameraVisibilityChange.None;
                state.VisibilityInvalidatedThisFrame = cameraChange == ECameraVisibilityChange.LargeInvalidation;

                if (state.VisibilityInvalidatedThisFrame)
                    ResetTemporalState(state);

                ResolveAvailableResults(state);
                return state.VisibilityInvalidatedThisFrame;
            }
        }

        public bool ShouldRender(int renderPass, uint sourceCommandIndex)
            => ShouldRender(renderPass, sourceCommandIndex, out _) != ECpuOcclusionDecision.Skip;

        /// <summary>
        /// Tri-state CPU occlusion cull decision. <see cref="ECpuOcclusionDecision.Visible"/>
        /// means draw the mesh normally; <see cref="ECpuOcclusionDecision.ProbeOnly"/> means
        /// the mesh is currently occluded but is due for a periodic requery — the caller
        /// should issue a depth-only AABB proxy draw (no color writes) inside Begin/End
        /// query instead of drawing the full mesh, which eliminates the visible flicker
        /// that a full-mesh requery would cause; <see cref="ECpuOcclusionDecision.Skip"/>
        /// means the mesh is occluded and not scheduled for retest this frame — emit
        /// nothing.
        /// </summary>
        public ECpuOcclusionDecision ShouldRender(int renderPass, uint sourceCommandIndex, out bool needsHardwareQuery)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass);
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;

                if (!state.Queries.TryGetValue(sourceCommandIndex, out QueryState? queryState))
                {
                    // No prior query for this command — must draw + query to seed state.
                    needsHardwareQuery = true;
                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.Seed);
                    return ECpuOcclusionDecision.Visible;
                }

                // Per-frame decision cache: prepass and color pass share the same decision
                // and the same hardware query result. Subsequent calls don't re-issue.
                if (queryState.LastDecidedFrameId == frameId)
                {
                    // Caller still needs a query call only if no pass has issued one yet
                    // this frame. ProbeOnly's "needs query" is handled the same way.
                    needsHardwareQuery = queryState.QueryIssuedFrameId != frameId
                        && !queryState.QueryPending
                        && queryState.LastDecision != ECpuOcclusionDecision.Skip;
                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.Cached);
                    return queryState.LastDecision;
                }

                queryState.LastTouchedFrame = frameId;
                queryState.LastDecidedFrameId = frameId;

                if (queryState.LastAnySamplesPassed)
                {
                    queryState.ConsecutiveOccludedFrames = 0;
                    queryState.LastDecision = ECpuOcclusionDecision.Visible;
                    needsHardwareQuery = !queryState.QueryPending;
                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.VisibleQuery);
                    return ECpuOcclusionDecision.Visible;
                }

                // Do not trust a stale "occluded" result while the full visible draw
                // that should replace it is still in flight. Reusing or overwriting the
                // same GL query before its result becomes available can otherwise keep
                // LastAnySamplesPassed pinned false and cull visible meshes.
                if (queryState.QueryPending)
                {
                    if (queryState.PendingQueryWasVisibleDraw)
                    {
                        queryState.LastDecision = ECpuOcclusionDecision.Visible;
                        needsHardwareQuery = false;
                        OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.VisibleHysteresis);
                        return ECpuOcclusionDecision.Visible;
                    }

                    queryState.LastDecision = ECpuOcclusionDecision.Skip;
                    needsHardwareQuery = false;
                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.Skip);
                    return ECpuOcclusionDecision.Skip;
                }

                // Hysteresis: keep drawing for HysteresisFrames after first-detected
                // occlusion to absorb a single-frame async-query latency hiccup.
                if (queryState.ConsecutiveOccludedFrames < HysteresisFrames)
                {
                    queryState.ConsecutiveOccludedFrames++;
                    queryState.LastDecision = ECpuOcclusionDecision.Visible;
                    needsHardwareQuery = true;
                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.VisibleHysteresis);
                    return ECpuOcclusionDecision.Visible;
                }

                queryState.ConsecutiveOccludedFrames++;

                // Periodic re-test: a fully occluded object MUST refresh its query every
                // retestPeriod frames so its visibility state can update when the
                // occluder moves. Stagger by sourceCommandIndex so not every culled
                // object retests the same frame (bounded per-frame retest cost).
                int retestPeriod = state.CameraMovedThisFrame
                    ? Math.Max(1, (GetOccludedRetestPeriodFrames() + 1) / 2)
                    : GetOccludedRetestPeriodFrames();
                if (((frameId + sourceCommandIndex) % (ulong)retestPeriod) == 0UL)
                {
                    queryState.LastDecision = ECpuOcclusionDecision.ProbeOnly;
                    needsHardwareQuery = true;
                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.Probe);
                    return ECpuOcclusionDecision.ProbeOnly;
                }

                queryState.LastDecision = ECpuOcclusionDecision.Skip;
                needsHardwareQuery = false;
                OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.Skip);
                return ECpuOcclusionDecision.Skip;
            }
        }

        /// <summary>
        /// Non-mutating cull decision peek. Used by secondary CPU passes (e.g. full-overdraw
        /// debug visualization) that need to mirror what the primary RenderCPU pass actually
        /// renders *to color*. Returns false on probe (depth-only requery) frames because
        /// those contribute no visible pixels, so the overdraw viz must also skip them.
        /// </summary>
        public bool PeekShouldRender(int renderPass, uint sourceCommandIndex)
        {
            lock (_lock)
            {
                if (!_passStates.TryGetValue(renderPass, out PassState? state))
                    return true;
                if (!state.Queries.TryGetValue(sourceCommandIndex, out QueryState? queryState))
                    return true;
                if (queryState.LastAnySamplesPassed)
                    return true;
                if (queryState.QueryPending && queryState.PendingQueryWasVisibleDraw)
                    return true;
                if (queryState.ConsecutiveOccludedFrames < HysteresisFrames)
                    return true;

                // Primary pass on this frame will issue a probe-only depth-AABB draw, not
                // a visible mesh draw — the overdraw viz reflects color contribution, so
                // it must report "not rendered" here.
                return false;
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

                // Idempotent within a frame: prepass and color pass may both wrap this
                // command's draw. Issue exactly one Begin/End pair against the GL query,
                // so its result reflects all draws (which write to the same depth target).
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
                if (queryState.QueryIssuedFrameId == frameId)
                    return;
                if (queryState.QueryPending)
                    return;

                GLRenderQuery? glQuery = gl.GenericToAPI<GLRenderQuery>(queryState.Query);
                if (glQuery is null)
                    return;

                queryState.QueryIssuedFrameId = frameId;
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

                // Only end the GL query if BeginQuery actually started it this frame.
                // (QueryIssuedFrameId is set inside BeginQuery; if it matches current
                // frame this BeginQuery owned the GL query; otherwise this call is a
                // no-op for a second pass that wasn't the one to begin it.)
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
                if (queryState.QueryIssuedFrameId != frameId)
                    return;

                GLRenderQuery? glQuery = gl.GenericToAPI<GLRenderQuery>(queryState.Query);
                if (glQuery is null)
                    return;

                glQuery.EndQuery();
                queryState.QueryPending = true;
                queryState.PendingQueryWasVisibleDraw = queryState.LastDecision == ECpuOcclusionDecision.Visible;
            }
        }

        public void ForceVisible(int renderPass, uint sourceCommandIndex)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass);
                QueryState queryState = GetOrCreateQueryState(state, sourceCommandIndex);
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;

                queryState.ConsecutiveOccludedFrames = 0;
                queryState.LastTouchedFrame = frameId;
                queryState.LastAnySamplesPassed = true;
                queryState.LastDecision = ECpuOcclusionDecision.Visible;
                queryState.LastDecidedFrameId = frameId;
                queryState.QueryIssuedFrameId = frameId;
                queryState.PendingQueryWasVisibleDraw = false;

                if (queryState.QueryPending)
                    queryState.DiscardPendingResult = true;
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
                LastTouchedFrame = RuntimeEngine.Rendering.State.RenderFrameId,
                LastAnySamplesPassed = true,
                QueryPending = false,
                DiscardPendingResult = false,
                PendingQueryWasVisibleDraw = false,
            };

            state.Queries[sourceCommandIndex] = created;
            return created;
        }

        private void ResolveAvailableResults(PassState state)
        {
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (state.LastResolvedFrameId == frameId)
                return;

            state.LastResolvedFrameId = frameId;

            // Resolve results and collect stale keys for eviction.
            List<uint>? staleKeys = null;
            foreach (var (key, queryState) in state.Queries)
            {
                if (queryState.QueryPending &&
                    _queryManager.TryGetAnySamplesPassed(queryState.Query, out bool anySamplesPassed))
                {
                    queryState.QueryPending = false;
                    queryState.PendingQueryWasVisibleDraw = false;
                    if (!queryState.DiscardPendingResult)
                        queryState.LastAnySamplesPassed = anySamplesPassed;

                    queryState.DiscardPendingResult = false;
                }

                // Evict queries not touched for StaleEvictionFrames to prevent unbounded growth
                // when scene objects are added/removed without changing total count.
                if (!queryState.QueryPending &&
                    frameId - queryState.LastTouchedFrame > StaleEvictionFrames)
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

        private static ECameraVisibilityChange UpdateCameraVisibilityState(PassState state, XRCamera camera)
        {
            Vector3 position = camera.Transform.RenderTranslation;
            Vector3 forward = camera.Transform.RenderForward;
            Vector3 up = camera.Transform.RenderUp;
            Matrix4x4 projection = camera.ProjectionMatrixUnjittered;

            if (!state.HasCameraState)
            {
                state.HasCameraState = true;
                state.LastCameraPosition = position;
                state.LastCameraForward = forward;
                state.LastCameraUp = up;
                state.LastProjection = projection;
                return ECameraVisibilityChange.None;
            }

            float motionDistanceSq = Vector3.DistanceSquared(state.LastCameraPosition, position);
            float forwardDot = Vector3.Dot(state.LastCameraForward, forward);
            float upDot = Vector3.Dot(state.LastCameraUp, up);
            bool moved = motionDistanceSq > (CameraMotionEpsilon * CameraMotionEpsilon);
            bool rotated = forwardDot < CameraOrientationDotThreshold || upDot < CameraOrientationDotThreshold;
            float projectionDelta =
                MathF.Abs(state.LastProjection.M11 - projection.M11) +
                MathF.Abs(state.LastProjection.M22 - projection.M22) +
                MathF.Abs(state.LastProjection.M31 - projection.M31) +
                MathF.Abs(state.LastProjection.M32 - projection.M32) +
                MathF.Abs(state.LastProjection.M33 - projection.M33) +
                MathF.Abs(state.LastProjection.M43 - projection.M43);
            bool projectionChanged = projectionDelta > ProjectionDeltaThreshold;
            bool invalidated =
                motionDistanceSq > (CameraLargeMotionDistance * CameraLargeMotionDistance) ||
                forwardDot < CameraLargeOrientationDotThreshold ||
                upDot < CameraLargeOrientationDotThreshold ||
                projectionDelta > ProjectionInvalidationDeltaThreshold;

            state.LastCameraPosition = position;
            state.LastCameraForward = forward;
            state.LastCameraUp = up;
            state.LastProjection = projection;

            if (invalidated)
                return ECameraVisibilityChange.LargeInvalidation;

            return moved || rotated || projectionChanged
                ? ECameraVisibilityChange.SmallMotion
                : ECameraVisibilityChange.None;
        }

        private static void ResetTemporalState(PassState state)
        {
            foreach (QueryState queryState in state.Queries.Values)
            {
                queryState.ConsecutiveOccludedFrames = 0;
                queryState.LastAnySamplesPassed = true;
                queryState.LastDecision = ECpuOcclusionDecision.Visible;
                queryState.LastDecidedFrameId = ulong.MaxValue;
                queryState.QueryIssuedFrameId = ulong.MaxValue;
                if (queryState.QueryPending)
                    queryState.DiscardPendingResult = true;
            }
        }
    }
}

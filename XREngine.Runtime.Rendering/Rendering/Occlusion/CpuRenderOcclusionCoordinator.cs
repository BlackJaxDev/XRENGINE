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

            // Per-frame decision cache. Multiple passes (depth-normal prepass + color pass)
            // can ask for the same command's decision within one frame. Without this the
            // hysteresis counter would tick twice per frame and a second Begin/End on the
            // same GL query would clobber the result.
            public ulong LastDecidedFrameId;
            public ECpuOcclusionDecision LastDecision = ECpuOcclusionDecision.Visible;
            public ulong QueryIssuedFrameId;

            // Camera-jump reset cooperative state. When set, the next decision returns
            // ProbeOnly (cheap depth-only AABB) instead of full-mesh redraw, so a fleet
            // reset doesn't translate to a frame-spike of full-mesh draws.
            public bool ResetSeedProbe;
        }

        private sealed class PassState
        {
            public readonly Dictionary<uint, QueryState> Queries = new();
            public Vector3 LastCameraPosition;
            public Matrix4x4 LastProjection;
            public bool HasCameraState;
            public ulong LastResolvedFrameId;
            public uint LastSceneCommandCount;

            // Camera-jump stagger: when a significant camera change is detected we mark
            // a reset window of ResetStripeFrames frames; each frame inside the window
            // re-seeds one stripe of commands (by StableQueryKey % ResetStripeFrames).
            // This spreads the cost of seed-probe draws over multiple frames instead of
            // a single-frame visibility cliff.
            public ulong ResetStartFrameId;
            public bool ResetActive;
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
        private const float CameraJumpDistance = 2.0f;
        private const float ProjectionDeltaThreshold = 0.125f;
        private const int StaleEvictionFrames = 120;
        private const int ResetStripeFrames = 8;

        private static int GetOccludedRetestPeriodFrames()
        {
            int period = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRetestPeriodFrames;
            return period > 0 ? period : DefaultOccludedRetestPeriodFrames;
        }

        public void BeginPass(int renderPass, XRCamera camera, uint sceneCommandCount)
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

                if (HasSignificantCameraChange(state, camera))
                    BeginStaggeredReset(state);

                // Process active reset stripe (if any) — re-seeds only commands matching
                // the current frame's stripe index, spreading load over ResetStripeFrames.
                if (state.ResetActive)
                    ApplyResetStripe(state);

                ResolveAvailableResults(state);
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
                        && queryState.LastDecision != ECpuOcclusionDecision.Skip;
                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.Cached);
                    return queryState.LastDecision;
                }

                queryState.LastTouchedFrame = frameId;
                queryState.LastDecidedFrameId = frameId;

                // Camera-jump reseed: spend exactly one ProbeOnly frame seeding the
                // result, then resume normal logic. This eliminates the full-mesh
                // redraw spike when many commands reset on the same frame.
                if (queryState.ResetSeedProbe)
                {
                    queryState.ResetSeedProbe = false;
                    queryState.LastDecision = ECpuOcclusionDecision.ProbeOnly;
                    needsHardwareQuery = true;
                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.Probe);
                    return ECpuOcclusionDecision.ProbeOnly;
                }

                if (queryState.LastAnySamplesPassed)
                {
                    queryState.ConsecutiveOccludedFrames = 0;
                    queryState.LastDecision = ECpuOcclusionDecision.Visible;
                    needsHardwareQuery = true;
                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.VisibleQuery);
                    return ECpuOcclusionDecision.Visible;
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
                int retestPeriod = GetOccludedRetestPeriodFrames();
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
                queryState.QueryIssuedFrameId = frameId;

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

                // Only end the GL query if BeginQuery actually started it this frame.
                // (QueryIssuedFrameId is set inside BeginQuery; if it matches current
                // frame this BeginQuery owned the GL query; otherwise this call is a
                // no-op for a second pass that wasn't the one to begin it.)
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
                if (queryState.QueryIssuedFrameId != frameId)
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
                LastTouchedFrame = RuntimeEngine.Rendering.State.RenderFrameId,
                LastAnySamplesPassed = true,
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
            Vector3 position = camera.Transform.RenderTranslation;
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
                queryState.ResetSeedProbe = true;
            }
        }

        /// <summary>
        /// Begins a multi-frame reset window after a significant camera change. Instead
        /// of resetting every command's visibility state on the same frame (which would
        /// translate to a frame-spike of full-mesh draws as every cached "occluded" flips
        /// back to "visible"), we mark a window of <see cref="ResetStripeFrames"/> frames.
        /// Each frame inside the window re-seeds one stripe of commands selected by
        /// <c>StableQueryKey % ResetStripeFrames</c>, spreading the seed cost.
        /// </summary>
        private static void BeginStaggeredReset(PassState state)
        {
            state.ResetActive = true;
            state.ResetStartFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        }

        /// <summary>
        /// Applied at the start of each frame while a reset window is active. Re-seeds
        /// the stripe of commands whose key matches this frame's stripe index. Each
        /// re-seeded command will then return ProbeOnly on its next decision (so the
        /// visible-cost cliff becomes a multi-frame depth-only AABB sweep instead).
        /// </summary>
        private static void ApplyResetStripe(PassState state)
        {
            ulong currentFrame = RuntimeEngine.Rendering.State.RenderFrameId;
            ulong elapsed = currentFrame - state.ResetStartFrameId;
            if (elapsed >= (ulong)ResetStripeFrames)
            {
                state.ResetActive = false;
                return;
            }

            uint stripe = (uint)elapsed;
            foreach (var (key, queryState) in state.Queries)
            {
                if ((key % (uint)ResetStripeFrames) != stripe)
                    continue;

                queryState.ConsecutiveOccludedFrames = 0;
                queryState.LastAnySamplesPassed = false;
                queryState.ResetSeedProbe = true;
                // Force a fresh decision call this frame even if the cached frame id
                // matches (the cached decision predates the reset).
                queryState.LastDecidedFrameId = 0;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// CPU-render-path async hardware-query occlusion coordinator.
    ///
    /// Contract:
    /// - Draw predicted-visible meshes normally first.
    /// - Submit depth-only AABB proxy queries after opaque pass depth is complete.
    /// - Resolve only available previous-frame query results.
    /// - Preserve history across normal camera and VR head-pose motion.
    /// - Force visible, with telemetry, for camera cuts and unsupported shared-stereo query scopes.
    /// </summary>
    public sealed class CpuRenderOcclusionCoordinator
    {
        private sealed class QueryState
        {
            public XRRenderQuery Query = null!;
            public ECpuOcclusionQueryStateKind StateKind = ECpuOcclusionQueryStateKind.Unknown;
            public bool LastAnySamplesPassed = true;
            public int ConsecutiveVisibleFrames;
            public int ConsecutiveOccludedFrames;
            public ulong LastTouchedFrame;
            public ulong LastVisibleFrame;
            public ulong LastOccludedFrame;
            public ulong LastQueryFrame;
            public ulong PendingSinceFrame;
            public bool QueryPending;
            public bool DiscardPendingResult;
            public bool PendingQueryWasVisibleDraw;
            public ECpuOcclusionQueryReason PendingReason;

            public ulong LastDecidedFrameId = ulong.MaxValue;
            public ECpuOcclusionDecision LastDecision = ECpuOcclusionDecision.Visible;
            public CpuOcclusionProbeRequest LastProbeRequest = CpuOcclusionProbeRequest.None;
            public ulong QueryIssuedFrameId = ulong.MaxValue;
        }

        private sealed class HierarchyGroupState
        {
            public QueryState Query = new();
            public bool LastAnySamplesPassed = true;
            public ulong LastQueryFrame;
            public ulong LastVisibleFrame;
            public ulong LastOccludedFrame;
            public ulong ScheduledFrameId = ulong.MaxValue;
            public int ChildProbeCount;
        }

        private struct HierarchyScratch
        {
            public AABB Bounds;
            public float Priority;
            public int Count;
            public bool HasBounds;
        }

        private sealed class PassState
        {
            public readonly Dictionary<uint, QueryState> Queries = new();
            public readonly Dictionary<uint, HierarchyGroupState> HierarchyGroups = new();
            public readonly Dictionary<uint, HierarchyScratch> HierarchyScratch = new();
            public OcclusionViewKey ViewKey;
            public Vector3 LastCameraPosition;
            public Vector3 LastCameraForward;
            public Vector3 LastCameraUp;
            public Matrix4x4 LastProjection;
            public bool HasCameraState;
            public ECpuOcclusionMotionTier MotionTier = ECpuOcclusionMotionTier.Stable;
            public bool ForceVisibleThisFrame;
            public ECpuOcclusionForceVisibleReason ForceVisibleReason;
            public ulong LastResolvedFrameId;
            public ulong LastBudgetFrameId = ulong.MaxValue;
            public int VisibleBudgetUsed;
            public int RecoveryBudgetUsed;
            public uint LastSceneCommandCount;
        }

        private readonly object _lock = new();
        private readonly Dictionary<OcclusionViewKey, PassState> _passStates = new();
        private readonly AsyncOcclusionQueryManager _queryManager = new();

        private const int VisibleDemotionZeroConfidenceFrames = 2;
        private const int DefaultOccludedRetestPeriodFrames = 6;
        private const int StaleEvictionFrames = 120;
        private const int HierarchyGroupShift = 5;
        private const int HierarchyMinChildren = 4;
        private const ulong VulkanQueryResolveMinLatencyFrames = 2UL;
        private const float CameraMotionEpsilon = 0.0001f;
        private const float ProjectionDeltaThreshold = 0.001f;
        private const float ProjectionCutDeltaThreshold = 0.125f;

        private static int GetOccludedRetestPeriodFrames()
        {
            int period = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRetestPeriodFrames;
            return period > 0 ? period : DefaultOccludedRetestPeriodFrames;
        }

        public bool BeginPass(int renderPass, XRCamera camera, uint sceneCommandCount)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass, camera);
                state.LastSceneCommandCount = sceneCommandCount;
                state.ViewKey = CreatePassKey(renderPass, camera);
                state.ForceVisibleThisFrame = false;
                state.ForceVisibleReason = ECpuOcclusionForceVisibleReason.None;

                ECpuOcclusionMotionTier tier = UpdateCameraVisibilityState(state, camera);
                state.MotionTier = tier;

                if (tier == ECpuOcclusionMotionTier.CameraCut)
                    ForceVisibleForPass(state, ECpuOcclusionForceVisibleReason.CameraCut);
                else if (!IsHardwareQueryBackendSupported())
                    ForceVisibleForPass(state, ECpuOcclusionForceVisibleReason.UnsupportedBackend);
                else if (IsUnsupportedSharedStereoScope(state.ViewKey))
                    ForceVisibleForPass(state, ECpuOcclusionForceVisibleReason.UnsupportedStereoMode);

                ResolveAvailableResults(state);
                OcclusionTelemetry.RecordCpuMotionTier(state.MotionTier);
                OcclusionTelemetry.RecordCpuActiveViewScope(state.ViewKey.Scope);
                if (state.ForceVisibleThisFrame)
                    OcclusionTelemetry.RecordCpuGlobalConservativeFrame(state.ForceVisibleReason);

                return state.MotionTier == ECpuOcclusionMotionTier.CameraCut;
            }
        }

        public bool ShouldRender(int renderPass, uint sourceCommandIndex)
        {
            ECpuOcclusionDecision decision = ShouldRender(
                renderPass,
                null,
                sourceCommandIndex,
                out CpuOcclusionProbeRequest _);
            return decision != ECpuOcclusionDecision.Skip;
        }

        public ECpuOcclusionDecision ShouldRender(int renderPass, uint sourceCommandIndex, out bool needsHardwareQuery)
            => ShouldRender(renderPass, null, sourceCommandIndex, out needsHardwareQuery);

        public ECpuOcclusionDecision ShouldRender(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            out bool needsHardwareQuery)
        {
            ECpuOcclusionDecision decision = ShouldRender(
                renderPass,
                camera,
                sourceCommandIndex,
                out CpuOcclusionProbeRequest request);
            needsHardwareQuery = request.Requested;
            return decision;
        }

        public ECpuOcclusionDecision ShouldRender(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            out CpuOcclusionProbeRequest probeRequest)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass, camera);
                QueryState queryState = GetOrCreateQueryState(state, sourceCommandIndex);
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;

                if (queryState.LastDecidedFrameId == frameId)
                {
                    probeRequest = queryState.LastProbeRequest;
                    if (probeRequest.Requested &&
                        (queryState.QueryIssuedFrameId == frameId || queryState.QueryPending))
                    {
                        probeRequest = CpuOcclusionProbeRequest.None;
                    }

                    OcclusionTelemetry.RecordCpuDecision(ECpuDecisionKind.Cached);
                    return queryState.LastDecision;
                }

                queryState.LastTouchedFrame = frameId;
                queryState.LastDecidedFrameId = frameId;

                if (state.ForceVisibleThisFrame)
                    return SetDecision(queryState, ECpuOcclusionDecision.Visible, CpuOcclusionProbeRequest.None, ECpuDecisionKind.ForcedVisible, out probeRequest);

                if (ExpireOverduePendingQuery(queryState, frameId))
                    return SetDecision(queryState, ECpuOcclusionDecision.Visible, CpuOcclusionProbeRequest.None, ECpuDecisionKind.ForcedVisible, out probeRequest);

                if (queryState.QueryPending)
                {
                    if (queryState.PendingQueryWasVisibleDraw ||
                        queryState.StateKind == ECpuOcclusionQueryStateKind.PendingVisibleProbe)
                    {
                        return SetDecision(queryState, ECpuOcclusionDecision.Visible, CpuOcclusionProbeRequest.None, ECpuDecisionKind.VisibleHysteresis, out probeRequest);
                    }

                    return SetDecision(queryState, ECpuOcclusionDecision.Skip, CpuOcclusionProbeRequest.None, ECpuDecisionKind.Skip, out probeRequest);
                }

                if (queryState.StateKind == ECpuOcclusionQueryStateKind.Unknown)
                {
                    var request = new CpuOcclusionProbeRequest(true, ECpuOcclusionQueryReason.InitialSeed, recoveryProbe: false, priorityBias: 2.0f);
                    return SetDecision(queryState, ECpuOcclusionDecision.Visible, request, ECpuDecisionKind.Seed, out probeRequest);
                }

                if (queryState.LastAnySamplesPassed)
                {
                    queryState.StateKind = ECpuOcclusionQueryStateKind.PredictedVisible;
                    queryState.ConsecutiveOccludedFrames = 0;
                    queryState.ConsecutiveVisibleFrames++;

                    CpuOcclusionProbeRequest request = ShouldRefreshVisibleProbe(queryState, frameId, state.MotionTier)
                        ? new CpuOcclusionProbeRequest(
                            true,
                            state.MotionTier == ECpuOcclusionMotionTier.Stable
                                ? ECpuOcclusionQueryReason.VisibleDemotion
                                : ECpuOcclusionQueryReason.CameraMotionRevalidation,
                            recoveryProbe: false,
                            priorityBias: GetMotionPriorityBias(state.MotionTier))
                        : CpuOcclusionProbeRequest.None;

                    return SetDecision(queryState, ECpuOcclusionDecision.Visible, request, ECpuDecisionKind.VisibleQuery, out probeRequest);
                }

                queryState.StateKind = ECpuOcclusionQueryStateKind.PredictedOccluded;

                if (IsHierarchyFreshOccluded(state, sourceCommandIndex, frameId))
                    return SetDecision(queryState, ECpuOcclusionDecision.Skip, CpuOcclusionProbeRequest.None, ECpuDecisionKind.Skip, out probeRequest);

                if (queryState.ConsecutiveOccludedFrames < VisibleDemotionZeroConfidenceFrames)
                {
                    queryState.ConsecutiveOccludedFrames++;
                    var request = new CpuOcclusionProbeRequest(true, ECpuOcclusionQueryReason.VisibleDemotion, recoveryProbe: false, priorityBias: 1.0f);
                    return SetDecision(queryState, ECpuOcclusionDecision.Visible, request, ECpuDecisionKind.VisibleHysteresis, out probeRequest);
                }

                if (ShouldForceVisibleForStaleOcclusion(queryState, frameId, state.MotionTier))
                {
                    var request = new CpuOcclusionProbeRequest(true, ECpuOcclusionQueryReason.CameraMotionRevalidation, recoveryProbe: false, priorityBias: 4.0f);
                    return SetDecision(queryState, ECpuOcclusionDecision.Visible, request, ECpuDecisionKind.VisibleHysteresis, out probeRequest);
                }

                if (ShouldScheduleRecoveryProbe(queryState, sourceCommandIndex, frameId, state.MotionTier))
                {
                    var request = new CpuOcclusionProbeRequest(
                        true,
                        state.MotionTier == ECpuOcclusionMotionTier.Stable
                            ? ECpuOcclusionQueryReason.OccludedRecovery
                            : ECpuOcclusionQueryReason.CameraMotionRevalidation,
                        recoveryProbe: true,
                        priorityBias: GetRecoveryPriorityBias(queryState, frameId, state.MotionTier));
                    return SetDecision(queryState, ECpuOcclusionDecision.ProbeOnly, request, ECpuDecisionKind.Probe, out probeRequest);
                }

                return SetDecision(queryState, ECpuOcclusionDecision.Skip, CpuOcclusionProbeRequest.None, ECpuDecisionKind.Skip, out probeRequest);
            }
        }

        public bool PeekShouldRender(int renderPass, uint sourceCommandIndex)
            => PeekShouldRender(renderPass, null, sourceCommandIndex);

        public bool PeekShouldRender(int renderPass, XRCamera? camera, uint sourceCommandIndex)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass, camera);
                if (state.ForceVisibleThisFrame)
                    return true;
                if (!state.Queries.TryGetValue(sourceCommandIndex, out QueryState? queryState))
                    return true;
                if (queryState.LastAnySamplesPassed)
                    return true;
                if (queryState.QueryPending && queryState.PendingQueryWasVisibleDraw)
                    return true;
                if (queryState.ConsecutiveOccludedFrames < VisibleDemotionZeroConfidenceFrames)
                    return true;

                return false;
            }
        }

        public void SelectProbeCandidates(
            int renderPass,
            XRCamera? camera,
            List<CpuOcclusionProbeCandidate> candidates,
            List<CpuOcclusionScheduledProbe> scheduled)
        {
            if (scheduled.Count != 0)
                scheduled.Clear();
            if (candidates.Count == 0)
                return;

            lock (_lock)
            {
                PassState state = GetPassState(renderPass, camera);
                if (state.ForceVisibleThisFrame)
                {
                    OcclusionTelemetry.RecordCpuBudgetSkipped(ECpuOcclusionQueryReason.DiagnosticForcedQuery, candidates.Count);
                    return;
                }

                RefreshBudgets(state);
                int maxQueries = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMaxQueriesPerFrame;
                if (maxQueries <= 0)
                {
                    OcclusionTelemetry.RecordCpuBudgetSkipped(ECpuOcclusionQueryReason.DiagnosticForcedQuery, candidates.Count);
                    return;
                }

                int pendingQueries = CountPendingQueries(state);
                OcclusionTelemetry.RecordCpuPendingQueries(pendingQueries);
                int available = Math.Max(0, maxQueries - pendingQueries);
                if (available == 0)
                {
                    OcclusionTelemetry.RecordCpuBudgetSkipped(ECpuOcclusionQueryReason.StaleStateRefresh, candidates.Count);
                    return;
                }

                ComputeBudgets(state.MotionTier, maxQueries, out int visibleBudget, out int recoveryBudget);
                int visibleRemaining = Math.Max(0, visibleBudget - state.VisibleBudgetUsed);
                int recoveryRemaining = Math.Max(0, recoveryBudget - state.RecoveryBudgetUsed);
                if (visibleRemaining + recoveryRemaining > available)
                {
                    int over = visibleRemaining + recoveryRemaining - available;
                    int visibleReduction = Math.Min(visibleRemaining, over);
                    visibleRemaining -= visibleReduction;
                    over -= visibleReduction;
                    recoveryRemaining = Math.Max(0, recoveryRemaining - over);
                }

                candidates.Sort(CompareProbeCandidates);
                ScheduleHierarchyProbes(state, candidates, scheduled, ref recoveryRemaining);
                ScheduleIndividualProbes(state, candidates, scheduled, true, ref recoveryRemaining);
                ScheduleIndividualProbes(state, candidates, scheduled, false, ref visibleRemaining);
            }
        }

        public void BeginQuery(int renderPass, uint sourceCommandIndex)
            => BeginQuery(renderPass, null, sourceCommandIndex);

        public void BeginQuery(int renderPass, XRCamera? camera, uint sourceCommandIndex)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass, camera);
                QueryState queryState = GetOrCreateQueryState(state, sourceCommandIndex);
                BeginQueryCore(queryState);
            }
        }

        public void EndQuery(int renderPass, uint sourceCommandIndex)
            => EndQuery(renderPass, null, sourceCommandIndex);

        public void EndQuery(int renderPass, XRCamera? camera, uint sourceCommandIndex)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass, camera);
                if (!state.Queries.TryGetValue(sourceCommandIndex, out QueryState? queryState))
                    return;

                EndQueryCore(queryState, queryState.LastProbeRequest);
            }
        }

        public void BeginHierarchyQuery(int renderPass, XRCamera? camera, uint hierarchyGroupKey)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass, camera);
                HierarchyGroupState group = GetOrCreateHierarchyGroup(state, hierarchyGroupKey);
                BeginQueryCore(group.Query);
            }
        }

        public void EndHierarchyQuery(int renderPass, XRCamera? camera, uint hierarchyGroupKey)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass, camera);
                if (!state.HierarchyGroups.TryGetValue(hierarchyGroupKey, out HierarchyGroupState? group))
                    return;

                EndQueryCore(group.Query, new CpuOcclusionProbeRequest(true, ECpuOcclusionQueryReason.OccludedRecovery, recoveryProbe: true));
            }
        }

        public void ForceVisible(int renderPass, uint sourceCommandIndex)
            => ForceVisible(renderPass, null, sourceCommandIndex);

        public void ForceVisible(int renderPass, XRCamera? camera, uint sourceCommandIndex)
            => ForceVisible(renderPass, camera, sourceCommandIndex, ECpuOcclusionForceVisibleReason.Diagnostic);

        public void ForceVisible(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            ECpuOcclusionForceVisibleReason reason)
        {
            lock (_lock)
            {
                PassState state = GetPassState(renderPass, camera);
                QueryState queryState = GetOrCreateQueryState(state, sourceCommandIndex);
                ForceQueryStateVisible(queryState, RuntimeEngine.Rendering.State.RenderFrameId);
                OcclusionTelemetry.RecordCpuForcedVisible(reason);
            }
        }

        private static ECpuOcclusionDecision SetDecision(
            QueryState queryState,
            ECpuOcclusionDecision decision,
            CpuOcclusionProbeRequest request,
            ECpuDecisionKind telemetryKind,
            out CpuOcclusionProbeRequest probeRequest)
        {
            queryState.LastDecision = decision;
            queryState.LastProbeRequest = request;
            probeRequest = request;
            OcclusionTelemetry.RecordCpuDecision(telemetryKind);
            return decision;
        }

        private PassState GetPassState(int renderPass, XRCamera? camera)
        {
            OcclusionViewKey passKey = CreatePassKey(renderPass, camera);
            if (!_passStates.TryGetValue(passKey, out PassState? state))
            {
                state = new PassState { ViewKey = passKey };
                _passStates.Add(passKey, state);
            }

            return state;
        }

        internal static OcclusionViewKey CreatePassKey(int renderPass, XRCamera? camera)
        {
            IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
            IRuntimeRenderCommandExecutionState? renderState = host.ActiveRenderCommandExecutionState;
            bool stereoPass = renderState?.StereoPass == true || RuntimeEngine.Rendering.State.IsStereoPass;
            bool eyeCamera = camera?.StereoEyeLeft.HasValue == true;

            if (stereoPass)
            {
                if (host.EnableVrFoveatedViewSet)
                    return new OcclusionViewKey(renderPass, EOcclusionViewScope.VrFoveatedView);
                if (host.VrViewRenderMode == EVrViewRenderMode.SinglePassStereo)
                    return new OcclusionViewKey(renderPass, EOcclusionViewScope.VrSinglePassStereo);
                return new OcclusionViewKey(renderPass, EOcclusionViewScope.VrStereoPair);
            }

            if (eyeCamera)
            {
                if (RuntimeEngine.EffectiveSettings.CpuQueryOcclusionStereoMode == ECpuQueryStereoMode.StereoPairShared)
                    return new OcclusionViewKey(renderPass, EOcclusionViewScope.VrStereoPair);

                bool left = camera!.StereoEyeLeft.GetValueOrDefault();
                return new OcclusionViewKey(renderPass, left ? EOcclusionViewScope.VrLeftEye : EOcclusionViewScope.VrRightEye, left ? 0 : 1);
            }

            if (host.IsInVR)
            {
                if (host.VrMirrorComposeFromEyeTextures)
                    return new OcclusionViewKey(renderPass, EOcclusionViewScope.MirrorOnly);
                if (host.RenderWindowsWhileInVR)
                    return new OcclusionViewKey(renderPass, EOcclusionViewScope.EditorDesktopWhileVr);
            }

            return new OcclusionViewKey(renderPass, EOcclusionViewScope.MonoDesktop);
        }

        private static bool IsUnsupportedSharedStereoScope(OcclusionViewKey key)
            => key.IsSharedStereoScope &&
               RuntimeEngine.EffectiveSettings.CpuQueryOcclusionStereoMode != ECpuQueryStereoMode.StereoPairShared;

        private static bool IsHardwareQueryBackendSupported()
            => AbstractRenderer.Current is OpenGLRenderer or VulkanRenderer;

        private QueryState GetOrCreateQueryState(PassState state, uint sourceCommandIndex)
        {
            if (state.Queries.TryGetValue(sourceCommandIndex, out QueryState? existing))
                return existing;

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            var created = new QueryState
            {
                Query = _queryManager.Acquire(EQueryTarget.AnySamplesPassedConservative),
                StateKind = ECpuOcclusionQueryStateKind.Unknown,
                LastTouchedFrame = frameId,
                LastVisibleFrame = frameId,
                LastQueryFrame = 0UL,
                LastAnySamplesPassed = true,
            };

            state.Queries[sourceCommandIndex] = created;
            return created;
        }

        private HierarchyGroupState GetOrCreateHierarchyGroup(PassState state, uint groupKey)
        {
            if (state.HierarchyGroups.TryGetValue(groupKey, out HierarchyGroupState? group))
            {
                if (group.Query.Query is null)
                    group.Query.Query = _queryManager.Acquire(EQueryTarget.AnySamplesPassedConservative);
                return group;
            }

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            group = new HierarchyGroupState
            {
                Query = new QueryState
                {
                    Query = _queryManager.Acquire(EQueryTarget.AnySamplesPassedConservative),
                    LastTouchedFrame = frameId,
                    LastVisibleFrame = frameId,
                    LastAnySamplesPassed = true,
                },
                LastVisibleFrame = frameId,
            };
            state.HierarchyGroups.Add(groupKey, group);
            return group;
        }

        private void BeginQueryCore(QueryState queryState)
        {
            if (queryState.Query is null)
                return;

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (queryState.QueryIssuedFrameId == frameId || queryState.QueryPending)
                return;

            if (BeginBackendQuery(queryState.Query))
                queryState.QueryIssuedFrameId = frameId;
        }

        private static void EndQueryCore(QueryState queryState, CpuOcclusionProbeRequest request)
        {
            if (queryState.Query is null)
                return;

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (queryState.QueryIssuedFrameId != frameId)
                return;

            if (!EndBackendQuery(queryState.Query))
                return;

            queryState.QueryPending = true;
            queryState.PendingSinceFrame = frameId;
            queryState.PendingReason = request.Reason;
            queryState.PendingQueryWasVisibleDraw = request.RecoveryProbe == false;
            queryState.StateKind = request.RecoveryProbe
                ? ECpuOcclusionQueryStateKind.PendingOccludedProbe
                : ECpuOcclusionQueryStateKind.PendingVisibleProbe;
            queryState.LastQueryFrame = frameId;
            OcclusionTelemetry.RecordCpuQuerySubmitted(request.Reason);
        }

        private static bool BeginBackendQuery(XRRenderQuery query)
        {
            if (AbstractRenderer.Current is OpenGLRenderer gl)
            {
                GLRenderQuery? glQuery = gl.GenericToAPI<GLRenderQuery>(query);
                if (glQuery is null)
                    return false;

                glQuery.BeginQuery(EQueryTarget.AnySamplesPassedConservative);
                return true;
            }

            if (AbstractRenderer.Current is VulkanRenderer vk)
                return vk.EnqueueOcclusionQueryBegin(query, EQueryTarget.AnySamplesPassedConservative);

            return false;
        }

        private static bool EndBackendQuery(XRRenderQuery query)
        {
            if (AbstractRenderer.Current is OpenGLRenderer gl)
            {
                GLRenderQuery? glQuery = gl.GenericToAPI<GLRenderQuery>(query);
                if (glQuery is null)
                    return false;

                glQuery.EndQuery();
                return true;
            }

            if (AbstractRenderer.Current is VulkanRenderer vk)
                return vk.EnqueueOcclusionQueryEnd(query);

            return false;
        }

        private void ResolveAvailableResults(PassState state)
        {
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (state.LastResolvedFrameId == frameId)
                return;

            state.LastResolvedFrameId = frameId;

            List<uint>? staleKeys = null;
            foreach (var (key, queryState) in state.Queries)
            {
                ResolveQueryState(queryState, frameId, out bool resolved, out bool anySamplesPassed);
                if (resolved)
                    ApplyResolvedCommandResult(state, key, queryState, frameId, anySamplesPassed);

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
                    if (state.Queries.Remove(key, out QueryState? removed) && removed.Query is not null)
                        _queryManager.Release(removed.Query);
                }
            }

            foreach (var (groupKey, group) in state.HierarchyGroups)
            {
                ResolveQueryState(group.Query, frameId, out bool resolved, out bool anySamplesPassed);
                if (!resolved)
                    continue;

                group.LastAnySamplesPassed = anySamplesPassed;
                group.LastQueryFrame = frameId;
                if (anySamplesPassed)
                    group.LastVisibleFrame = frameId;
                else
                    group.LastOccludedFrame = frameId;

                _ = groupKey;
            }
        }

        private void ResolveQueryState(QueryState queryState, ulong frameId, out bool resolved, out bool anySamplesPassed)
        {
            resolved = false;
            anySamplesPassed = true;
            if (!queryState.QueryPending || queryState.Query is null)
                return;

            if (ShouldDelayPendingQueryPoll(queryState, frameId))
                return;

            if (!_queryManager.TryGetAnySamplesPassed(queryState.Query, out anySamplesPassed))
                return;

            resolved = true;
            queryState.QueryPending = false;
            queryState.PendingQueryWasVisibleDraw = false;
            ulong latency = frameId >= queryState.PendingSinceFrame ? frameId - queryState.PendingSinceFrame : 0UL;
            OcclusionTelemetry.RecordCpuQueryResolved(queryState.PendingReason, latency);

            if (!queryState.DiscardPendingResult)
                ApplyResolvedQueryState(queryState, frameId, anySamplesPassed);

            queryState.DiscardPendingResult = false;
            queryState.PendingReason = ECpuOcclusionQueryReason.None;
        }

        private static bool ShouldDelayPendingQueryPoll(QueryState queryState, ulong frameId)
        {
            if (AbstractRenderer.Current is not VulkanRenderer)
                return false;

            ulong age = frameId >= queryState.PendingSinceFrame
                ? frameId - queryState.PendingSinceFrame
                : 0UL;
            return age < VulkanQueryResolveMinLatencyFrames;
        }

        private static void ApplyResolvedQueryState(QueryState queryState, ulong frameId, bool anySamplesPassed)
        {
            queryState.LastAnySamplesPassed = anySamplesPassed;
            if (anySamplesPassed)
            {
                queryState.StateKind = ECpuOcclusionQueryStateKind.PredictedVisible;
                queryState.ConsecutiveVisibleFrames++;
                queryState.ConsecutiveOccludedFrames = 0;
                queryState.LastVisibleFrame = frameId;
            }
            else
            {
                queryState.StateKind = ECpuOcclusionQueryStateKind.PredictedOccluded;
                queryState.ConsecutiveVisibleFrames = 0;
                queryState.ConsecutiveOccludedFrames++;
                queryState.LastOccludedFrame = frameId;
            }
        }

        private static void ApplyResolvedCommandResult(
            PassState state,
            uint sourceCommandIndex,
            QueryState queryState,
            ulong frameId,
            bool anySamplesPassed)
        {
            uint groupKey = GetHierarchyGroupKey(sourceCommandIndex);
            if (!state.HierarchyGroups.TryGetValue(groupKey, out HierarchyGroupState? group))
                return;

            group.ChildProbeCount++;
            if (anySamplesPassed)
            {
                group.LastAnySamplesPassed = true;
                group.LastVisibleFrame = frameId;
            }
            else if (queryState.ConsecutiveOccludedFrames >= VisibleDemotionZeroConfidenceFrames)
            {
                group.LastOccludedFrame = frameId;
            }
        }

        private bool ExpireOverduePendingQuery(QueryState queryState, ulong frameId)
        {
            if (!queryState.QueryPending)
                return false;

            int maxPendingFrames = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMaxPendingFrames;
            if (frameId - queryState.PendingSinceFrame <= (ulong)Math.Max(1, maxPendingFrames))
                return false;

            queryState.QueryPending = false;
            queryState.DiscardPendingResult = true;
            ForceQueryStateVisible(queryState, frameId);
            OcclusionTelemetry.RecordCpuForcedVisible(ECpuOcclusionForceVisibleReason.PendingTooOld);
            return true;
        }

        private static void ForceQueryStateVisible(QueryState queryState, ulong frameId)
        {
            queryState.StateKind = ECpuOcclusionQueryStateKind.ForcedVisible;
            queryState.ConsecutiveOccludedFrames = 0;
            queryState.ConsecutiveVisibleFrames++;
            queryState.LastTouchedFrame = frameId;
            queryState.LastVisibleFrame = frameId;
            queryState.LastAnySamplesPassed = true;
            queryState.LastDecision = ECpuOcclusionDecision.Visible;
            queryState.LastProbeRequest = CpuOcclusionProbeRequest.None;
            queryState.LastDecidedFrameId = frameId;
            queryState.QueryIssuedFrameId = ulong.MaxValue;
            queryState.PendingQueryWasVisibleDraw = false;

            if (queryState.QueryPending)
                queryState.DiscardPendingResult = true;
        }

        private static bool ShouldRefreshVisibleProbe(QueryState queryState, ulong frameId, ECpuOcclusionMotionTier motionTier)
        {
            int period = GetOccludedRetestPeriodFrames();
            int refresh = motionTier switch
            {
                ECpuOcclusionMotionTier.Stable => period * 2,
                ECpuOcclusionMotionTier.SmallMotion => period,
                ECpuOcclusionMotionTier.MediumMotion => Math.Max(2, period / 2),
                ECpuOcclusionMotionTier.LargeMotion => Math.Max(1, RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRecoveryMinCadenceFrames),
                ECpuOcclusionMotionTier.VrHeadPoseMotion => Math.Max(2, period / 2),
                _ => period,
            };

            return queryState.LastQueryFrame == 0UL || frameId - queryState.LastQueryFrame >= (ulong)Math.Max(1, refresh);
        }

        private static bool ShouldScheduleRecoveryProbe(
            QueryState queryState,
            uint sourceCommandIndex,
            ulong frameId,
            ECpuOcclusionMotionTier motionTier)
        {
            int cadence = GetRecoveryCadence(motionTier);
            if (queryState.LastQueryFrame != 0UL && frameId - queryState.LastQueryFrame < (ulong)cadence)
                return false;

            int stagger = Math.Max(1, cadence);
            return ((frameId + sourceCommandIndex) % (ulong)stagger) == 0UL || motionTier != ECpuOcclusionMotionTier.Stable;
        }

        private static bool ShouldForceVisibleForStaleOcclusion(QueryState queryState, ulong frameId, ECpuOcclusionMotionTier motionTier)
        {
            if (motionTier is ECpuOcclusionMotionTier.Stable or ECpuOcclusionMotionTier.SmallMotion or ECpuOcclusionMotionTier.VrHeadPoseMotion)
                return false;

            ulong age = queryState.LastQueryFrame == 0UL ? ulong.MaxValue : frameId - queryState.LastQueryFrame;
            ulong maxAge = motionTier == ECpuOcclusionMotionTier.LargeMotion ? 2UL : 4UL;
            return age > maxAge;
        }

        private static int GetRecoveryCadence(ECpuOcclusionMotionTier motionTier)
        {
            int period = GetOccludedRetestPeriodFrames();
            int minCadence = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRecoveryMinCadenceFrames;
            return motionTier switch
            {
                ECpuOcclusionMotionTier.Stable => Math.Max(minCadence, period),
                ECpuOcclusionMotionTier.SmallMotion => Math.Max(minCadence, (period + 1) / 2),
                ECpuOcclusionMotionTier.MediumMotion => minCadence,
                ECpuOcclusionMotionTier.LargeMotion => 1,
                ECpuOcclusionMotionTier.VrHeadPoseMotion => minCadence,
                _ => period,
            };
        }

        private static float GetMotionPriorityBias(ECpuOcclusionMotionTier tier)
            => tier switch
            {
                ECpuOcclusionMotionTier.Stable => 0.0f,
                ECpuOcclusionMotionTier.SmallMotion => 0.5f,
                ECpuOcclusionMotionTier.MediumMotion => 1.0f,
                ECpuOcclusionMotionTier.LargeMotion => 2.0f,
                ECpuOcclusionMotionTier.VrHeadPoseMotion => 1.25f,
                _ => 0.0f,
            };

        private static float GetRecoveryPriorityBias(QueryState queryState, ulong frameId, ECpuOcclusionMotionTier tier)
        {
            float age = queryState.LastVisibleFrame == 0UL ? 0.0f : Math.Min(60.0f, frameId - queryState.LastVisibleFrame);
            return GetMotionPriorityBias(tier) + age * 0.05f;
        }

        private static void RefreshBudgets(PassState state)
        {
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (state.LastBudgetFrameId == frameId)
                return;

            state.LastBudgetFrameId = frameId;
            state.VisibleBudgetUsed = 0;
            state.RecoveryBudgetUsed = 0;
        }

        private static void ComputeBudgets(ECpuOcclusionMotionTier tier, int maxQueries, out int visibleBudget, out int recoveryBudget)
        {
            float visibleFraction = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionVisibleDemotionBudgetFraction;
            visibleBudget = Math.Clamp((int)MathF.Round(maxQueries * visibleFraction), 0, maxQueries);

            if (tier is ECpuOcclusionMotionTier.MediumMotion or ECpuOcclusionMotionTier.LargeMotion or ECpuOcclusionMotionTier.VrHeadPoseMotion)
                visibleBudget = Math.Min(visibleBudget, Math.Max(1, maxQueries / 8));

            recoveryBudget = Math.Max(0, maxQueries - visibleBudget);
        }

        private static int CountPendingQueries(PassState state)
        {
            int pending = 0;
            foreach (QueryState query in state.Queries.Values)
            {
                if (query.QueryPending)
                    pending++;
            }

            foreach (HierarchyGroupState group in state.HierarchyGroups.Values)
            {
                if (group.Query.QueryPending)
                    pending++;
            }

            return pending;
        }

        private static int CompareProbeCandidates(CpuOcclusionProbeCandidate x, CpuOcclusionProbeCandidate y)
        {
            int result = y.ScreenPriority.CompareTo(x.ScreenPriority);
            if (result != 0)
                return result;

            result = x.DistanceMeters.CompareTo(y.DistanceMeters);
            if (result != 0)
                return result;

            return x.QueryKey.CompareTo(y.QueryKey);
        }

        private static void ScheduleHierarchyProbes(
            PassState state,
            List<CpuOcclusionProbeCandidate> candidates,
            List<CpuOcclusionScheduledProbe> scheduled,
            ref int recoveryRemaining)
        {
            if (recoveryRemaining <= 0)
                return;

            state.HierarchyScratch.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                CpuOcclusionProbeCandidate candidate = candidates[i];
                if (!candidate.Request.RecoveryProbe)
                    continue;

                uint groupKey = GetHierarchyGroupKey(candidate.QueryKey);
                ref HierarchyScratch scratch = ref CollectionsMarshal.GetValueRefOrAddDefault(
                    state.HierarchyScratch,
                    groupKey,
                    out bool exists);
                if (!exists || !scratch.HasBounds)
                {
                    scratch.Bounds = candidate.WorldBounds;
                    scratch.Priority = candidate.ScreenPriority;
                    scratch.Count = 1;
                    scratch.HasBounds = true;
                }
                else
                {
                    scratch.Bounds = AABB.Union(scratch.Bounds, candidate.WorldBounds);
                    scratch.Priority = MathF.Max(scratch.Priority, candidate.ScreenPriority);
                    scratch.Count++;
                }
            }

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            foreach (var (groupKey, scratch) in state.HierarchyScratch)
            {
                if (recoveryRemaining <= 0)
                    break;
                if (scratch.Count < HierarchyMinChildren)
                    continue;

                HierarchyGroupState group = GetOrCreateHierarchyGroupStatic(state, groupKey);
                if (group.Query.QueryPending || group.Query.QueryIssuedFrameId == frameId)
                    continue;
                if (group.ScheduledFrameId == frameId)
                    continue;
                if (IsHierarchyFreshOccluded(group, frameId, state.MotionTier))
                    continue;

                group.ScheduledFrameId = frameId;
                scheduled.Add(new CpuOcclusionScheduledProbe(0u, scratch.Bounds, isHierarchyGroup: true, groupKey));
                state.RecoveryBudgetUsed++;
                recoveryRemaining--;
            }
        }

        private static void ScheduleIndividualProbes(
            PassState state,
            List<CpuOcclusionProbeCandidate> candidates,
            List<CpuOcclusionScheduledProbe> scheduled,
            bool recovery,
            ref int remaining)
        {
            if (remaining <= 0)
                return;

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            int skipped = 0;
            for (int i = 0; i < candidates.Count && remaining > 0; i++)
            {
                CpuOcclusionProbeCandidate candidate = candidates[i];
                if (candidate.Request.RecoveryProbe != recovery)
                    continue;

                uint groupKey = GetHierarchyGroupKey(candidate.QueryKey);
                if (recovery &&
                    state.HierarchyGroups.TryGetValue(groupKey, out HierarchyGroupState? group) &&
                    (group.ScheduledFrameId == frameId || IsHierarchyFreshOccluded(group, frameId, state.MotionTier)))
                {
                    skipped++;
                    continue;
                }

                if (!state.Queries.TryGetValue(candidate.QueryKey, out QueryState? queryState))
                {
                    skipped++;
                    continue;
                }

                if (queryState.QueryPending || queryState.QueryIssuedFrameId == frameId)
                {
                    skipped++;
                    continue;
                }

                scheduled.Add(new CpuOcclusionScheduledProbe(candidate.QueryKey, candidate.WorldBounds, isHierarchyGroup: false, groupKey));
                if (recovery)
                    state.RecoveryBudgetUsed++;
                else
                    state.VisibleBudgetUsed++;
                remaining--;
            }

            if (skipped > 0)
            {
                OcclusionTelemetry.RecordCpuBudgetSkipped(
                    recovery ? ECpuOcclusionQueryReason.OccludedRecovery : ECpuOcclusionQueryReason.VisibleDemotion,
                    skipped);
            }
        }

        private bool IsHierarchyFreshOccluded(PassState state, uint sourceCommandIndex, ulong frameId)
        {
            uint groupKey = GetHierarchyGroupKey(sourceCommandIndex);
            return state.HierarchyGroups.TryGetValue(groupKey, out HierarchyGroupState? group) &&
                IsHierarchyFreshOccluded(group, frameId, state.MotionTier);
        }

        private static bool IsHierarchyFreshOccluded(HierarchyGroupState group, ulong frameId, ECpuOcclusionMotionTier tier)
        {
            if (group.LastAnySamplesPassed || group.LastQueryFrame == 0UL)
                return false;

            ulong freshness = tier switch
            {
                ECpuOcclusionMotionTier.Stable => (ulong)Math.Max(1, GetOccludedRetestPeriodFrames()),
                ECpuOcclusionMotionTier.SmallMotion => (ulong)Math.Max(1, GetOccludedRetestPeriodFrames() / 2),
                ECpuOcclusionMotionTier.VrHeadPoseMotion => (ulong)Math.Max(1, RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRecoveryMinCadenceFrames),
                _ => 1UL,
            };
            return frameId - group.LastQueryFrame <= freshness;
        }

        private static HierarchyGroupState GetOrCreateHierarchyGroupStatic(PassState state, uint groupKey)
        {
            if (state.HierarchyGroups.TryGetValue(groupKey, out HierarchyGroupState? group))
                return group;

            group = new HierarchyGroupState();
            state.HierarchyGroups.Add(groupKey, group);
            return group;
        }

        private static uint GetHierarchyGroupKey(uint sourceCommandIndex)
            => sourceCommandIndex >> HierarchyGroupShift;

        private static void ForceVisibleForPass(PassState state, ECpuOcclusionForceVisibleReason reason)
        {
            state.ForceVisibleThisFrame = true;
            state.ForceVisibleReason = reason;
            OcclusionTelemetry.RecordCpuForcedVisible(reason);

            if (reason == ECpuOcclusionForceVisibleReason.UnsupportedStereoMode)
                OcclusionTelemetry.RecordCpuUnsupportedStereoQueryMode();

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            foreach (QueryState queryState in state.Queries.Values)
            {
                queryState.StateKind = ECpuOcclusionQueryStateKind.ForcedVisible;
                queryState.LastDecidedFrameId = ulong.MaxValue;
                queryState.QueryIssuedFrameId = ulong.MaxValue;
                queryState.LastProbeRequest = CpuOcclusionProbeRequest.None;
                if (queryState.QueryPending)
                    queryState.DiscardPendingResult = true;
                queryState.LastTouchedFrame = frameId;
            }
        }

        private static ECpuOcclusionMotionTier UpdateCameraVisibilityState(PassState state, XRCamera camera)
        {
            Vector3 position = camera.Transform.RenderTranslation;
            Vector3 forward = SafeNormalize(camera.Transform.RenderForward, Vector3.UnitZ);
            Vector3 up = SafeNormalize(camera.Transform.RenderUp, Vector3.UnitY);
            Matrix4x4 projection = camera.ProjectionMatrixUnjittered;

            if (!state.HasCameraState)
            {
                state.HasCameraState = true;
                state.LastCameraPosition = position;
                state.LastCameraForward = forward;
                state.LastCameraUp = up;
                state.LastProjection = projection;
                return ECpuOcclusionMotionTier.Stable;
            }

            float distance = Vector3.Distance(state.LastCameraPosition, position);
            float forwardAngle = DotToDegrees(Vector3.Dot(state.LastCameraForward, forward));
            float upAngle = DotToDegrees(Vector3.Dot(state.LastCameraUp, up));
            float angle = MathF.Max(forwardAngle, upAngle);
            float projectionDelta =
                MathF.Abs(state.LastProjection.M11 - projection.M11) +
                MathF.Abs(state.LastProjection.M22 - projection.M22) +
                MathF.Abs(state.LastProjection.M31 - projection.M31) +
                MathF.Abs(state.LastProjection.M32 - projection.M32) +
                MathF.Abs(state.LastProjection.M33 - projection.M33) +
                MathF.Abs(state.LastProjection.M43 - projection.M43);

            state.LastCameraPosition = position;
            state.LastCameraForward = forward;
            state.LastCameraUp = up;
            state.LastProjection = projection;

            if (distance <= CameraMotionEpsilon && angle <= 0.001f && projectionDelta <= ProjectionDeltaThreshold)
                return ECpuOcclusionMotionTier.Stable;

            bool vrScope = state.ViewKey.Scope is EOcclusionViewScope.VrLeftEye
                or EOcclusionViewScope.VrRightEye
                or EOcclusionViewScope.VrStereoPair
                or EOcclusionViewScope.VrSinglePassStereo
                or EOcclusionViewScope.VrFoveatedView;
            if (vrScope &&
                distance <= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionVrHeadMotionMeters &&
                angle <= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionVrHeadRotationDegrees &&
                projectionDelta <= ProjectionCutDeltaThreshold)
            {
                return ECpuOcclusionMotionTier.VrHeadPoseMotion;
            }

            if (distance >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionCameraCutMeters ||
                angle >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionCameraCutRotationDegrees ||
                projectionDelta > ProjectionCutDeltaThreshold)
            {
                return ECpuOcclusionMotionTier.CameraCut;
            }

            if (distance >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionLargeMotionMeters ||
                angle >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionLargeRotationDegrees)
            {
                return ECpuOcclusionMotionTier.LargeMotion;
            }

            if (distance >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMediumMotionMeters ||
                angle >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMediumRotationDegrees)
            {
                return ECpuOcclusionMotionTier.MediumMotion;
            }

            return ECpuOcclusionMotionTier.SmallMotion;
        }

        private static float DotToDegrees(float dot)
        {
            float clamped = Math.Clamp(dot, -1.0f, 1.0f);
            return MathF.Acos(clamped) * (180.0f / MathF.PI);
        }

        private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
            => value.LengthSquared() > 0.000001f ? Vector3.Normalize(value) : fallback;
    }
}

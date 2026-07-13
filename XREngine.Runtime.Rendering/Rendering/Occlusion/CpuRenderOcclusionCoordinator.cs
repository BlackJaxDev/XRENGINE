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
            public ulong RecoveryStartedFrame = ulong.MaxValue;

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
            public OcclusionViewOwnership Ownership;
            public PovState Pov = null!;
            public OcclusionTelemetry.CpuViewTelemetryHandle Telemetry;
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
            public ulong CommandSetSignature;
            public bool HasCommandSetSignature;
            public ulong LastTouchedFrame;
        }

        private readonly struct PovKey : IEquatable<PovKey>
        {
            public PovKey(int renderPass, int povId, int resourceGeneration)
            {
                RenderPass = renderPass;
                PovId = povId;
                ResourceGeneration = resourceGeneration;
            }

            public int RenderPass { get; }
            public int PovId { get; }
            public int ResourceGeneration { get; }

            public bool Equals(PovKey other)
                => RenderPass == other.RenderPass && PovId == other.PovId && ResourceGeneration == other.ResourceGeneration;

            public override bool Equals(object? obj)
                => obj is PovKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(RenderPass, PovId, ResourceGeneration);
        }

        private sealed class PovQueryState
        {
            public uint ValidCoverageMask;
            public uint VisibleCoverageMask;
            public uint PendingCoverageMask;
            public ulong Coverage0Frame;
            public ulong Coverage1Frame;
            public ulong Coverage2Frame;
            public ulong Coverage3Frame;
            public ulong LastRecoveryStartFrame;
            public int MaxAgeFrames;
        }

        private sealed class PovState
        {
            public readonly Dictionary<uint, PovQueryState> Queries = new();
            public PovKey Key;
            public uint RequiredCoverageMask;
            public int DeclaredViewCount;
            public ulong LastTouchedFrame;
        }

        private readonly object _lock = new();
        private readonly Dictionary<OcclusionViewKey, PassState> _passStates = new();
        private readonly Dictionary<PovKey, PovState> _povStates = new();
        private readonly AsyncOcclusionQueryManager _queryManager = new();
        private readonly List<OcclusionViewKey> _stalePassKeys = new(8);
        private readonly List<PovKey> _stalePovKeys = new(8);
        private ulong _lastOwnershipEvictionFrame;

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

        public bool BeginPass(
            int renderPass,
            XRCamera camera,
            uint sceneCommandCount,
            int? pipelineInstanceId = null)
            => BeginPass(
                renderPass,
                camera,
                sceneCommandCount,
                CreateDefaultOwnership(pipelineInstanceId));

        public bool BeginPass(
            int renderPass,
            XRCamera camera,
            uint sceneCommandCount,
            OcclusionViewOwnership ownership)
            => BeginPass(
                renderPass,
                camera,
                sceneCommandCount,
                BuildFallbackCommandSetSignature(sceneCommandCount),
                ownership);

        public bool BeginPass(
            int renderPass,
            XRCamera camera,
            uint sceneCommandCount,
            ulong commandSetSignature,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
                bool commandSetChanged = state.HasCommandSetSignature &&
                    state.CommandSetSignature != commandSetSignature;
                state.LastSceneCommandCount = sceneCommandCount;
                state.CommandSetSignature = commandSetSignature;
                state.HasCommandSetSignature = true;
                state.LastTouchedFrame = frameId;
                state.Pov.LastTouchedFrame = frameId;
                state.ForceVisibleThisFrame = false;
                state.ForceVisibleReason = ECpuOcclusionForceVisibleReason.None;

                ECpuOcclusionMotionTier tier = UpdateCameraVisibilityState(state, camera);
                state.MotionTier = tier;

                if (!ownership.IsValid)
                    ForceVisibleForPass(state, ECpuOcclusionForceVisibleReason.MissingOwnership);
                else if (commandSetChanged)
                {
                    InvalidateForCommandSetChange(state, frameId);
                    ForceVisibleForPass(state, ECpuOcclusionForceVisibleReason.CommandSetChanged);
                }
                else if (tier == ECpuOcclusionMotionTier.CameraCut)
                {
                    InvalidatePov(state.Pov);
                    ForceVisibleForPass(state, ECpuOcclusionForceVisibleReason.CameraCut);
                }
                else if (!IsHardwareQueryBackendSupported())
                    ForceVisibleForPass(state, ECpuOcclusionForceVisibleReason.UnsupportedBackend);
                else if (IsUnsupportedSharedStereoScope(state.ViewKey))
                    ForceVisibleForPass(state, ECpuOcclusionForceVisibleReason.UnsupportedStereoMode);

                ResolveAvailableResultsForPov(state.Pov);
                EvictStaleOwnershipStates(frameId);
                OcclusionTelemetry.RecordCpuMotionTier(state.MotionTier);
                OcclusionTelemetry.RecordCpuActiveViewScope(state.ViewKey.Scope);
                state.Telemetry.RecordPassBegin((int)sceneCommandCount);
                if (state.ForceVisibleThisFrame)
                {
                    OcclusionTelemetry.RecordCpuGlobalConservativeFrame(state.ForceVisibleReason);
                    state.Telemetry.RecordForcedVisible();
                }

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
            out CpuOcclusionProbeRequest probeRequest,
            int? pipelineInstanceId = null)
            => ShouldRender(
                renderPass,
                camera,
                sourceCommandIndex,
                out probeRequest,
                CreateDefaultOwnership(pipelineInstanceId));

        public ECpuOcclusionDecision ShouldRender(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            out CpuOcclusionProbeRequest probeRequest,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
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
                    return SetDecision(state, queryState, ECpuOcclusionDecision.Visible, CpuOcclusionProbeRequest.None, ECpuDecisionKind.ForcedVisible, out probeRequest);

                if (ExpireOverduePendingQuery(state, sourceCommandIndex, queryState, frameId))
                    return SetDecision(state, queryState, ECpuOcclusionDecision.Visible, CpuOcclusionProbeRequest.None, ECpuDecisionKind.ForcedVisible, out probeRequest);

                if (queryState.QueryPending)
                {
                    if (queryState.PendingQueryWasVisibleDraw ||
                        queryState.StateKind == ECpuOcclusionQueryStateKind.PendingVisibleProbe)
                    {
                        return SetDecision(state, queryState, ECpuOcclusionDecision.Visible, CpuOcclusionProbeRequest.None, ECpuDecisionKind.VisibleHysteresis, out probeRequest);
                    }

                    if ((state.Pov.RequiredCoverageMask & (state.Pov.RequiredCoverageMask - 1u)) != 0u)
                    {
                        // A recovery query invalidates this member's coverage until
                        // its result resolves. No sibling eye may consume the old
                        // all-occluded aggregate during that pending window.
                        return SetDecision(state, queryState, ECpuOcclusionDecision.Visible, CpuOcclusionProbeRequest.None, ECpuDecisionKind.ForcedVisible, out probeRequest);
                    }

                    return SetDecision(state, queryState, ECpuOcclusionDecision.Skip, CpuOcclusionProbeRequest.None, ECpuDecisionKind.Skip, out probeRequest);
                }

                if (queryState.StateKind == ECpuOcclusionQueryStateKind.Unknown)
                {
                    var request = new CpuOcclusionProbeRequest(true, ECpuOcclusionQueryReason.InitialSeed, recoveryProbe: false, priorityBias: 2.0f);
                    return SetDecision(state, queryState, ECpuOcclusionDecision.Visible, request, ECpuDecisionKind.Seed, out probeRequest);
                }

                if (queryState.RecoveryStartedFrame != ulong.MaxValue)
                {
                    // A forced-visible transition must not strand the command in
                    // visible hysteresis. Re-probe every eligible frame until a
                    // fresh result completes the recovery interval.
                    var request = new CpuOcclusionProbeRequest(
                        true,
                        ECpuOcclusionQueryReason.StaleStateRefresh,
                        recoveryProbe: false,
                        priorityBias: 4.0f);
                    state.Telemetry.RecordRecoveryAge(GetFrameAge(queryState.RecoveryStartedFrame, frameId));
                    return SetDecision(
                        state,
                        queryState,
                        ECpuOcclusionDecision.Visible,
                        request,
                        ECpuDecisionKind.ForcedVisible,
                        out probeRequest);
                }

                bool anySamplesPassed = GetConservativeAnySamplesPassed(state, sourceCommandIndex, queryState, frameId, out bool staleOrIncomplete);
                if (anySamplesPassed)
                {
                    queryState.StateKind = ECpuOcclusionQueryStateKind.PredictedVisible;
                    queryState.ConsecutiveOccludedFrames = 0;
                    queryState.ConsecutiveVisibleFrames++;

                    CpuOcclusionProbeRequest request = staleOrIncomplete
                        ? new CpuOcclusionProbeRequest(
                            true,
                            ECpuOcclusionQueryReason.StaleStateRefresh,
                            recoveryProbe: false,
                            priorityBias: 4.0f)
                        : ShouldRefreshVisibleProbe(queryState, frameId, state.MotionTier)
                        ? new CpuOcclusionProbeRequest(
                            true,
                            state.MotionTier == ECpuOcclusionMotionTier.Stable
                                ? ECpuOcclusionQueryReason.VisibleDemotion
                                : ECpuOcclusionQueryReason.CameraMotionRevalidation,
                            recoveryProbe: false,
                            priorityBias: GetMotionPriorityBias(state.MotionTier))
                        : CpuOcclusionProbeRequest.None;

                    if (staleOrIncomplete)
                    {
                        BeginRecovery(state, queryState, frameId);
                        OcclusionTelemetry.RecordCpuForcedVisible(ECpuOcclusionForceVisibleReason.StaleResult);
                        state.Telemetry.RecordForcedVisible();
                    }

                    return SetDecision(
                        state,
                        queryState,
                        ECpuOcclusionDecision.Visible,
                        request,
                        staleOrIncomplete ? ECpuDecisionKind.ForcedVisible : ECpuDecisionKind.VisibleQuery,
                        out probeRequest);
                }

                queryState.StateKind = ECpuOcclusionQueryStateKind.PredictedOccluded;

                if (IsHierarchyFreshOccluded(state, sourceCommandIndex, frameId))
                    return SetDecision(state, queryState, ECpuOcclusionDecision.Skip, CpuOcclusionProbeRequest.None, ECpuDecisionKind.Skip, out probeRequest);

                if (queryState.ConsecutiveOccludedFrames < VisibleDemotionZeroConfidenceFrames)
                {
                    queryState.ConsecutiveOccludedFrames++;
                    var request = new CpuOcclusionProbeRequest(true, ECpuOcclusionQueryReason.VisibleDemotion, recoveryProbe: false, priorityBias: 1.0f);
                    return SetDecision(state, queryState, ECpuOcclusionDecision.Visible, request, ECpuDecisionKind.VisibleHysteresis, out probeRequest);
                }

                if (ShouldForceVisibleForStaleOcclusion(queryState, frameId, state.MotionTier))
                {
                    BeginRecovery(state, queryState, frameId);
                    OcclusionTelemetry.RecordCpuForcedVisible(ECpuOcclusionForceVisibleReason.StaleResult);
                    state.Telemetry.RecordForcedVisible();
                    var request = new CpuOcclusionProbeRequest(true, ECpuOcclusionQueryReason.StaleStateRefresh, recoveryProbe: false, priorityBias: 4.0f);
                    return SetDecision(state, queryState, ECpuOcclusionDecision.Visible, request, ECpuDecisionKind.VisibleHysteresis, out probeRequest);
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
                    return SetDecision(state, queryState, ECpuOcclusionDecision.ProbeOnly, request, ECpuDecisionKind.Probe, out probeRequest);
                }

                return SetDecision(state, queryState, ECpuOcclusionDecision.Skip, CpuOcclusionProbeRequest.None, ECpuDecisionKind.Skip, out probeRequest);
            }
        }

        public bool PeekShouldRender(int renderPass, uint sourceCommandIndex)
            => PeekShouldRender(renderPass, null, sourceCommandIndex);

        public bool PeekShouldRender(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            int? pipelineInstanceId = null)
            => PeekShouldRender(
                renderPass,
                camera,
                sourceCommandIndex,
                CreateDefaultOwnership(pipelineInstanceId));

        public bool PeekShouldRender(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
                if (state.ForceVisibleThisFrame)
                    return true;
                if (!state.Queries.TryGetValue(sourceCommandIndex, out QueryState? queryState))
                    return true;
                if (GetConservativeAnySamplesPassed(
                        state,
                        sourceCommandIndex,
                        queryState,
                        RuntimeEngine.Rendering.State.RenderFrameId,
                        out _))
                    return true;
                if (ShouldForceVisibleForStaleOcclusion(
                        queryState,
                        RuntimeEngine.Rendering.State.RenderFrameId,
                        state.MotionTier))
                    return true;
                if (queryState.QueryPending && queryState.PendingQueryWasVisibleDraw)
                    return true;
                if (queryState.ConsecutiveOccludedFrames < VisibleDemotionZeroConfidenceFrames)
                    return true;

                return false;
            }
        }

        /// <summary>
        /// Returns the exact currently valid coverage bits proving that a command
        /// is occluded. Callers use this immediately after a Skip/ProbeOnly decision
        /// to persist Phase 5.2.4b owning-view evidence; it never manufactures proof
        /// for pending, visible, or incomplete shared-view results.
        /// </summary>
        internal uint GetOccludedProofCoverageMask(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                OcclusionViewKey key = CreatePassKey(renderPass, camera, ownership);
                if (!_passStates.TryGetValue(key, out PassState? state))
                    return 0u;

                uint required = state.Pov.RequiredCoverageMask;
                if ((required & (required - 1u)) != 0u &&
                    state.Pov.Queries.TryGetValue(sourceCommandIndex, out PovQueryState? povQuery))
                {
                    return povQuery.ValidCoverageMask &
                           ~povQuery.VisibleCoverageMask &
                           ~povQuery.PendingCoverageMask &
                           required;
                }

                if (state.Queries.TryGetValue(sourceCommandIndex, out QueryState? query) &&
                    !query.QueryPending &&
                    !query.LastAnySamplesPassed)
                {
                    return state.ViewKey.CoverageMask & required;
                }

                return IsHierarchyFreshOccluded(
                    state,
                    sourceCommandIndex,
                    RuntimeEngine.Rendering.State.RenderFrameId)
                    ? state.ViewKey.CoverageMask & required
                    : 0u;
            }
        }

        public void SelectProbeCandidates(
            int renderPass,
            XRCamera? camera,
            List<CpuOcclusionProbeCandidate> candidates,
            List<CpuOcclusionScheduledProbe> scheduled,
            int? pipelineInstanceId = null)
            => SelectProbeCandidates(
                renderPass,
                camera,
                candidates,
                scheduled,
                CreateDefaultOwnership(pipelineInstanceId));

        public void SelectProbeCandidates(
            int renderPass,
            XRCamera? camera,
            List<CpuOcclusionProbeCandidate> candidates,
            List<CpuOcclusionScheduledProbe> scheduled,
            OcclusionViewOwnership ownership)
        {
            if (scheduled.Count != 0)
                scheduled.Clear();
            if (candidates.Count == 0)
                return;

            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
                if (state.ForceVisibleThisFrame)
                {
                    OcclusionTelemetry.RecordCpuBudgetSkipped(ECpuOcclusionQueryReason.DiagnosticForcedQuery, candidates.Count);
                    state.Telemetry.RecordBudgetSkipped(candidates.Count);
                    return;
                }

                RefreshBudgets(state);
                int maxQueries = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMaxQueriesPerFrame;
                if (maxQueries <= 0)
                {
                    OcclusionTelemetry.RecordCpuBudgetSkipped(ECpuOcclusionQueryReason.DiagnosticForcedQuery, candidates.Count);
                    state.Telemetry.RecordBudgetSkipped(candidates.Count);
                    return;
                }

                int pendingQueries = CountPendingQueries(state);
                OcclusionTelemetry.RecordCpuPendingQueries(pendingQueries);
                int available = Math.Max(0, maxQueries - pendingQueries);
                if (available == 0)
                {
                    OcclusionTelemetry.RecordCpuBudgetSkipped(ECpuOcclusionQueryReason.StaleStateRefresh, candidates.Count);
                    state.Telemetry.RecordBudgetSkipped(candidates.Count);
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

        public void BeginQuery(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            int? pipelineInstanceId = null)
            => BeginQuery(renderPass, camera, sourceCommandIndex, CreateDefaultOwnership(pipelineInstanceId));

        public void BeginQuery(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
                QueryState queryState = GetOrCreateQueryState(state, sourceCommandIndex);
                BeginQueryCore(queryState);
            }
        }

        public void EndQuery(int renderPass, uint sourceCommandIndex)
            => EndQuery(renderPass, null, sourceCommandIndex);

        public void EndQuery(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            int? pipelineInstanceId = null)
            => EndQuery(renderPass, camera, sourceCommandIndex, CreateDefaultOwnership(pipelineInstanceId));

        public void EndQuery(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
                if (!state.Queries.TryGetValue(sourceCommandIndex, out QueryState? queryState))
                    return;

                if (EndQueryCore(queryState, queryState.LastProbeRequest))
                    MarkPovQueryPending(state, sourceCommandIndex);
            }
        }

        public void BeginHierarchyQuery(
            int renderPass,
            XRCamera? camera,
            uint hierarchyGroupKey,
            int? pipelineInstanceId = null)
            => BeginHierarchyQuery(renderPass, camera, hierarchyGroupKey, CreateDefaultOwnership(pipelineInstanceId));

        public void BeginHierarchyQuery(
            int renderPass,
            XRCamera? camera,
            uint hierarchyGroupKey,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
                HierarchyGroupState group = GetOrCreateHierarchyGroup(state, hierarchyGroupKey);
                BeginQueryCore(group.Query);
            }
        }

        public void EndHierarchyQuery(
            int renderPass,
            XRCamera? camera,
            uint hierarchyGroupKey,
            int? pipelineInstanceId = null)
            => EndHierarchyQuery(renderPass, camera, hierarchyGroupKey, CreateDefaultOwnership(pipelineInstanceId));

        public void EndHierarchyQuery(
            int renderPass,
            XRCamera? camera,
            uint hierarchyGroupKey,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
                if (!state.HierarchyGroups.TryGetValue(hierarchyGroupKey, out HierarchyGroupState? group))
                    return;

                if (EndQueryCore(
                    group.Query,
                    new CpuOcclusionProbeRequest(
                        true,
                        ECpuOcclusionQueryReason.OccludedRecovery,
                        recoveryProbe: true)))
                {
                    state.Telemetry.RecordSubmission();
                }
            }
        }

        /// <summary>
        /// Keeps a validation sentinel fail-visible while still requesting a real
        /// individual proxy query. A pending result remains consumable; this never
        /// manufactures a visible result or converts the request into hierarchy work.
        /// </summary>
        internal CpuOcclusionProbeRequest ForceVisibleForValidation(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
                QueryState queryState = GetOrCreateQueryState(state, sourceCommandIndex);
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;

                queryState.LastTouchedFrame = frameId;
                queryState.LastDecision = ECpuOcclusionDecision.Visible;
                queryState.LastDecidedFrameId = frameId;

                CpuOcclusionProbeRequest request = CpuOcclusionProbeRequest.None;
                if (!state.ForceVisibleThisFrame &&
                    !queryState.QueryPending &&
                    queryState.QueryIssuedFrameId != frameId)
                {
                    BeginRecovery(state, queryState, frameId);
                    request = new CpuOcclusionProbeRequest(
                        true,
                        ECpuOcclusionQueryReason.DiagnosticForcedQuery,
                        recoveryProbe: false,
                        priorityBias: 8.0f);
                    queryState.LastProbeRequest = request;
                }

                OcclusionTelemetry.RecordCpuForcedVisible(ECpuOcclusionForceVisibleReason.Diagnostic);
                state.Telemetry.RecordForcedVisible();
                return request;
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
            ECpuOcclusionForceVisibleReason reason,
            int? pipelineInstanceId = null)
            => ForceVisible(
                renderPass,
                camera,
                sourceCommandIndex,
                reason,
                CreateDefaultOwnership(pipelineInstanceId));

        public void ForceVisible(
            int renderPass,
            XRCamera? camera,
            uint sourceCommandIndex,
            ECpuOcclusionForceVisibleReason reason,
            OcclusionViewOwnership ownership)
        {
            lock (_lock)
            {
                PassState state = GetPassStateForOwnership(renderPass, camera, ownership);
                QueryState queryState = GetOrCreateQueryState(state, sourceCommandIndex);
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
                if (ShouldTrackRecovery(reason))
                    BeginRecovery(state, queryState, frameId);
                else
                    CancelRecovery(queryState);
                ForceQueryStateVisible(queryState, frameId);
                InvalidatePovQuery(state.Pov, sourceCommandIndex, state.ViewKey.CoverageMask);
                OcclusionTelemetry.RecordCpuForcedVisible(reason);
                state.Telemetry.RecordForcedVisible();
            }
        }

        private static ECpuOcclusionDecision SetDecision(
            PassState state,
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
            if (decision == ECpuOcclusionDecision.Skip)
                state.Telemetry.RecordSkip();
            return decision;
        }

        private PassState GetPassState(int renderPass, XRCamera? camera)
            => GetPassStateForPipeline(renderPass, camera, pipelineInstanceId: null);

        private PassState GetPassStateForPipeline(
            int renderPass,
            XRCamera? camera,
            int? pipelineInstanceId = null)
            => GetPassStateForOwnership(renderPass, camera, CreateDefaultOwnership(pipelineInstanceId));

        private PassState GetPassStateForOwnership(
            int renderPass,
            XRCamera? camera,
            OcclusionViewOwnership ownership)
        {
            OcclusionViewKey passKey = CreatePassKey(renderPass, camera, ownership);
            if (!_passStates.TryGetValue(passKey, out PassState? state))
            {
                PovState pov = GetOrCreatePovState(passKey);
                state = new PassState
                {
                    ViewKey = passKey,
                    Ownership = ownership,
                    Pov = pov,
                    Telemetry = OcclusionTelemetry.GetCpuViewTelemetryHandle(passKey),
                    LastTouchedFrame = RuntimeEngine.Rendering.State.RenderFrameId,
                };
                _passStates.Add(passKey, state);
            }

            return state;
        }

        private PovState GetOrCreatePovState(OcclusionViewKey viewKey)
        {
            var key = new PovKey(viewKey.RenderPass, viewKey.PovId, viewKey.ResourceGeneration);
            if (_povStates.TryGetValue(key, out PovState? state))
            {
                // Coverage disagreement is fail-visible by construction: the union
                // cannot become fully valid until every declared member reports.
                state.RequiredCoverageMask |= viewKey.RequiredCoverageMask;
                state.DeclaredViewCount = Math.Max(state.DeclaredViewCount, viewKey.DeclaredViewCount);
                return state;
            }

            state = new PovState
            {
                Key = key,
                RequiredCoverageMask = viewKey.RequiredCoverageMask,
                DeclaredViewCount = viewKey.DeclaredViewCount,
                LastTouchedFrame = RuntimeEngine.Rendering.State.RenderFrameId,
            };
            _povStates.Add(key, state);
            return state;
        }

        private static OcclusionViewOwnership CreateDefaultOwnership(int? pipelineInstanceId)
        {
            int id = pipelineInstanceId.GetValueOrDefault();
            return id > 0
                ? OcclusionViewOwnership.Independent(id)
                : default;
        }

        internal static OcclusionViewKey CreatePassKey(
            int renderPass,
            XRCamera? camera,
            int? pipelineInstanceId = null)
            => CreatePassKey(renderPass, camera, CreateDefaultOwnership(pipelineInstanceId));

        internal static OcclusionViewKey CreatePassKey(
            int renderPass,
            XRCamera? camera,
            OcclusionViewOwnership ownership)
        {
            IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
            IRuntimeRenderCommandExecutionState? renderState = host.ActiveRenderCommandExecutionState;
            bool stereoPass = renderState?.StereoPass == true || RuntimeEngine.Rendering.State.IsStereoPass;
            bool eyeCamera = camera?.StereoEyeLeft.HasValue == true;
            // Occlusion state is isolated per render pipeline instance: desktop, each VR
            // eye, and capture/preview cameras run completely independent query state.
            int resolvedPipelineInstanceId = ownership.PipelineInstanceId;

            EOcclusionViewScope scope;
            int viewId;

            if (ownership.HasScopeOverride)
            {
                scope = ownership.Scope;
                viewId = camera?.StereoEyeLeft == false ? 1 : 0;
            }
            else if (stereoPass)
            {
                scope = host.EnableVrFoveatedViewSet
                    ? EOcclusionViewScope.VrFoveatedView
                    : host.VrViewRenderMode == EVrViewRenderMode.SinglePassStereo
                        ? EOcclusionViewScope.VrSinglePassStereo
                        : EOcclusionViewScope.VrStereoPair;
                viewId = 0;
            }
            else if (eyeCamera)
            {
                bool left = camera!.StereoEyeLeft.GetValueOrDefault();
                scope = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionStereoMode == ECpuQueryStereoMode.StereoPairShared
                    ? EOcclusionViewScope.VrStereoPair
                    : left ? EOcclusionViewScope.VrLeftEye : EOcclusionViewScope.VrRightEye;
                viewId = left ? 0 : 1;
            }
            else if (host.IsInVR && host.VrMirrorComposeFromEyeTextures)
            {
                scope = EOcclusionViewScope.MirrorOnly;
                viewId = 0;
            }
            else if (host.IsInVR && host.RenderWindowsWhileInVR)
            {
                scope = EOcclusionViewScope.EditorDesktopWhileVr;
                viewId = 0;
            }
            else
            {
                scope = EOcclusionViewScope.MonoDesktop;
                viewId = 0;
            }

            uint coverageMask = ownership.IsValid ? ownership.CoverageMask : 0x1u;
            uint requiredCoverageMask = ownership.IsValid ? ownership.RequiredCoverageMask : 0x1u;
            int declaredViewCount = ownership.IsValid ? ownership.DeclaredViewCount : 1;
            int povId = ownership.IsValid ? ownership.PovId : 0;
            return new OcclusionViewKey(
                renderPass,
                scope,
                viewId,
                resolvedPipelineInstanceId,
                povId,
                coverageMask,
                requiredCoverageMask,
                declaredViewCount,
                ownership.ResourceGeneration,
                ownership.OutputId);
        }

        internal static bool IsUnsupportedSharedStereoScope(OcclusionViewKey key)
            // True single-pass/foveated scopes emit one multiview query whose result is
            // conservative across every layer in that pipeline. Explicit per-eye POV
            // ownership uses partial coverage and aggregates independent physical queries;
            // only the legacy single-key eye-pair alias remains gated.
            => key.Scope == EOcclusionViewScope.VrStereoPair &&
               key.CoverageMask == key.RequiredCoverageMask &&
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

        private static bool EndQueryCore(QueryState queryState, CpuOcclusionProbeRequest request)
        {
            if (queryState.Query is null)
                return false;

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (queryState.QueryIssuedFrameId != frameId)
                return false;

            if (!EndBackendQuery(queryState.Query))
                return false;

            queryState.QueryPending = true;
            queryState.PendingSinceFrame = frameId;
            queryState.PendingReason = request.Reason;
            queryState.PendingQueryWasVisibleDraw = request.RecoveryProbe == false;
            queryState.StateKind = request.RecoveryProbe
                ? ECpuOcclusionQueryStateKind.PendingOccludedProbe
                : ECpuOcclusionQueryStateKind.PendingVisibleProbe;
            queryState.LastQueryFrame = frameId;
            OcclusionTelemetry.RecordCpuQuerySubmitted(request.Reason);
            return true;
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
                bool discardResult = queryState.DiscardPendingResult;
                ResolveQueryState(queryState, frameId, out bool resolved, out bool anySamplesPassed);
                if (resolved && !discardResult)
                {
                    ApplyResolvedCommandResult(state, key, queryState, frameId, anySamplesPassed);
                    ApplyResolvedPovResult(state, key, frameId, anySamplesPassed);
                    state.Telemetry.RecordResolution(queryState.PendingSinceFrame, frameId);
                    CompleteRecovery(state, queryState, frameId);
                }

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

                state.Telemetry.RecordResolution(group.Query.PendingSinceFrame, frameId);

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

        private bool ExpireOverduePendingQuery(
            PassState state,
            uint sourceCommandIndex,
            QueryState queryState,
            ulong frameId)
        {
            if (!queryState.QueryPending)
                return false;

            int maxPendingFrames = RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMaxPendingFrames;
            if (frameId - queryState.PendingSinceFrame <= (ulong)Math.Max(1, maxPendingFrames))
                return false;

            queryState.QueryPending = false;
            queryState.DiscardPendingResult = true;
            InvalidatePovQuery(state.Pov, sourceCommandIndex, state.ViewKey.CoverageMask);
            BeginRecovery(state, queryState, frameId);
            ForceQueryStateVisible(queryState, frameId);
            OcclusionTelemetry.RecordCpuForcedVisible(ECpuOcclusionForceVisibleReason.PendingTooOld);
            state.Telemetry.RecordForcedVisible();
            return true;
        }

        private void ResolveAvailableResultsForPov(PovState pov)
        {
            // Resolve every physical member before the first member consumes the
            // family aggregate this frame. This prevents left/right decisions
            // from depending on which eye happened to call BeginPass first.
            foreach (PassState pass in _passStates.Values)
            {
                if (ReferenceEquals(pass.Pov, pov))
                    ResolveAvailableResults(pass);
            }
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
            if (queryState.LastQueryFrame == 0UL || frameId < queryState.LastQueryFrame)
                return true;

            int period = GetOccludedRetestPeriodFrames();
            int maximumAge = motionTier switch
            {
                ECpuOcclusionMotionTier.Stable => Math.Max(2, period * 2),
                ECpuOcclusionMotionTier.SmallMotion => Math.Max(2, period),
                ECpuOcclusionMotionTier.MediumMotion => Math.Max(1, period / 2),
                ECpuOcclusionMotionTier.LargeMotion => 1,
                ECpuOcclusionMotionTier.VrHeadPoseMotion => Math.Max(1, RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRecoveryMinCadenceFrames),
                _ => 1,
            };
            return frameId - queryState.LastQueryFrame > (ulong)maximumAge;
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
                if (group.LastQueryFrame != 0UL && group.LastAnySamplesPassed)
                {
                    // A visible group is only a broad-phase answer. Expand back
                    // to individual members so one visible child cannot make the
                    // group query starve every recovery probe indefinitely.
                    continue;
                }

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
                state.Telemetry.RecordBudgetSkipped(skipped);
            }
        }

        private static void MarkPovQueryPending(PassState state, uint sourceCommandIndex)
        {
            PovQueryState query = GetOrCreatePovQueryState(state.Pov, sourceCommandIndex);
            uint coverage = state.ViewKey.CoverageMask;
            query.ValidCoverageMask &= ~coverage;
            query.PendingCoverageMask |= coverage;
            if (query.LastRecoveryStartFrame == 0UL)
                query.LastRecoveryStartFrame = RuntimeEngine.Rendering.State.RenderFrameId;
            state.Telemetry.RecordSubmission();
        }

        private static void ApplyResolvedPovResult(
            PassState state,
            uint sourceCommandIndex,
            ulong frameId,
            bool anySamplesPassed)
        {
            PovQueryState query = GetOrCreatePovQueryState(state.Pov, sourceCommandIndex);
            uint coverage = state.ViewKey.CoverageMask;
            query.ValidCoverageMask |= coverage;
            query.PendingCoverageMask &= ~coverage;
            if (anySamplesPassed)
                query.VisibleCoverageMask |= coverage;
            else
                query.VisibleCoverageMask &= ~coverage;

            SetCoverageFrame(query, coverage, frameId);
            query.LastRecoveryStartFrame = 0UL;
        }

        private static bool GetConservativeAnySamplesPassed(
            PassState state,
            uint sourceCommandIndex,
            QueryState physicalQuery,
            ulong frameId,
            out bool staleOrIncomplete)
        {
            staleOrIncomplete = false;
            uint required = state.Pov.RequiredCoverageMask;
            if ((required & (required - 1u)) == 0u)
                return physicalQuery.LastAnySamplesPassed;

            if (!state.Pov.Queries.TryGetValue(sourceCommandIndex, out PovQueryState? query))
            {
                staleOrIncomplete = true;
                return true;
            }

            if ((query.ValidCoverageMask & required) != required ||
                (query.PendingCoverageMask & required) != 0u)
            {
                staleOrIncomplete = true;
                state.Telemetry.RecordResultAge(int.MaxValue);
                return true;
            }

            int maximumAge = GetMaximumOccludedResultAge(state.MotionTier);
            int currentAge = 0;
            for (int bit = 0; bit < 4; bit++)
            {
                uint mask = 1u << bit;
                if ((required & mask) == 0u)
                    continue;

                ulong resolvedFrame = GetCoverageFrame(query, bit);
                if (resolvedFrame == 0UL || resolvedFrame > frameId)
                {
                    staleOrIncomplete = true;
                    return true;
                }

                ulong age = frameId - resolvedFrame;
                int boundedAge = age > int.MaxValue ? int.MaxValue : (int)age;
                currentAge = Math.Max(currentAge, boundedAge);
                if (age > (ulong)maximumAge)
                    staleOrIncomplete = true;
            }

            query.MaxAgeFrames = Math.Max(query.MaxAgeFrames, currentAge);
            state.Telemetry.RecordResultAge(currentAge);
            if (staleOrIncomplete)
                return true;

            return (query.VisibleCoverageMask & required) != 0u;
        }

        private static int GetMaximumOccludedResultAge(ECpuOcclusionMotionTier motionTier)
        {
            int period = GetOccludedRetestPeriodFrames();
            return motionTier switch
            {
                ECpuOcclusionMotionTier.Stable => Math.Max(2, period * 2),
                ECpuOcclusionMotionTier.SmallMotion => Math.Max(2, period),
                ECpuOcclusionMotionTier.MediumMotion => Math.Max(1, period / 2),
                ECpuOcclusionMotionTier.LargeMotion => 1,
                ECpuOcclusionMotionTier.VrHeadPoseMotion => Math.Max(1, RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRecoveryMinCadenceFrames),
                _ => 1,
            };
        }

        private static PovQueryState GetOrCreatePovQueryState(PovState pov, uint sourceCommandIndex)
        {
            if (pov.Queries.TryGetValue(sourceCommandIndex, out PovQueryState? query))
                return query;

            query = new PovQueryState();
            pov.Queries.Add(sourceCommandIndex, query);
            return query;
        }

        private static void SetCoverageFrame(PovQueryState query, uint coverage, ulong frameId)
        {
            if ((coverage & 0x1u) != 0u)
                query.Coverage0Frame = frameId;
            if ((coverage & 0x2u) != 0u)
                query.Coverage1Frame = frameId;
            if ((coverage & 0x4u) != 0u)
                query.Coverage2Frame = frameId;
            if ((coverage & 0x8u) != 0u)
                query.Coverage3Frame = frameId;
        }

        private static ulong GetCoverageFrame(PovQueryState query, int bit)
            => bit switch
            {
                0 => query.Coverage0Frame,
                1 => query.Coverage1Frame,
                2 => query.Coverage2Frame,
                3 => query.Coverage3Frame,
                _ => 0UL,
            };

        private static void InvalidatePov(PovState pov)
        {
            foreach (PovQueryState query in pov.Queries.Values)
            {
                query.ValidCoverageMask = 0u;
                query.PendingCoverageMask = 0u;
                query.VisibleCoverageMask = 0u;
                query.Coverage0Frame = 0UL;
                query.Coverage1Frame = 0UL;
                query.Coverage2Frame = 0UL;
                query.Coverage3Frame = 0UL;
            }
        }

        private static ulong BuildFallbackCommandSetSignature(uint commandCount)
            => 0x9E3779B97F4A7C15UL ^ commandCount;

        private static void InvalidateForCommandSetChange(PassState state, ulong frameId)
        {
            InvalidatePov(state.Pov);
            foreach (QueryState query in state.Queries.Values)
            {
                BeginRecovery(state, query, frameId);
                query.StateKind = ECpuOcclusionQueryStateKind.Unknown;
                query.LastAnySamplesPassed = true;
                query.ConsecutiveVisibleFrames = 0;
                query.ConsecutiveOccludedFrames = 0;
                query.LastDecidedFrameId = ulong.MaxValue;
                query.QueryIssuedFrameId = ulong.MaxValue;
                query.LastProbeRequest = CpuOcclusionProbeRequest.None;
                if (query.QueryPending)
                    query.DiscardPendingResult = true;
            }

            foreach (HierarchyGroupState group in state.HierarchyGroups.Values)
            {
                group.LastAnySamplesPassed = true;
                group.LastQueryFrame = 0UL;
                group.LastVisibleFrame = frameId;
                group.LastOccludedFrame = 0UL;
                group.ScheduledFrameId = ulong.MaxValue;
                group.ChildProbeCount = 0;
                if (group.Query.QueryPending)
                    group.Query.DiscardPendingResult = true;
            }
        }

        private static void BeginRecovery(PassState state, QueryState queryState, ulong frameId)
        {
            if (queryState.RecoveryStartedFrame != ulong.MaxValue)
                return;

            queryState.RecoveryStartedFrame = frameId;
            state.Telemetry.RecordRecoveryStarted();
        }

        private static void CancelRecovery(QueryState queryState)
            => queryState.RecoveryStartedFrame = ulong.MaxValue;

        private static bool ShouldTrackRecovery(ECpuOcclusionForceVisibleReason reason)
            => reason is ECpuOcclusionForceVisibleReason.CameraCut
                or ECpuOcclusionForceVisibleReason.ProjectionDiscontinuity
                or ECpuOcclusionForceVisibleReason.PendingTooOld
                or ECpuOcclusionForceVisibleReason.StaleResult
                or ECpuOcclusionForceVisibleReason.ResourceGenerationChanged
                or ECpuOcclusionForceVisibleReason.CommandSetChanged;

        private static int GetFrameAge(ulong startFrame, ulong currentFrame)
        {
            ulong age = currentFrame >= startFrame ? currentFrame - startFrame : 0UL;
            return age > int.MaxValue ? int.MaxValue : (int)age;
        }

        private static void CompleteRecovery(PassState state, QueryState queryState, ulong frameId)
        {
            if (queryState.RecoveryStartedFrame == ulong.MaxValue)
                return;

            state.Telemetry.RecordRecoveryCompleted(queryState.RecoveryStartedFrame, frameId);
            queryState.RecoveryStartedFrame = ulong.MaxValue;
        }

        private static void InvalidatePovQuery(PovState pov, uint sourceCommandIndex, uint coverageMask)
        {
            if (!pov.Queries.TryGetValue(sourceCommandIndex, out PovQueryState? query))
                return;

            query.ValidCoverageMask &= ~coverageMask;
            query.PendingCoverageMask &= ~coverageMask;
        }

        private void EvictStaleOwnershipStates(ulong frameId)
        {
            if (frameId < _lastOwnershipEvictionFrame + StaleEvictionFrames)
                return;

            _lastOwnershipEvictionFrame = frameId;
            _stalePassKeys.Clear();
            foreach (KeyValuePair<OcclusionViewKey, PassState> pair in _passStates)
            {
                if (frameId - pair.Value.LastTouchedFrame <= StaleEvictionFrames)
                    continue;

                _stalePassKeys.Add(pair.Key);
            }

            if (_stalePassKeys.Count == 0)
                return;

            foreach (OcclusionViewKey key in _stalePassKeys)
            {
                if (!_passStates.Remove(key, out PassState? removed))
                    continue;
                foreach (QueryState query in removed.Queries.Values)
                {
                    if (query.Query is not null)
                        _queryManager.Release(query.Query);
                }
                foreach (HierarchyGroupState group in removed.HierarchyGroups.Values)
                {
                    if (group.Query.Query is not null)
                        _queryManager.Release(group.Query.Query);
                }
            }

            _stalePovKeys.Clear();
            foreach (KeyValuePair<PovKey, PovState> pair in _povStates)
            {
                bool referenced = false;
                foreach (PassState pass in _passStates.Values)
                {
                    if (ReferenceEquals(pass.Pov, pair.Value))
                    {
                        referenced = true;
                        break;
                    }
                }
                if (!referenced)
                    _stalePovKeys.Add(pair.Key);
            }
            foreach (PovKey key in _stalePovKeys)
                _povStates.Remove(key);
        }

        private bool IsHierarchyFreshOccluded(PassState state, uint sourceCommandIndex, ulong frameId)
        {
            // Hierarchy queries are physical-pass probes. A headset family can
            // only cull from the per-command aggregate until hierarchy coverage
            // is tracked for every member as well.
            if (state.ViewKey.RequiredCoverageMask != state.ViewKey.CoverageMask)
                return false;

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
                if (ShouldTrackRecovery(reason))
                {
                    BeginRecovery(state, queryState, frameId);
                    state.Telemetry.RecordRecoveryAge(GetFrameAge(queryState.RecoveryStartedFrame, frameId));
                }
                else
                {
                    CancelRecovery(queryState);
                }
                if (queryState.StateKind != ECpuOcclusionQueryStateKind.Unknown)
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

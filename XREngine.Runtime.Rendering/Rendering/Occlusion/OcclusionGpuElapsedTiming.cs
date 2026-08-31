using System;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Occlusion;

/// <summary>Delayed, non-blocking elapsed-GPU timing for Hi-Z build and test work.</summary>
public sealed class OcclusionGpuElapsedTiming
{
    public static OcclusionGpuElapsedTiming Instance { get; } = new();
    private const int QueryPairCount = 8;
    private readonly object _sync = new();
    private readonly ConditionalWeakTable<IRuntimeRendererHost, RendererTimingState> _states = [];
    // Completed measurements and live diagnostic state are separate so saturation
    // remains visible without overwriting the last trustworthy duration.
    private OcclusionGpuElapsedSample _build, _test;
    private IRuntimeRendererHost? _buildOwner, _testOwner;
    private EOcclusionGpuElapsedAvailability _buildDiagnostic, _testDiagnostic;
    private ulong _nextSequence;

    private OcclusionGpuElapsedTiming() { }
    public bool IsRequested => RenderPipelineGpuProfiler.Instance.IsProfilingActive ||
        XREnvironment.IsEnabled(XREngineEnvironmentVariables.OcclusionGpuTiming);

    /// <summary>Begins an allocation-free timestamp scope, or returns a default no-op scope when unavailable.</summary>
    public OcclusionGpuElapsedScope Begin(EOcclusionGpuElapsedStage stage)
    {
        if (!IsRequested || AbstractRenderer.Current is not IRuntimeRendererHost renderer ||
            !renderer.TryGetBackendCapability<IOcclusionQueryBackendCapability>(out IOcclusionQueryBackendCapability? capability) || capability is null)
            return default;
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        lock (_sync)
        {
            RendererTimingState state = _states.GetValue(renderer, static _ => new RendererTimingState());
            if (!state.TryReserve(out int slot, out ulong generation))
            {
                SetNonReady(stage, EOcclusionGpuElapsedAvailability.Saturated, frameId);
                return default;
            }
            ref QueryPair pair = ref state.Pairs[slot];
            if (!capability.EnsureQueryGenerated(pair.Start) || !capability.EnsureQueryGenerated(pair.End))
            {
                state.Release(slot, generation);
                SetNonReady(stage, EOcclusionGpuElapsedAvailability.Unsupported, frameId);
                return default;
            }
            if (capability.WriteTimestamp(pair.Start) != ERenderQueryReadStatus.Ready)
            {
                state.Release(slot, generation);
                SetNonReady(stage, EOcclusionGpuElapsedAvailability.Unsupported, frameId);
                return default;
            }
            state.MarkStartWritten(slot, generation, stage, frameId, NextSequence());
            SetNonReady(stage, EOcclusionGpuElapsedAvailability.Pending, frameId);
            return new OcclusionGpuElapsedScope(this, state, stage, frameId, slot, generation);
        }
    }

    /// <summary>Polls only the supplied current renderer. Uncertain pairs remain quarantined until teardown.</summary>
    public void Resolve(IRuntimeRendererHost renderer, ulong currentFrameId)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (!ReferenceEquals(AbstractRenderer.Current, renderer) ||
            !renderer.TryGetBackendCapability<IOcclusionQueryBackendCapability>(out IOcclusionQueryBackendCapability? capability) || capability is null)
            return;
        lock (_sync)
        {
            if (!_states.TryGetValue(renderer, out RendererTimingState? state)) return;
            for (int slot = 0; slot < state.Pairs.Length; ++slot)
            {
                ref QueryPair pair = ref state.Pairs[slot];
                if (pair.State is not (PairState.Pending or PairState.Quarantined)) continue;
                if (pair.StartWritten && !pair.StartReady && !pair.StartAbandoned)
                {
                    if (capability.TryConsumeAbandonedTimestamp(pair.Start))
                        pair.StartAbandoned = true;
                    else
                    CacheStart(ref pair, capability.TryGetTimestamp(pair.Start, out TimestampQueryResult result), result);
                }
                if (pair.EndWritten && !pair.EndReady && !pair.EndAbandoned)
                {
                    if (capability.TryConsumeAbandonedTimestamp(pair.End))
                        pair.EndAbandoned = true;
                    else
                    CacheEnd(ref pair, capability.TryGetTimestamp(pair.End, out TimestampQueryResult result), result);
                }
                if (!pair.StartTerminal || !pair.EndTerminal) continue;
                if (pair.StartReady && pair.EndReady)
                    SetReady(renderer, pair.Stage, capability.GetElapsedTimestampNanoseconds(pair.StartTicks, pair.EndTicks), pair.FrameId, pair.Sequence, currentFrameId);
                else
                    SetNonReady(pair.Stage, EOcclusionGpuElapsedAvailability.Unsupported, pair.FrameId);
                state.Release(slot, pair.Generation);
            }
        }
    }

    public OcclusionGpuElapsedSample GetSample(EOcclusionGpuElapsedStage stage, ulong currentFrameId)
    {
        lock (_sync)
        {
            OcclusionGpuElapsedSample sample = stage == EOcclusionGpuElapsedStage.Build ? _build : _test;
            return sample with { AgeFrames = Age(currentFrameId, sample.SourceFrameId) };
        }
    }

    public EOcclusionGpuElapsedAvailability GetDiagnosticAvailability(EOcclusionGpuElapsedStage stage)
    {
        lock (_sync)
            return stage == EOcclusionGpuElapsedStage.Build ? _buildDiagnostic : _testDiagnostic;
    }

    /// <summary>Captures the bounded ring state for one renderer without touching backend query objects.</summary>
    public OcclusionGpuElapsedRingDiagnostic CaptureRingDiagnostic(IRuntimeRendererHost renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        lock (_sync)
        {
            if (!_states.TryGetValue(renderer, out RendererTimingState? state))
                return new(QueryPairCount, QueryPairCount, 0, 0, 0, 0, 0, 0, 0);

            int available = 0, open = 0, pending = 0, quarantined = 0;
            int startReady = 0, endReady = 0, startAbandoned = 0, endAbandoned = 0;
            foreach (QueryPair pair in state.Pairs)
            {
                switch (pair.State)
                {
                    case PairState.Available: available++; break;
                    case PairState.Open: open++; break;
                    case PairState.Pending: pending++; break;
                    case PairState.Quarantined: quarantined++; break;
                }
                if (pair.StartReady) startReady++;
                if (pair.EndReady) endReady++;
                if (pair.StartAbandoned) startAbandoned++;
                if (pair.EndAbandoned) endAbandoned++;
            }
            return new(QueryPairCount, available, open, pending, quarantined, startReady, endReady, startAbandoned, endAbandoned);
        }
    }

    /// <summary>Releases all renderer-owned query objects while that renderer's context is still alive.</summary>
    public void CleanupRenderer(IRuntimeRendererHost renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        lock (_sync)
        {
            if (!_states.TryGetValue(renderer, out RendererTimingState? state)) return;
            foreach (QueryPair pair in state.Pairs) { pair.Start.Destroy(); pair.End.Destroy(); }
            _states.Remove(renderer);
            if (ReferenceEquals(_buildOwner, renderer)) { _build = default; _buildOwner = null; _buildDiagnostic = EOcclusionGpuElapsedAvailability.Disabled; }
            if (ReferenceEquals(_testOwner, renderer)) { _test = default; _testOwner = null; _testDiagnostic = EOcclusionGpuElapsedAvailability.Disabled; }
        }
    }

    internal void End(RendererTimingState state, EOcclusionGpuElapsedStage stage, ulong frameId, int slot, ulong generation)
    {
        if (AbstractRenderer.Current is not IRuntimeRendererHost renderer || !_states.TryGetValue(renderer, out RendererTimingState? current) || !ReferenceEquals(state, current) ||
            !renderer.TryGetBackendCapability<IOcclusionQueryBackendCapability>(out IOcclusionQueryBackendCapability? capability) || capability is null)
        {
            lock (_sync)
            {
                state.Quarantine(slot, generation);
                SetNonReady(stage, EOcclusionGpuElapsedAvailability.Unsupported, frameId);
            }
            return;
        }
        lock (_sync)
        {
            if (!state.IsOpen(slot, generation)) return;
            if (capability.WriteTimestamp(state.Pairs[slot].End) == ERenderQueryReadStatus.Ready) state.MarkEndWritten(slot, generation);
            else
            {
                state.Quarantine(slot, generation);
                SetNonReady(stage, EOcclusionGpuElapsedAvailability.Unsupported, frameId);
            }
        }
    }

    private void CacheStart(ref QueryPair pair, ERenderQueryReadStatus status, in TimestampQueryResult result)
    {
        if (status == ERenderQueryReadStatus.Ready) { pair.StartReady = true; pair.StartTicks = result.RawTicks; }
        else if (status != ERenderQueryReadStatus.NotReady) { pair.State = PairState.Quarantined; SetNonReady(pair.Stage, EOcclusionGpuElapsedAvailability.Unsupported, pair.FrameId); }
    }
    private void CacheEnd(ref QueryPair pair, ERenderQueryReadStatus status, in TimestampQueryResult result)
    {
        if (status == ERenderQueryReadStatus.Ready) { pair.EndReady = true; pair.EndTicks = result.RawTicks; }
        else if (status != ERenderQueryReadStatus.NotReady) { pair.State = PairState.Quarantined; SetNonReady(pair.Stage, EOcclusionGpuElapsedAvailability.Unsupported, pair.FrameId); }
    }
    private void SetNonReady(EOcclusionGpuElapsedStage stage, EOcclusionGpuElapsedAvailability availability, ulong frameId)
    {
        if (stage == EOcclusionGpuElapsedStage.Build) _buildDiagnostic = availability;
        else _testDiagnostic = availability;
    }
    private void SetReady(IRuntimeRendererHost owner, EOcclusionGpuElapsedStage stage, ulong elapsed, ulong sourceFrameId, ulong sequence, ulong currentFrameId)
    {
        OcclusionGpuElapsedSample current = stage == EOcclusionGpuElapsedStage.Build ? _build : _test;
        if (current.SourceFrameId > sourceFrameId || current.SourceFrameId == sourceFrameId && current.Sequence >= sequence) return;
        Set(stage, new(EOcclusionGpuElapsedAvailability.Ready, elapsed, sourceFrameId, Age(currentFrameId, sourceFrameId), sequence));
        if (stage == EOcclusionGpuElapsedStage.Build) { _buildOwner = owner; _buildDiagnostic = EOcclusionGpuElapsedAvailability.Ready; }
        else { _testOwner = owner; _testDiagnostic = EOcclusionGpuElapsedAvailability.Ready; }
    }
    private void Set(EOcclusionGpuElapsedStage stage, OcclusionGpuElapsedSample sample) { if (stage == EOcclusionGpuElapsedStage.Build) _build = sample; else _test = sample; }
    private ulong NextSequence() => ++_nextSequence;
    private static ulong Age(ulong current, ulong source) => current > source ? current - source : 0u;

    internal sealed class RendererTimingState
    {
        public QueryPair[] Pairs { get; } = CreatePairs();
        public bool TryReserve(out int slot, out ulong generation)
        {
            for (int i = 0; i < Pairs.Length; ++i)
            {
                ref QueryPair pair = ref Pairs[i];
                if (pair.State != PairState.Available) continue;
                pair.Generation = pair.Generation == ulong.MaxValue ? 1u : pair.Generation + 1u;
                pair.State = PairState.Open; pair.Reset(); slot = i; generation = pair.Generation; return true;
            }
            slot = -1; generation = 0u; return false;
        }
        public bool IsOpen(int slot, ulong generation) => Matches(slot, generation, PairState.Open);
        public void MarkStartWritten(int slot, ulong generation, EOcclusionGpuElapsedStage stage, ulong frameId, ulong sequence)
        {
            if (!Matches(slot, generation, PairState.Open)) return;
            ref QueryPair pair = ref Pairs[slot]; pair.StartWritten = true; pair.Stage = stage; pair.FrameId = frameId; pair.Sequence = sequence;
        }
        public void MarkEndWritten(int slot, ulong generation) { if (Matches(slot, generation, PairState.Open)) { Pairs[slot].EndWritten = true; Pairs[slot].State = PairState.Pending; } }
        public void Quarantine(int slot, ulong generation) { if (Matches(slot, generation, PairState.Open) || Matches(slot, generation, PairState.Pending)) Pairs[slot].State = PairState.Quarantined; }
        public void Release(int slot, ulong generation) { if (slot >= 0 && slot < Pairs.Length && Pairs[slot].Generation == generation) { Pairs[slot].State = PairState.Available; Pairs[slot].Reset(); } }
        private bool Matches(int slot, ulong generation, PairState state) => slot >= 0 && slot < Pairs.Length && Pairs[slot].Generation == generation && Pairs[slot].State == state;
        private static QueryPair[] CreatePairs() { var pairs = new QueryPair[QueryPairCount]; for (int i = 0; i < pairs.Length; ++i) pairs[i] = new(); return pairs; }
    }
    internal struct QueryPair
    {
        public XRRenderQuery Start = new(RenderQueryDescriptor.Timestamp); public XRRenderQuery End = new(RenderQueryDescriptor.Timestamp);
        public PairState State; public ulong Generation, FrameId, Sequence, StartTicks, EndTicks; public EOcclusionGpuElapsedStage Stage; public bool StartWritten, EndWritten, StartReady, EndReady, StartAbandoned, EndAbandoned;
        public readonly bool StartTerminal => StartReady || StartAbandoned;
        public readonly bool EndTerminal => EndReady || EndAbandoned;
        public QueryPair() { }
        public void Reset() => (StartWritten, EndWritten, StartReady, EndReady, StartAbandoned, EndAbandoned, StartTicks, EndTicks) = (false, false, false, false, false, false, 0u, 0u);
    }
    internal enum PairState : byte { Available, Open, Pending, Quarantined }
}

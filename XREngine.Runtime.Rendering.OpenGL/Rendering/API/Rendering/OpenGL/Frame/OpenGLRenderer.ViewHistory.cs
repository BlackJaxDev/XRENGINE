namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private const int ImmediateHistoryCapacity = 32;
    private readonly RenderFrameViewHistoryBackendReservation[] _immediateHistory = new RenderFrameViewHistoryBackendReservation[ImmediateHistoryCapacity];
    private readonly XRFrameBuffer?[] _immediateHistoryTargets = new XRFrameBuffer?[ImmediateHistoryCapacity];
    private readonly bool[] _immediateHistoryWritten = new bool[ImmediateHistoryCapacity];
    private readonly bool[] _immediateHistoryGpuWriteUnproven = new bool[ImmediateHistoryCapacity];
    private int _immediateHistoryDepth;
    private uint _immediateHistoryGeneration;
    private XRFrameBuffer? _historyDrawTarget;
    private bool _historyDrawTargetKnown;

    internal override bool TryReserveFrameViewHistoryCandidate(
        in RenderFrameViewHistoryCandidateToken candidate,
        in RenderOutputRequest output,
        XRFrameBuffer? targetFrameBuffer,
        out RenderFrameViewHistoryBackendReservation reservation)
    {
        reservation = default;
        if (!candidate.IsValid || !output.IsDefined || _immediateHistoryDepth == ImmediateHistoryCapacity)
            return false;
        if (++_immediateHistoryGeneration == 0)
            ++_immediateHistoryGeneration;
        int slot = _immediateHistoryDepth++;
        reservation = new(
            candidate,
            output,
            targetFrameBuffer,
            slot,
            _immediateHistoryGeneration);
        _immediateHistory[slot] = reservation;
        _immediateHistoryTargets[slot] = targetFrameBuffer;
        _immediateHistoryWritten[slot] = false;
        _immediateHistoryGpuWriteUnproven[slot] = false;
        return true;
    }

    internal override void CompleteFrameViewHistoryCandidateAuthoring(
        in RenderFrameViewHistoryBackendReservation reservation,
        bool succeeded)
    {
        int slot = reservation.BackendSlot;
        if ((uint)slot >= (uint)_immediateHistoryDepth ||
            _immediateHistory[slot].BackendGeneration != reservation.BackendGeneration)
            return;

        // Nested captures settle independently. An out-of-order unwind must
        // discard abandoned children rather than publish a parent's writes for them.
        for (int child = _immediateHistoryDepth - 1; child > slot; --child)
            SettleImmediateHistory(child, commit: false);
        SettleImmediateHistory(slot, succeeded && _immediateHistoryWritten[slot]);
        _immediateHistoryDepth = slot;
    }

    private void SettleImmediateHistory(int slot, bool commit)
    {
        RenderFrameViewHistoryCandidateToken candidate = _immediateHistory[slot].Candidate;
        bool onlyUnprovenGpuWrite =
            !commit && !_immediateHistoryWritten[slot] &&
            _immediateHistoryGpuWriteUnproven[slot];
        _immediateHistory[slot] = default;
        _immediateHistoryTargets[slot] = null;
        _immediateHistoryWritten[slot] = false;
        _immediateHistoryGpuWriteUnproven[slot] = false;
        if (commit)
            candidate.Commit();
        else
        {
            candidate.Discard();
            if (onlyUnprovenGpuWrite && Debug.ShouldLogEvery(
                    "OpenGL.FrameViewHistory.UnprovenGpuWrite",
                    TimeSpan.FromSeconds(1)))
            {
                Debug.OpenGLWarning(
                    "[OpenGL][FrameViewHistory] Discarded candidate sequence {0}: only GPU-count or image-dispatch writes were observed, so a non-zero color write to the exact output could not be proven.",
                    candidate.Sequence);
            }
        }
    }

    /// <summary>Tracks draw bindings without synchronous GL state queries.</summary>
    internal void TrackHistoryDrawTarget(XRFrameBuffer? target, bool known = true)
    {
        _historyDrawTarget = target;
        _historyDrawTargetKnown = known;
    }

    /// <summary>Attests an issued native write only for the innermost exact output.</summary>
    internal void MarkImmediateHistoryWrite(XRFrameBuffer? target)
    {
        int slot = _immediateHistoryDepth - 1;
        if (slot >= 0 && ReferenceEquals(_immediateHistoryTargets[slot], target) &&
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline is { } pipeline &&
            pipeline.TemporalHistoryPipelineIdentity == _immediateHistory[slot].Candidate.PipelineIdentity &&
            unchecked((ulong)pipeline.ResourceGeneration) == _immediateHistory[slot].Output.Target.TargetGeneration)
            _immediateHistoryWritten[slot] = true;
    }

    private void MarkImmediateHistoryBoundWrite()
    {
        if (_historyDrawTargetKnown)
            MarkImmediateHistoryWrite(_historyDrawTarget);
    }

    /// <summary>
    /// Records that a GPU-defined draw count or image dispatch may have written
    /// the active output. This deliberately does not attest history freshness:
    /// the CPU cannot prove a non-zero draw or an image-to-FBO alias here.
    /// </summary>
    internal void MarkImmediateHistoryGpuWriteUnproven()
    {
        int slot = _immediateHistoryDepth - 1;
        if (slot >= 0 && _historyDrawTargetKnown &&
            ReferenceEquals(_immediateHistoryTargets[slot], _historyDrawTarget))
        {
            _immediateHistoryGpuWriteUnproven[slot] = true;
        }
    }

    private void DiscardImmediateHistory()
    {
        while (_immediateHistoryDepth > 0)
            SettleImmediateHistory(--_immediateHistoryDepth, commit: false);
        TrackHistoryDrawTarget(null, known: false);
    }
}

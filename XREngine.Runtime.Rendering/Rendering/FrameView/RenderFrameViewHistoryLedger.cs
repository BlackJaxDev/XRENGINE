using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Freezes desktop current view state during collection and resolves history only at authoring.</summary>
internal sealed class RenderFrameViewHistoryLedger
{
    private readonly object _sync = new();
    private readonly Pending[] _pending = new Pending[3];
    private Committed _committed;
    private ulong _rejectedSequenceHighWater;
    private ulong _generation = 1UL;
    private ulong _nextCandidateId;
    private ulong _effectiveCommitCount;
    private ulong _effectiveDiscardCount;

    public RenderFrameViewDescriptor Capture(ulong sequence, ulong sourceFrame, IRuntimeRenderCamera camera,
        ulong pipelineIdentity, ulong extentRevision, ulong outputIdentity, bool authoring,
        in RenderFrameViewDescriptor current, out bool accepted,
        out RenderFrameViewHistoryCandidateToken candidate)
    {
        lock (_sync)
        {
            candidate = default;
            int index = Find(sequence);
            if (index >= 0)
            {
                ref Pending pending = ref _pending[index];
                if (!authoring)
                {
                    if (pending.SourceFrame != sourceFrame ||
                        !Matches(pending, camera, pipelineIdentity, extentRevision, outputIdentity, current))
                    {
                        ReleasePending(index, countDiscard: true);
                        _rejectedSequenceHighWater = Math.Max(_rejectedSequenceHighWater, sequence);
                        accepted = false;
                        return Unavailable(current);
                    }

                    accepted = true;
                    return Provisional(pending.Descriptor);
                }
                if (pending.SourceFrame != sourceFrame ||
                    !Matches(pending, camera, pipelineIdentity, extentRevision, outputIdentity, current))
                {
                    ReleasePending(index, countDiscard: true);
                    _rejectedSequenceHighWater = Math.Max(_rejectedSequenceHighWater, sequence);
                    accepted = false;
                    return Unavailable(current);
                }
                if (!pending.AuthoringResolved)
                {
                    pending.Descriptor = Resolve(pending);
                    pending.AuthoringResolved = true;
                }
                if (pending.CandidateId == 0UL)
                    pending.CandidateId = AllocateCandidateId();
                accepted = true;
                candidate = CreateCandidate(in pending);
                return pending.Descriptor;
            }
            if ((_committed.Occupied && sequence <= _committed.Sequence) ||
                sequence <= _rejectedSequenceHighWater)
            {
                accepted = false;
                return Unavailable(current);
            }
            index = Available();
            if (index < 0)
            {
                _rejectedSequenceHighWater = Math.Max(_rejectedSequenceHighWater, sequence);
                accepted = false;
                return Unavailable(current);
            }
            var created = new Pending(true, sequence, sourceFrame, camera, camera.TemporalHistoryEpoch,
                pipelineIdentity, extentRevision, outputIdentity, current, false);
            if (authoring)
            {
                created.Descriptor = Resolve(created);
                created.AuthoringResolved = true;
                created.CandidateId = AllocateCandidateId();
            }
            _pending[index] = created;
            accepted = true;
            if (authoring)
                candidate = CreateCandidate(in created);
            return authoring ? created.Descriptor : Provisional(current);
        }
    }

    internal void Commit(in RenderFrameViewHistoryCandidateToken candidate)
    {
        lock (_sync)
        {
            if (!Owns(in candidate, out int index) || !_pending[index].AuthoringResolved)
                return;
            ulong sequence = candidate.Sequence;
            if (_committed.Occupied && sequence <= _committed.Sequence)
            {
                ReleasePending(index, countDiscard: true);
                return;
            }
            Pending pending = _pending[index];
            RenderFrameViewDescriptor d = pending.Descriptor;
            _committed = new(true, sequence, pending.SourceFrame, pending.Camera, pending.CameraEpoch, pending.PipelineIdentity,
                pending.ExtentRevision, pending.OutputIdentity, d.EffectiveHistoryKey, d.ViewRect, d.DepthZeroToOne,
                d.ReversedDepth, d.ProjectionMatrixUnjittered, d.ViewProjectionMatrix, UnjitteredVp(d), d.CurrentJitter,
                RenderFrameViewHistoryPolicy.GetPoseVector(d.CameraPositionAndNear),
                RenderFrameViewHistoryPolicy.GetPoseVector(d.CameraForwardAndFar));
            _pending[index] = default;
            IncrementSaturating(ref _effectiveCommitCount);
            for (int i = 0; i < _pending.Length; i++)
                if (_pending[i].Occupied && _pending[i].Sequence <= sequence)
                    ReleasePending(i, countDiscard: true);
        }
    }

    internal void Discard(in RenderFrameViewHistoryCandidateToken candidate)
    {
        lock (_sync)
        {
            if (Owns(in candidate, out int index))
            {
                ReleasePending(index, countDiscard: true);
                _rejectedSequenceHighWater = Math.Max(_rejectedSequenceHighWater, candidate.Sequence);
            }
        }
    }

    internal void DiscardUnresolved(ulong sequence)
    {
        lock (_sync)
        {
            int index = Find(sequence);
            if (index < 0 || _pending[index].CandidateId != 0UL)
                return;
            ReleasePending(index, countDiscard: false);
            _rejectedSequenceHighWater = Math.Max(_rejectedSequenceHighWater, sequence);
        }
    }

    internal RenderFrameViewHistorySnapshot CaptureSnapshot()
    {
        lock (_sync)
        {
            int pendingCount = 0;
            ulong minimumSequence = ulong.MaxValue;
            ulong maximumSequence = 0UL;
            for (int i = 0; i < _pending.Length; i++)
            {
                ref readonly Pending pending = ref _pending[i];
                if (!pending.Occupied)
                    continue;

                pendingCount++;
                minimumSequence = Math.Min(minimumSequence, pending.Sequence);
                maximumSequence = Math.Max(maximumSequence, pending.Sequence);
            }

            return new RenderFrameViewHistorySnapshot(
                _generation,
                _committed.Occupied,
                _committed.Sequence,
                _committed.SourceFrame,
                pendingCount,
                pendingCount == 0 ? 0UL : minimumSequence,
                maximumSequence,
                _effectiveCommitCount,
                _effectiveDiscardCount);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            for (int i = 0; i < _pending.Length; i++)
                ReleasePending(i, countDiscard: true);
            _committed = default;
            _rejectedSequenceHighWater = 0UL;
            _generation = NextNonZero(_generation);
        }
    }

    private RenderFrameViewHistoryCandidateToken CreateCandidate(in Pending pending)
        => new(this, _generation, pending.CandidateId, pending.Sequence, pending.SourceFrame,
            pending.PipelineIdentity, pending.ExtentRevision, pending.OutputIdentity);

    private ulong AllocateCandidateId()
    {
        _nextCandidateId = NextNonZero(_nextCandidateId);
        return _nextCandidateId;
    }

    private void ReleasePending(int index, bool countDiscard)
    {
        if (countDiscard && _pending[index].Occupied && _pending[index].CandidateId != 0UL)
            IncrementSaturating(ref _effectiveDiscardCount);
        _pending[index] = default;
    }

    private static void IncrementSaturating(ref ulong counter)
    {
        if (counter != ulong.MaxValue)
            counter++;
    }

    private bool Owns(in RenderFrameViewHistoryCandidateToken candidate, out int index)
    {
        index = -1;
        if (!candidate.IsValid || !candidate.IsOwnedBy(this) ||
            candidate.LedgerGeneration != _generation)
            return false;
        index = Find(candidate.Sequence);
        if (index < 0)
            return false;
        ref readonly Pending pending = ref _pending[index];
        return pending.CandidateId == candidate.CandidateId &&
            pending.SourceFrame == candidate.SourceFrame &&
            pending.PipelineIdentity == candidate.PipelineIdentity &&
            pending.ExtentRevision == candidate.ExtentRevision &&
            pending.OutputIdentity == candidate.OutputIdentity;
    }

    private static ulong NextNonZero(ulong value) => value == ulong.MaxValue ? 1UL : value + 1UL;

    private RenderFrameViewDescriptor Resolve(in Pending pending)
    {
        ERenderFrameViewHistoryStatus status;
        Vector3 position = RenderFrameViewHistoryPolicy.GetPoseVector(pending.Descriptor.CameraPositionAndNear);
        Vector3 forward = RenderFrameViewHistoryPolicy.GetPoseVector(pending.Descriptor.CameraForwardAndFar);
        if (pending.CameraEpoch == 0UL || !RenderFrameViewHistoryPolicy.IsPoseValid(position, forward))
            status = ERenderFrameViewHistoryStatus.TrackingInvalid;
        else if (!_committed.Occupied)
            status = ERenderFrameViewHistoryStatus.FirstObservation;
        else if (!ReferenceEquals(pending.Camera, _committed.Camera) || pending.Descriptor.ProjectionMatrixUnjittered != _committed.Projection)
            status = ERenderFrameViewHistoryStatus.CameraChanged;
        else if (pending.Camera.TemporalHistoryEpoch != pending.CameraEpoch || pending.CameraEpoch != _committed.CameraEpoch ||
            RenderFrameViewHistoryPolicy.IsDiscontinuity(position, forward, _committed.Position, _committed.Forward))
            status = ERenderFrameViewHistoryStatus.CameraCut;
        else if (pending.Descriptor.EffectiveHistoryKey != _committed.HistoryKey || pending.Descriptor.ViewRect != _committed.Rect ||
            pending.Descriptor.DepthZeroToOne != _committed.DepthZeroToOne || pending.Descriptor.ReversedDepth != _committed.ReversedDepth ||
            pending.PipelineIdentity != _committed.PipelineIdentity || pending.ExtentRevision != _committed.ExtentRevision || pending.OutputIdentity != _committed.OutputIdentity)
            status = ERenderFrameViewHistoryStatus.OutputChanged;
        else
            status = pending.Sequence == _committed.Sequence + 1UL ? ERenderFrameViewHistoryStatus.Valid : ERenderFrameViewHistoryStatus.FrameGap;
        bool valid = status == ERenderFrameViewHistoryStatus.Valid;
        RenderFrameViewDescriptor d = pending.Descriptor;
        return d with
        {
            PreviousViewProjectionMatrix = valid ? _committed.ViewProjection : d.ViewProjectionMatrix,
            PreviousViewProjectionMatrixUnjittered = valid ? _committed.UnjitteredViewProjection : UnjitteredVp(d),
            PreviousJitter = valid ? _committed.Jitter : d.CurrentJitter,
            HistoryStatus = status,
        };
    }

    private static bool Matches(in Pending p, IRuntimeRenderCamera camera, ulong pipelineIdentity, ulong extentRevision,
        ulong outputIdentity, in RenderFrameViewDescriptor d)
        => ReferenceEquals(p.Camera, camera) && p.PipelineIdentity == pipelineIdentity && p.ExtentRevision == extentRevision &&
            p.OutputIdentity == outputIdentity && p.Descriptor.EffectiveHistoryKey == d.EffectiveHistoryKey && p.Descriptor.Kind == d.Kind &&
            p.Descriptor.ViewRect == d.ViewRect && p.Descriptor.DepthZeroToOne == d.DepthZeroToOne && p.Descriptor.ReversedDepth == d.ReversedDepth &&
            p.Descriptor.ProjectionMatrixUnjittered == d.ProjectionMatrixUnjittered;

    private int Find(ulong sequence)
    {
        for (int i = 0; i < _pending.Length; i++) if (_pending[i].Occupied && _pending[i].Sequence == sequence) return i;
        return -1;
    }
    private int Available()
    {
        for (int i = 0; i < _pending.Length; i++) if (!_pending[i].Occupied) return i;
        return -1;
    }
    private static Matrix4x4 UnjitteredVp(in RenderFrameViewDescriptor d) => d.ViewMatrix * d.ProjectionMatrixUnjittered;
    private static RenderFrameViewDescriptor Provisional(in RenderFrameViewDescriptor d) => Unavailable(d);
    private static RenderFrameViewDescriptor Unavailable(in RenderFrameViewDescriptor d) => d with
    {
        PreviousViewProjectionMatrix = d.ViewProjectionMatrix,
        PreviousViewProjectionMatrixUnjittered = UnjitteredVp(d),
        PreviousJitter = d.CurrentJitter,
        HistoryStatus = ERenderFrameViewHistoryStatus.Unavailable,
    };

    private record struct Pending(bool Occupied, ulong Sequence, ulong SourceFrame, IRuntimeRenderCamera Camera, ulong CameraEpoch,
        ulong PipelineIdentity, ulong ExtentRevision, ulong OutputIdentity, RenderFrameViewDescriptor Descriptor, bool AuthoringResolved)
    {
        internal ulong CandidateId;
    }
    private readonly record struct Committed(bool Occupied, ulong Sequence, ulong SourceFrame, IRuntimeRenderCamera Camera, ulong CameraEpoch,
        ulong PipelineIdentity, ulong ExtentRevision, ulong OutputIdentity, ulong HistoryKey, RenderFrameViewRect Rect,
        bool DepthZeroToOne, bool ReversedDepth, Matrix4x4 Projection, Matrix4x4 ViewProjection,
        Matrix4x4 UnjitteredViewProjection, Vector2 Jitter, Vector3 Position, Vector3 Forward);
}

using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Freezes desktop current view state during collection and resolves history only at authoring.</summary>
internal sealed class RenderFrameViewHistoryLedger
{
    private readonly object _sync = new();
    private readonly Pending[] _pending = new Pending[3];
    private Committed _committed;
    private ulong _rejectedSequenceHighWater;

    public RenderFrameViewDescriptor Capture(ulong sequence, ulong sourceFrame, IRuntimeRenderCamera camera,
        ulong pipelineIdentity, ulong extentRevision, ulong outputIdentity, bool authoring,
        in RenderFrameViewDescriptor current, out bool accepted)
    {
        lock (_sync)
        {
            int index = Find(sequence);
            if (index >= 0)
            {
                ref Pending pending = ref _pending[index];
                if (!authoring)
                {
                    if (pending.SourceFrame != sourceFrame ||
                        !Matches(pending, camera, pipelineIdentity, extentRevision, outputIdentity, current))
                    {
                        pending = default;
                        _rejectedSequenceHighWater = Math.Max(_rejectedSequenceHighWater, sequence);
                        accepted = false;
                        return Unavailable(current);
                    }

                    accepted = true;
                    return Provisional(pending.Descriptor);
                }
                if (!Matches(pending, camera, pipelineIdentity, extentRevision, outputIdentity, current))
                {
                    pending = default;
                    _rejectedSequenceHighWater = Math.Max(_rejectedSequenceHighWater, sequence);
                    accepted = false;
                    return Unavailable(current);
                }
                if (!pending.AuthoringResolved)
                {
                    pending.Descriptor = Resolve(pending);
                    pending.AuthoringResolved = true;
                }
                accepted = true;
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
            }
            _pending[index] = created;
            accepted = true;
            return authoring ? created.Descriptor : Provisional(current);
        }
    }

    public void Commit(ulong sequence)
    {
        lock (_sync)
        {
            int index = Find(sequence);
            if (index < 0 || !_pending[index].AuthoringResolved)
                return;
            if (_committed.Occupied && sequence <= _committed.Sequence)
            {
                _pending[index] = default;
                return;
            }
            Pending pending = _pending[index];
            RenderFrameViewDescriptor d = pending.Descriptor;
            _committed = new(true, sequence, pending.Camera, pending.CameraEpoch, pending.PipelineIdentity,
                pending.ExtentRevision, pending.OutputIdentity, d.EffectiveHistoryKey, d.ViewRect, d.DepthZeroToOne,
                d.ReversedDepth, d.ProjectionMatrixUnjittered, d.ViewProjectionMatrix, UnjitteredVp(d), d.CurrentJitter);
            _pending[index] = default;
            for (int i = 0; i < _pending.Length; i++)
                if (_pending[i].Occupied && _pending[i].Sequence <= sequence)
                    _pending[i] = default;
        }
    }

    public void Discard(ulong sequence)
    {
        lock (_sync)
        {
            int index = Find(sequence);
            if (index >= 0)
                _pending[index] = default;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_pending);
            _committed = default;
            _rejectedSequenceHighWater = 0UL;
        }
    }

    private RenderFrameViewDescriptor Resolve(in Pending pending)
    {
        ERenderFrameViewHistoryStatus status;
        if (pending.CameraEpoch == 0UL)
            status = ERenderFrameViewHistoryStatus.TrackingInvalid;
        else if (!_committed.Occupied)
            status = ERenderFrameViewHistoryStatus.FirstObservation;
        else if (!ReferenceEquals(pending.Camera, _committed.Camera) || pending.Descriptor.ProjectionMatrixUnjittered != _committed.Projection)
            status = ERenderFrameViewHistoryStatus.CameraChanged;
        else if (pending.Camera.TemporalHistoryEpoch != pending.CameraEpoch || pending.CameraEpoch != _committed.CameraEpoch)
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
        ulong PipelineIdentity, ulong ExtentRevision, ulong OutputIdentity, RenderFrameViewDescriptor Descriptor, bool AuthoringResolved);
    private readonly record struct Committed(bool Occupied, ulong Sequence, IRuntimeRenderCamera Camera, ulong CameraEpoch,
        ulong PipelineIdentity, ulong ExtentRevision, ulong OutputIdentity, ulong HistoryKey, RenderFrameViewRect Rect,
        bool DepthZeroToOne, bool ReversedDepth, Matrix4x4 Projection, Matrix4x4 ViewProjection,
        Matrix4x4 UnjitteredViewProjection, Vector2 Jitter);
}

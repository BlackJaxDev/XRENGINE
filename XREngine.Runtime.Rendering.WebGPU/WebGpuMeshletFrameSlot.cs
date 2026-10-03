using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Completion-retained publication and bounded generated geometry for one atomic frame.</summary>
internal sealed class WebGpuMeshletFrameSlot(WebGpuRendererHost renderer) : IDisposable
{
    private const int MaximumDraws = 256;
    private readonly List<WebGpuMeshletWork> _work = new(32);
    private GpuMeshSubmissionPublicationLease _lease;
    private int _drawCount;
    internal uint RecordingSequence { get; private set; }
    internal uint SubmittedSequence { get; private set; }
    internal bool IsAvailable => RecordingSequence == 0 && SubmittedSequence == 0 && !_lease.IsValid;
    internal GPUScene? Scene { get; private set; }
    internal GpuMeshSubmissionPublication Publication => _lease.Publication;

    internal void Begin(GPUScene scene, GpuMeshSubmissionPublicationLease lease, uint sequence)
    {
        if (!IsAvailable || !lease.IsValid || sequence == 0)
            throw new InvalidOperationException("WebGPU.Meshlets.SlotOwned: recorded or GPU-owned meshlet work cannot be overwritten.");
        Scene = scene;
        _lease = lease;
        RecordingSequence = sequence;
        _drawCount = 0;
    }

    internal WebGpuMeshletWork NextWork()
    {
        if (_drawCount == MaximumDraws)
            throw new NotSupportedException("WebGPU.Meshlets.DrawCapacity: one atomic scene submission exceeds 256 retained generated-index draws.");
        if (_drawCount == _work.Count) _work.Add(new(renderer));
        return _work[_drawCount++];
    }

    internal void EndRecording(uint sequence, bool submitted)
    {
        if (RecordingSequence != sequence) return;
        if (submitted) { SubmittedSequence = sequence; RecordingSequence = 0; }
        else Release();
    }

    internal void Reclaim(uint completed)
    {
        if (SubmittedSequence != 0 && SubmittedSequence <= completed) Release();
    }

    private void Release()
    {
        _lease.Dispose();
        _lease = default;
        Scene = null;
        RecordingSequence = 0;
        SubmittedSequence = 0;
    }

    internal void ReleaseDrawUsing(AbstractRenderAPIObject resource)
    {
        foreach (WebGpuMeshletWork work in _work) work.ReleaseDrawUsing(resource);
    }

    internal void ReleaseCommandsUsingHandle(AbstractRenderAPIObject resource, int handle)
    {
        foreach (WebGpuMeshletWork work in _work) work.ReleaseCommandsUsingHandle(resource, handle);
    }

    internal void ReleaseCommandUsing(WebGpuRenderProgram program, WebGpuBindingSet bindings)
    {
        foreach (WebGpuMeshletWork work in _work) work.ReleaseCommandUsing(program, bindings);
    }

    public void Dispose()
    {
        foreach (WebGpuMeshletWork work in _work) work.Dispose();
        _work.Clear();
        Release();
    }
}

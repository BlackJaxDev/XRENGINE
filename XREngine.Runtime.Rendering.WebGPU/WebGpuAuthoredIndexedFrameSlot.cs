using System.Text;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Completion-retained publication and bounded indirect or generated-index work for one atomic frame.</summary>
internal sealed class WebGpuAuthoredIndexedFrameSlot(WebGpuRendererHost renderer) : IDisposable
{
    internal const int MaximumSources = 256;
    internal const int MaximumDraws = 256;
    private readonly List<WebGpuMeshletWork> _work = new(32);
    private readonly List<WebGpuIndirectWork> _indirectWork = new(32);
    private readonly List<WebGpuAuthoredIndexedLodSelection> _lodSelections = new(32);
    private readonly List<WebGpuAuthoredOrderingBatch> _orderings = new(4);
    private readonly List<WebGpuAuthoredRasterSnapshot> _directSnapshots = new(8);
    private GpuMeshSubmissionPublicationLease _lease;
    private int _drawCount;
    private int _meshletDrawCount;
    private int _indirectDrawCount;
    private int _sourceCount;
    private int _orderingCount;
    private int _directSnapshotCount;
    internal uint RecordingSequence { get; private set; }
    internal uint SubmittedSequence { get; private set; }
    internal bool IsAvailable => RecordingSequence == 0 && SubmittedSequence == 0 && !_lease.IsValid;
    internal GPUScene? Scene { get; private set; }
    internal GpuMeshSubmissionPublication Publication => _lease.Publication;

    /// <summary>Appends retained indexed owners for an explicit between-frame diagnostic capture.</summary>
    internal void AppendCacheDiagnostics(StringBuilder output, int slot)
    {
        output.Append("{\"slot\":").Append(slot).Append(",\"selections\":[");
        for (int i = 0; i < _lodSelections.Count; i++)
        {
            if (i != 0) output.Append(',');
            WebGpuOwnedStorageBuffer selected = _lodSelections[i].Selected;
            output.Append("{\"index\":").Append(i)
                .Append(",\"handle\":").Append(selected.ResourceHandle)
                .Append(",\"pendingHandle\":").Append(selected.PendingResourceHandle)
                .Append(",\"pendingRequest\":");
            AppendPendingBufferRequest(output, selected);
            output.Append('}');
        }
        output.Append("],\"works\":[");
        for (int i = 0; i < _indirectWork.Count; i++)
        {
            if (i != 0) output.Append(',');
            WebGpuIndirectWork work = _indirectWork[i];
            output.Append("{\"index\":").Append(i)
                .Append(",\"arguments\":").Append(work.Arguments.ResourceHandle)
                .Append(",\"pendingArguments\":").Append(work.Arguments.PendingResourceHandle)
                .Append(",\"pendingArgumentsRequest\":");
            AppendPendingBufferRequest(output, work.Arguments);
            output.Append(",\"pipeline\":").Append(work.DiagnosticPipelineHandle)
                .Append(",\"outputGeneration\":").Append(work.DiagnosticOutputGeneration)
                .Append('}');
        }
        output.Append("]}");
    }

    private static void AppendPendingBufferRequest(StringBuilder output, WebGpuOwnedStorageBuffer buffer)
    {
        if (buffer.PendingResourceRequest is not { } request)
        {
            output.Append("null");
            return;
        }
        if (!ReferenceEquals(request.Owner, buffer) || request.OwnerGeneration != buffer.OwnerGeneration || request.Kind != 1)
            throw new InvalidOperationException("WebGPU.AuthoredIndexed.DiagnosticOwner: a pending buffer must retain its exact allocation request.");
        output.Append("{\"identity\":").Append(request.Identity)
            .Append(",\"ownerGeneration\":").Append(request.OwnerGeneration)
            .Append(",\"state\":").Append((int)request.State)
            .Append(",\"descriptor\":").Append(request.Json)
            .Append('}');
    }

    internal void Begin(GPUScene scene, GpuMeshSubmissionPublicationLease lease, uint sequence)
    {
        if (!IsAvailable || !lease.IsValid || sequence == 0)
            throw new InvalidOperationException("WebGPU.AuthoredIndexed.SlotOwned: recorded or GPU-owned authored indexed work cannot be overwritten.");
        Scene = scene;
        _lease = lease;
        RecordingSequence = sequence;
        _drawCount = 0;
        _meshletDrawCount = 0;
        _indirectDrawCount = 0;
        _sourceCount = 0;
        _orderingCount = 0;
        _directSnapshotCount = 0;
    }

    internal WebGpuAuthoredOrderingBatch NextOrdering()
    {
        if (_orderingCount == 64)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.PassCapacity: one atomic scene submission exceeds 64 ordered passes.");
        if (_orderingCount == _orderings.Count) _orderings.Add(new(renderer));
        WebGpuAuthoredOrderingBatch ordering = _orderings[_orderingCount++];
        ordering.Begin();
        return ordering;
    }

    internal WebGpuAuthoredRasterSnapshot NextDirectSnapshot()
    {
        RequireCandidateCapacity(1);
        _drawCount++;
        if (_directSnapshotCount == _directSnapshots.Count) _directSnapshots.Add(new(renderer));
        return _directSnapshots[_directSnapshotCount++];
    }

    internal WebGpuAuthoredIndexedLodSelection NextLodSelection()
    {
        if (_sourceCount == MaximumSources)
            throw new NotSupportedException("WebGPU.AuthoredIndexed.SourceCapacity: one atomic scene submission exceeds 256 retained source/view selections.");
        if (_sourceCount == _lodSelections.Count) _lodSelections.Add(new(renderer));
        return _lodSelections[_sourceCount++];
    }

    internal WebGpuMeshletWork NextMeshletWork()
    {
        RequireCandidateCapacity(1);
        _drawCount++;
        if (_meshletDrawCount == _work.Count) _work.Add(new(renderer));
        return _work[_meshletDrawCount++];
    }

    internal WebGpuIndirectWork NextIndirectWork()
    {
        RequireCandidateCapacity(1);
        _drawCount++;
        if (_indirectDrawCount == _indirectWork.Count) _indirectWork.Add(new(renderer));
        return _indirectWork[_indirectDrawCount++];
    }

    internal void RequireCandidateCapacity(int count)
    {
        if (count < 0 || count > MaximumDraws - _drawCount)
            throw new NotSupportedException("WebGPU.AuthoredIndexed.DrawCapacity: resident LOD candidates exceed the remaining completion-owned draw capacity.");
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
        foreach (WebGpuAuthoredRasterSnapshot snapshot in _directSnapshots) snapshot.ReleaseDrawUsing(resource);
        foreach (WebGpuMeshletWork work in _work) work.ReleaseDrawUsing(resource);
        foreach (WebGpuIndirectWork work in _indirectWork) work.ReleaseDrawUsing(resource);
    }

    internal void ReleaseCommandsUsingHandle(AbstractRenderAPIObject resource, int handle)
    {
        foreach (WebGpuAuthoredRasterSnapshot snapshot in _directSnapshots) snapshot.ReleaseCommandsUsingHandle(resource, handle);
        foreach (WebGpuMeshletWork work in _work) work.ReleaseCommandsUsingHandle(resource, handle);
        foreach (WebGpuIndirectWork work in _indirectWork) work.ReleaseCommandsUsingHandle(resource, handle);
    }

    internal void ReleaseCommandUsing(WebGpuRenderProgram program, WebGpuBindingSet bindings)
    {
        foreach (WebGpuAuthoredRasterSnapshot snapshot in _directSnapshots) snapshot.ReleaseCommandUsing(bindings);
        foreach (WebGpuMeshletWork work in _work) work.ReleaseCommandUsing(program, bindings);
        foreach (WebGpuIndirectWork work in _indirectWork) work.ReleaseCommandUsing(program, bindings);
    }

    public void Dispose()
    {
        foreach (WebGpuMeshletWork work in _work) work.Dispose();
        foreach (WebGpuIndirectWork work in _indirectWork) work.Dispose();
        foreach (WebGpuAuthoredIndexedLodSelection selection in _lodSelections) selection.Dispose();
        foreach (WebGpuAuthoredOrderingBatch ordering in _orderings) ordering.Dispose();
        foreach (WebGpuAuthoredRasterSnapshot snapshot in _directSnapshots) snapshot.Dispose();
        _work.Clear();
        _indirectWork.Clear();
        _lodSelections.Clear();
        _orderings.Clear();
        _directSnapshots.Clear();
        Release();
    }
}

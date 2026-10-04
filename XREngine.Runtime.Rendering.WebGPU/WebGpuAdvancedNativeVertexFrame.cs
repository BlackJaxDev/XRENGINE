using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Completion-slot-owned geometry overlay and immutable local-vertex dispatch inputs.</summary>
internal sealed class WebGpuAdvancedNativeVertexFrame : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly WebGpuAdvancedGeometryArena _image = new();
    private bool _resident;
    private bool _staged;
    internal WebGpuAdvancedNativeVertexJob[] Jobs = [];
    private int[] _jobIndices = [];
    internal int Count;
    internal bool Executed;
    internal bool AggregateCopied;
    internal ulong WorldFrameId;
    internal uint RecordingSequence;

    internal WebGpuAdvancedNativeVertexFrame(WebGpuRendererHost renderer, int slot)
    {
        _renderer = renderer;
        Geometry = new(renderer, $"Advanced material vertex geometry {slot}");
    }

    internal WebGpuOwnedStorageBuffer Geometry { get; }
    internal bool HasWork => Count != 0;

    internal void Begin(int payloadCount, ulong frameId, uint sequence)
    {
        if (Jobs.Length < payloadCount)
        {
            Array.Resize(ref Jobs, payloadCount);
            Array.Resize(ref _jobIndices, payloadCount);
        }
        // A reused slot is completion-owned by the associated scene slot.
        Array.Clear(Jobs, 0, Count);
        _jobIndices.AsSpan(0, payloadCount).Fill(-1);
        Count = 0;
        Executed = false;
        AggregateCopied = false;
        WorldFrameId = frameId;
        RecordingSequence = sequence;
    }

    internal void Add(in WebGpuAdvancedNativeVertexJob job)
    {
        _jobIndices[job.PayloadIndex] = Count;
        Jobs[Count++] = job;
    }

    internal bool TryGet(int payloadIndex, out WebGpuAdvancedNativeVertexJob job)
    {
        int index = payloadIndex < _jobIndices.Length ? _jobIndices[payloadIndex] : -1;
        job = index >= 0 && index < Count ? Jobs[index] : default;
        return index >= 0 && index < Count;
    }

    internal void PrepareGeometry(AdvancedGpuScenePublicationSnapshot snapshot, uint currentBytes, uint previousBytes)
    {
        if (!HasWork) return;
        if (_resident && _image.Matches(snapshot.GeometryPayloads, snapshot.DatabaseEpoch, currentBytes, previousBytes))
            return;
        _resident = false;
        _image.Pack(snapshot.GeometryPayloads, snapshot.DatabaseEpoch, _renderer.MaximumAdvancedStorageBytes, currentBytes, previousBytes);
        Geometry.EnsureCapacity(_image.Bytes.Length);
        Geometry.UploadPreparation(_image.Bytes);
        _staged = true;
    }

    internal void EndRecording(bool submitted)
    {
        if (_staged && submitted) _resident = true;
        _staged = false;
        RecordingSequence = 0;
    }

    public void Dispose()
    {
        Array.Clear(Jobs);
        Geometry.Dispose();
    }
}

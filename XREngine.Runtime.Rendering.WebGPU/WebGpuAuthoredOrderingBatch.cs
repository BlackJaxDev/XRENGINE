using System.Buffers.Binary;
using System.Numerics;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>One bounded GPU source ordering and rank-major authored candidate replay.</summary>
internal sealed class WebGpuAuthoredOrderingBatch(WebGpuRendererHost renderer) : IDisposable
{
    private readonly GpuMeshSubmissionOrderSource[] _sources = new GpuMeshSubmissionOrderSource[64];
    private readonly WebGpuAuthoredRasterSnapshot?[] _candidates = new WebGpuAuthoredRasterSnapshot?[256];
    private readonly int[] _candidateSources = new int[256];
    private readonly int[] _candidatePrimitives = new int[256];
    private readonly byte[] _sourceBytes = new byte[64 * 64];
    private readonly WebGpuOwnedStorageBuffer _sourceStorage = new(renderer, "Authored source sort inputs");
    private int _candidateCount;
    internal WebGpuOwnedStorageBuffer Ranks { get; } = new(renderer, "Authored GPU source ranks");
    internal int SourceCount { get; private set; }

    internal void Begin()
    {
        SourceCount = 0;
        Array.Clear(_sources);
        Array.Clear(_candidates);
        _candidateCount = 0;
    }

    internal int AddSource(in GpuMeshSubmissionOrderSource source)
    {
        for (int index = 0; index < SourceCount; index++)
        {
            if (ReferenceEquals(_sources[index].Source, source.Source)) return index;
            if (_sources[index].InsertionOrder == source.InsertionOrder)
                throw new NotSupportedException("WebGPU.AuthoredOrdering.DuplicateToken: distinct sources cannot share a collection insertion token.");
        }
        if (SourceCount == _sources.Length)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.SourceCapacity: ordered GPU submission admits at most 64 logical sources per pass.");
        _sources[SourceCount] = source;
        return SourceCount++;
    }

    internal int FindSource(IRenderCommandMesh source)
    {
        for (int index = 0; index < SourceCount; index++)
            if (ReferenceEquals(_sources[index].Source, source)) return index;
        throw new NotSupportedException("WebGPU.AuthoredOrdering.SourceMissing: a candidate has no complete matching collection insertion publication.");
    }

    internal void Record(GpuMeshSubmissionOrderPublication publication, WebGpuRenderProgram program)
    {
        if (SourceCount == 0) return;
        _sourceStorage.EnsureCapacity(SourceCount * 64);
        Ranks.EnsureCapacity(SourceCount * 4);
        Span<byte> bytes = _sourceBytes.AsSpan(0, SourceCount * 64);
        bytes.Clear();
        for (int index = 0; index < SourceCount; index++)
        {
            ref readonly GpuMeshSubmissionOrderSource source = ref _sources[index];
            Span<byte> row = bytes.Slice(index * 64, 64);
            WriteVector(row, source.BoundsMin);
            BinaryPrimitives.WriteUInt32LittleEndian(row[12..], source.HasBounds ? 1u : 0u);
            WriteVector(row[16..], source.BoundsMax);
            WriteVector(row[32..], source.FallbackPosition);
            BinaryPrimitives.WriteInt32LittleEndian(row[44..], source.Priority);
            BinaryPrimitives.WriteUInt64LittleEndian(row[48..], source.InsertionOrder);
        }
        _sourceStorage.StageUpload(bytes);
        try
        {
            program.SetNativeBindingCacheOwner(Ranks);
            program.BindStorageBuffer(0, _sourceStorage);
            program.BindStorageBuffer(1, Ranks);
            program.Data.Uniform("CameraPosition", renderer.RequireFrozenView().View.CameraPositionAndNear);
            program.Data.Uniform("SourceCount", checked((uint)SourceCount));
            program.Data.Uniform("SortPolicy", checked((uint)publication.SortPolicy));
            program.Data.Uniform("UsePriority", publication.UsePriority ? 1u : 0u);
            program.Data.Uniform("Reserved", 0u);
            program.RecordCompute(1, 1, 1);
        }
        finally { program.ClearTransientComputeBindings(); }
    }

    internal void AddCandidate(WebGpuAuthoredRasterSnapshot snapshot, int sourceIndex, int primitive)
    {
        if (_candidateCount == _candidates.Length)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.CandidateCapacity: ordered GPU submission admits at most 256 authored candidates.");
        int index = _candidateCount++;
        // Resident dense compaction cannot change the authored primitive sequence.
        // Source rank remains entirely GPU-produced; this orders only known input topology.
        while (index > 0 && (_candidateSources[index - 1] > sourceIndex ||
            _candidateSources[index - 1] == sourceIndex && _candidatePrimitives[index - 1] > primitive))
        {
            _candidates[index] = _candidates[index - 1];
            _candidateSources[index] = _candidateSources[index - 1];
            _candidatePrimitives[index] = _candidatePrimitives[index - 1];
            index--;
        }
        _candidates[index] = snapshot;
        _candidateSources[index] = sourceIndex;
        _candidatePrimitives[index] = primitive;
    }

    internal void RecordRaster()
    {
        renderer.RequireAuthoredOrderingCommandCapacity(checked(SourceCount * _candidateCount));
        for (int rank = 0; rank < SourceCount; rank++)
            for (int candidate = 0; candidate < _candidateCount; candidate++)
                _candidates[candidate]!.RecordRank(rank);
    }

    private static void WriteVector(Span<byte> row, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(row, value.X);
        BinaryPrimitives.WriteSingleLittleEndian(row[4..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(row[8..], value.Z);
    }

    public void Dispose()
    {
        _sourceStorage.Dispose();
        Ranks.Dispose();
        Begin();
    }
}

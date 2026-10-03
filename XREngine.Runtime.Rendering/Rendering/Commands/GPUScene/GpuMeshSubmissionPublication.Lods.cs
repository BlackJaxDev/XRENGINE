namespace XREngine.Rendering.Commands;

public sealed partial class GpuMeshSubmissionPublication
{
    private GpuMeshSubmissionRecord[] _lodRecords = [];
    private GpuMeshSubmissionSourceBindings?[] _lodBindingClosures = [];
    private GpuMeshSubmissionSourceBindings?[] _outlineBindingClosures = [];
    private readonly Dictionary<IRenderCommandMesh, IRenderCommandMesh> _materialAuxiliarySources = new(ReferenceEqualityComparer.Instance);
    private int[] _authoredLodCounts = [];

    /// <summary>Returns the authored source count, including levels beyond the bounded GPU table.</summary>
    public int GetAuthoredLodCount(int recordIndex) => _authoredLodCounts[recordIndex];

    /// <summary>Identifies CPU-current auxiliary work replaced only by the atomically admitted GPU candidate set.</summary>
    public bool IsGpuOwnedMaterialAuxiliary(IRenderCommandMesh command)
        => _materialAuxiliarySources.TryGetValue(command, out IRenderCommandMesh? primary) &&
            GetSourceOwnership(primary) == EGpuMeshSubmissionSourceOwnership.Gpu;

    /// <summary>Auxiliary material geometry must follow the GPU-selected LOD before CPU replay is safe.</summary>
    public bool RequiresLodAuxiliaryPassPublication()
    {
        foreach (ref readonly GpuMeshSubmissionRecord source in Records)
            if (source.InstanceCount != 0 && source.RequiresLodAuxiliaryPassPublication &&
                GetSourceOwnership(source.Source) != EGpuMeshSubmissionSourceOwnership.ExplicitCpu) return true;
        return false;
    }

    /// <summary>Four frozen resident candidates; an absent resident level has a default record.</summary>
    public ReadOnlySpan<GpuMeshSubmissionRecord> GetLodCandidates(int recordIndex)
        => _lodRecords.AsSpan(checked(recordIndex * GPUScene.MaxLogicalMeshLodCount), GPUScene.MaxLogicalMeshLodCount);

    /// <summary>Includes every frozen authored candidate pass even when the CPU-current source uses another pass.</summary>
    public bool IncludesRenderPass(int recordIndex, int renderPass)
    {
        ref readonly GpuMeshSubmissionRecord source = ref _records[recordIndex];
        if (renderPass < 0 || source.RenderPass < 0 || source.RenderPass == renderPass) return true;
        if (source.LodCount <= 1) return false;
        foreach (ref readonly GpuMeshSubmissionRecord candidate in GetLodCandidates(recordIndex))
            if (candidate.Mesh is not null && (candidate.RenderPass < 0 || candidate.RenderPass == renderPass ||
                candidate.OutlinePass is { Enabled: true } outline && (outline.RenderPass < 0 || outline.RenderPass == renderPass))) return true;
        return false;
    }

    private void BeginLodCapture(int count)
    {
        int capacity = checked(_records.Length * GPUScene.MaxLogicalMeshLodCount);
        if (_lodRecords.Length < capacity)
        {
            Array.Resize(ref _lodRecords, capacity);
            Array.Resize(ref _lodBindingClosures, capacity);
            Array.Resize(ref _outlineBindingClosures, capacity);
            Array.Resize(ref _authoredLodCounts, _records.Length);
        }
        Array.Clear(_lodRecords, 0, checked(Math.Max(count, Count) * GPUScene.MaxLogicalMeshLodCount));
        Array.Clear(_authoredLodCounts, 0, Math.Max(count, Count));
        _materialAuxiliarySources.Clear();
    }

    internal void SetAuthoredLodCount(int recordIndex, int count) => _authoredLodCounts[recordIndex] = count;

    internal void CaptureLodCandidate(int recordIndex, int level, in GpuMeshSubmissionRecord record)
    {
        int index = checked(recordIndex * GPUScene.MaxLogicalMeshLodCount + level);
        GpuMeshSubmissionSourceBindings bindings = record.SourceBindings.CapturePublication(_lodBindingClosures[index]);
        _lodBindingClosures[index] = bindings;
        GpuMeshSubmissionSourceBindings? outlineBindings = record.OutlineSourceBindings?.CapturePublication(_outlineBindingClosures[index]);
        if (outlineBindings is not null) _outlineBindingClosures[index] = outlineBindings;
        _lodRecords[index] = record with { SourceBindings = bindings, OutlineSourceBindings = outlineBindings };
    }

    private void CaptureMaterialAuxiliarySources()
    {
        _materialAuxiliarySources.EnsureCapacity(Count);
        foreach (ref readonly GpuMeshSubmissionRecord record in Records)
            if (record.MaterialOutlineCommand is { } auxiliary)
                _materialAuxiliarySources[auxiliary] = record.Source;
    }

    private void ClearLodSources()
    {
        int count = checked(Count * GPUScene.MaxLogicalMeshLodCount);
        for (int index = 0; index < count; index++)
        {
            _lodBindingClosures[index]?.ReleaseRetainedSources();
            _outlineBindingClosures[index]?.ReleaseRetainedSources();
        }
        Array.Clear(_lodRecords, 0, count);
        Array.Clear(_authoredLodCounts, 0, Count);
        _materialAuxiliarySources.Clear();
    }
}

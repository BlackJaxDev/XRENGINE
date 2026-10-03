using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

public sealed partial class AdvancedGpuDeformationResources
{
    /// <summary>Exact input sections in the bounded packed aggregate ABI; output remains separately owned.</summary>
    public const int PackedInputSectionCount = 13;
    private readonly Dictionary<AdvancedGpuHandle, AdvancedDeformationJobControls> _controlsByPose = [];
    private XRDataBuffer<AdvancedDeformationJobControls>[] _controlBuffers = [];
    private AdvancedDeformationJobControls[] _controlScratch = [];

    internal void SetJobControls(AdvancedGpuHandle pose, XRMeshRenderer renderer)
    {
        if (!UsesPackedAggregateInputs)
            throw new InvalidOperationException("Authored aggregate controls require the packed backend capability.");
        int cap = renderer.ActiveSkinningInfluenceCap;
        float threshold = renderer.BlendshapeActiveWeightThreshold;
        if (!float.IsFinite(threshold) || threshold < 0)
            throw new NotSupportedException("Aggregate deformation requires a finite nonnegative morph weight threshold.");
        if (!_controlsByPose.ContainsKey(pose) && _controlsByPose.Count >= _controlScratch.Length)
            throw new NotSupportedException("Aggregate deformation exceeds its bounded authored-control capacity.");
        _controlsByPose[pose] = new(cap <= 0 ? uint.MaxValue : checked((uint)cap), threshold);
    }

    private void InitializeJobControls(int maximumJobs)
    {
        _controlsByPose.EnsureCapacity(maximumJobs);
        _controlScratch = new AdvancedDeformationJobControls[maximumJobs];
        _controlBuffers = new XRDataBuffer<AdvancedDeformationJobControls>[_frameSlotCount];
        for (int slot = 0; slot < _frameSlotCount; slot++)
            _controlBuffers[slot] = CreateDynamicBuffer<AdvancedDeformationJobControls>(
                $"AdvancedDeformation.Controls.Slot{slot}", checked((uint)maximumJobs));
    }

    private void PublishJobControls(ReadOnlySpan<AdvancedDeformationJobRecord> jobs)
    {
        for (int index = 0; index < jobs.Length; index++)
        {
            if (!_controlsByPose.TryGetValue(jobs[index].SharedPose, out var controls))
                throw new InvalidOperationException("A canonical aggregate job has no captured authored deformation controls.");
            _controlScratch[index] = controls;
        }
        if (!jobs.IsEmpty)
            _controlBuffers[_currentFrameSlot].Write(0, _controlScratch.AsSpan(0, jobs.Length));
    }

    /// <summary>Returns canonical CPU-authored input storage and exact used bytes without mapping GPU output.</summary>
    public XRDataBuffer GetPackedInputSection(int section, out uint byteLength)
    {
        if (!UsesPackedAggregateInputs)
            throw new InvalidOperationException("Packed aggregate inputs require the native backend capability.");
        XRDataBuffer buffer;
        uint count;
        switch (section)
        {
            case 0: buffer = Publication.Jobs; count = Publication.JobCount; break;
            case 1: buffer = Publication.GroupedJobIndices; count = Publication.GroupedJobCount; break;
            case 2: buffer = Publication.GroupedJobVertexOffsets; count = Publication.GroupedJobCount; break;
            case 3: buffer = _staticBuffers.SourceVertices; count = _sourceVertexCount; break;
            case 4: buffer = _staticBuffers.SkinInfluences; count = _skinInfluenceCount; break;
            case 5: buffer = _paletteBuffers[_currentFrameSlot]; count = _paletteCount; break;
            case 6: buffer = _staticBuffers.InverseBindMatrices; count = 1; break;
            case 7: buffer = _activeBlendshapeBuffers[_currentFrameSlot]; count = _activeBlendshapeCount; break;
            case 8: buffer = _staticBuffers.BlendshapeDeltas; count = _blendshapeDeltaCount; break;
            case 9: buffer = _staticBuffers.SpillInfluences; count = _spillInfluenceCount; break;
            case 10: buffer = _staticBuffers.BlendshapeRanges; count = _blendshapeRangeCount; break;
            case 11: buffer = _staticBuffers.BlendshapeRecords; count = _blendshapeRecordCount; break;
            case 12: buffer = _controlBuffers[_currentFrameSlot]; count = Publication.JobCount; break;
            default: throw new ArgumentOutOfRangeException(nameof(section));
        }
        byteLength = checked(count * buffer.ElementSize);
        return buffer;
    }
}

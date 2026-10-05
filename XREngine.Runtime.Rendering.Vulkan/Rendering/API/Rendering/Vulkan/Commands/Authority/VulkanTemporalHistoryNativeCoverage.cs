using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>Tracks full native history copies made by one primary recording.</summary>
internal struct VulkanTemporalHistoryNativeCoverage
{
    public bool Active;
    public TemporalHistorySubmissionCandidate Candidate;
    public int TemporalResolveOriginalIndex;
    public int TsrResolveOriginalIndex;
    public bool TemporalResolveRecorded;
    public bool TsrResolveRecorded;
    public TemporalHistoryResourceIdentity TemporalResolveColor;
    public TemporalHistoryResourceIdentity TemporalResolveAuxiliary;
    public TemporalHistoryResourceIdentity TsrResolveColor;
    public TemporalHistoryResourceIdentity TsrResolveAuxiliary;
    public uint TemporalResolveColorIndex;
    public uint TsrResolveColorIndex;
    public uint TemporalResolveAuxiliaryIndex;
    public uint TsrResolveAuxiliaryIndex;
    public TemporalHistoryResourceIdentity ColorCopySource;
    public TemporalHistoryResourceIdentity TsrColorCopySource;
    public TemporalHistoryResourceIdentity MetadataCopySource;
    public TemporalHistoryResourceIdentity ExposureCopySource;
    public uint TemporalResolveWidth;
    public uint TemporalResolveHeight;
    public uint TsrResolveWidth;
    public uint TsrResolveHeight;
    public uint ColorLayerMask;
    public uint DepthLayerMask;
    public uint TsrColorLayerMask;
    public uint MetadataLayerMask;
    public uint ExposureLayerMask;
}

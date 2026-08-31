namespace XREngine.Rendering;

/// <summary>
/// Latest opt-in GPU counter evidence retained for one render pass.
/// Values are <see langword="null"/> when the diagnostic readback was unavailable.
/// </summary>
public sealed record VulkanGpuCounterPassDiagnostic(
    int RenderPass,
    ulong FrameId,
    string Point,
    uint? CulledDrawCount = null,
    uint? CulledInstanceCount = null,
    uint? CulledOverflowCount = null,
    uint? DrawCount = null,
    uint? PerViewCandidateCount = null,
    uint? MaterialScatterInputCount = null,
    uint? MaterialScatterCulledCount = null,
    uint? MaterialScatterKeyCount = null,
    uint? MaterialScatterEmittedCount = null,
    uint? MaterialScatterRejectedDrawId = null,
    uint? MaterialScatterRejectedMaterial = null,
    uint? MaterialScatterRejectedMesh = null,
    uint? MaterialScatterRejectedAtlas = null,
    uint? MaterialScatterRejectedBucket = null,
    uint? MaterialScatterRejectedBounds = null,
    uint? GpuStaticAtlasIndexCount = null,
    uint? GpuDynamicAtlasIndexCount = null,
    uint? GpuStreamingAtlasIndexCount = null,
    uint? GpuStaticAtlasVertexCount = null,
    uint? GpuDynamicAtlasVertexCount = null,
    uint? GpuStreamingAtlasVertexCount = null,
    uint? RejectedAtlasIndexEmpty = null,
    uint? RejectedAtlasVertexEmpty = null,
    uint? RejectedAtlasFirstIndex = null,
    uint? RejectedAtlasIndexRange = null,
    uint? RejectedAtlasBaseVertex = null,
    uint? LastBucketIndex = null,
    uint? LastBucketCount = null,
    uint? LastCommandIndexCount = null,
    uint? LastCommandInstanceCount = null);

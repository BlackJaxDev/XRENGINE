using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Exact resident publication and authored pass requested by the shared command graph.</summary>
public readonly record struct MeshletIndexedBackendRequest(
    GPUScene Scene,
    ulong FrameId,
    int RenderPass,
    int RenderGraphPassIndex,
    XRCamera Camera,
    EMeshSubmissionStrategy SubmissionStrategy);

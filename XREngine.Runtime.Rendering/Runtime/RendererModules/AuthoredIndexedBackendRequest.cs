using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Exact resident publication and authored pass requested by the shared command graph.</summary>
public readonly record struct AuthoredIndexedBackendRequest(
    GPUScene Scene,
    ulong FrameId,
    int RenderPass,
    int RenderGraphPassIndex,
    XRCamera Camera,
    RenderFrameViewSelection View,
    EMeshSubmissionStrategy SubmissionStrategy);

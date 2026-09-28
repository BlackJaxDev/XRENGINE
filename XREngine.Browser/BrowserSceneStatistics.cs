using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Cold-path diagnostic snapshot; visibility counts describe the last collected frame across its views.</summary>
public sealed record BrowserSceneStatistics(
    bool CullingEnabled,
    int VisibilityCandidates,
    int VisibilityCulled,
    int VisibilityDrawn,
    int RetainedMeshCount,
    int RetainedMaterialCount,
    int RetainedTextureCount,
    double VariableDeltaSeconds,
    uint HistoryGeneration,
    RenderFrameOutputDescription? Output);

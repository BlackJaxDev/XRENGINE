namespace XREngine.Rendering;

/// <summary>All accepted layer receipts and their unpublished CPU-backed atlas, owned until finalization completes.</summary>
public sealed record SceneCaptureFinalizeRequest(XRTexture2DArray Color,
    IReadOnlyList<SceneCaptureReadback> Layers, Func<bool> IsCurrent);

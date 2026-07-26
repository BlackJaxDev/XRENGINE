namespace XREngine.Rendering;

/// <summary>
/// Produces an ImGui-compatible texture handle without exposing backend wrappers.
/// </summary>
public interface IRenderTexturePreviewBackendCapability
{
    bool TryGetTexturePreviewHandle(
        XRTexture texture,
        in RenderTexturePreviewOptions options,
        out nint handle,
        out bool requiresVerticalFlip,
        out string? failureReason);

    /// <summary>
    /// Rebuilds backend-owned ImGui font resources when supported.
    /// </summary>
    bool RebuildFontAtlas()
        => false;
}

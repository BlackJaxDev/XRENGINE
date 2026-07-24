using XREngine.Rendering;

namespace XREngine.Editor;

/// <summary>
/// Resolves backend texture interop for editor previews without exposing API wrappers.
/// </summary>
internal static class EditorTexturePreviewService
{
    public static bool TryGetHandle(
        XRTexture texture,
        out nint handle,
        out bool requiresVerticalFlip,
        out string? failureReason)
        => TryGetHandle(
            texture,
            default,
            out handle,
            out requiresVerticalFlip,
            out failureReason);

    public static bool TryGetHandle(
        XRTexture texture,
        in RenderTexturePreviewOptions options,
        out nint handle,
        out bool requiresVerticalFlip,
        out string? failureReason)
    {
        handle = nint.Zero;
        requiresVerticalFlip = false;

        if (!Engine.IsRenderThread)
        {
            failureReason = "Preview unavailable outside the render thread.";
            return false;
        }

        if (!EditorRendererCapabilityResolver.TryGet(out IRenderTexturePreviewBackendCapability capability))
        {
            failureReason = "The active renderer does not provide texture preview interop.";
            return false;
        }

        return capability.TryGetTexturePreviewHandle(
            texture,
            in options,
            out handle,
            out requiresVerticalFlip,
            out failureReason);
    }
}

namespace XREngine.Rendering;

/// <summary>
/// Coarse module capabilities available before a renderer instance or graphics device exists.
/// Device-specific feature support remains on <see cref="IRuntimeRendererHost"/>.
/// </summary>
[Flags]
public enum RendererBackendCapabilities
{
    None = 0,
    DesktopPresentation = 1 << 0,
    HeadlessRendering = 1 << 1,
    OpenXrPresentation = 1 << 2,
    GpuCompute = 1 << 3,
    EditorTextureInterop = 1 << 4,
    SparseTextureStreaming = 1 << 5,
}

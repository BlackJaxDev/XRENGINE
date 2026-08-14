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
    PresentationlessRendering = 1 << 1,
    OpenXrPresentation = 1 << 2,
    GpuCompute = 1 << 3,
    EditorTextureInterop = 1 << 4,
    SparseTextureStreaming = 1 << 5,
    HeadlessWsiPresentation = 1 << 6,
    BrowserCanvasPresentation = 1 << 7,
    BrowserWorkerPresentation = 1 << 8,
    AsyncGpuReadback = 1 << 9,
    ExternalImageSource = 1 << 10,
    WebXrPresentation = 1 << 11,

    [Obsolete("Use PresentationlessRendering or HeadlessWsiPresentation; 'headless' is not a stable execution mode.")]
    HeadlessRendering = PresentationlessRendering,
}

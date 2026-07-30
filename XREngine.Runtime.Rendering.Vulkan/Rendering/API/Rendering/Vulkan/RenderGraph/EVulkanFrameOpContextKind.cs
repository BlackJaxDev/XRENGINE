namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Represents the different kinds of frame operation contexts in the Vulkan renderer.
/// </summary>
internal enum EVulkanFrameOpContextKind
{
    /// <summary>
    /// The context kind is unknown.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// The main viewport context.
    /// </summary>
    MainViewport = 1,
    /// <summary>
    /// The OpenXR eye context.
    /// </summary>
    OpenXrEye = 2,
    /// <summary>
    /// The OpenXR mirror context.
    /// </summary>
    OpenXrMirror = 3,
    /// <summary>
    /// The scene capture context.
    /// </summary>
    SceneCapture = 4,
    /// <summary>
    /// The light probe capture context.
    /// </summary>
    LightProbeCapture = 5,
    /// <summary>
    /// The shadow context.
    /// </summary>
    Shadow = 6,
    /// <summary>
    /// The UI preview context.
    /// </summary>
    UiPreview = 7,
    /// <summary>
    /// The diagnostic capture context.
    /// </summary>
    DiagnosticCapture = 8,
}

using XREngine.Data.Vectors;

namespace XREngine.Rendering;

/// <summary>Portable request for a desktop surface and its graphics context.</summary>
public readonly record struct RuntimeWindowCreateOptions(
    WindowStartupValues Startup,
    RuntimeGraphicsApiKind GraphicsApi,
    EInteractiveWindowResizeStrategy ResizeStrategy,
    RuntimeWindowPurpose Purpose,
    IVector2 Position,
    IVector2 Size,
    bool VSyncEnabled,
    bool Visible,
    bool TopMost,
    bool PreferHdrOutput,
    bool TransparentFramebuffer,
    int ColorBits,
    int DepthBits,
    int StencilBits,
    int OpenGlMajorVersion,
    int OpenGlMinorVersion,
    bool OpenGlDebugContext,
    bool OpenGlForwardCompatible,
    bool SwapAutomatically,
    IRuntimeWindowGlContext? SharedContext = null);

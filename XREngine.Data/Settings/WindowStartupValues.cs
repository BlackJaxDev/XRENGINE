using XREngine.Data.Core;

namespace XREngine;

/// <summary>
/// Backend-neutral authored values for one startup window. Bootstrap combines
/// these values with a target world and renderer policy before asking Rendering
/// to create a native window.
/// </summary>
public readonly record struct WindowStartupValues(
    string? Title,
    int X,
    int Y,
    int Width,
    int Height,
    EWindowState State,
    ELocalPlayerIndexMask LocalPlayers,
    bool VSync,
    bool TransparentFramebuffer,
    bool? OutputHdr,
    bool UseNativeTitleBar,
    RuntimeWindowResizeMode InteractiveResizeMode)
{
    /// <summary>Stable desktop defaults independent of any graphics backend.</summary>
    public static WindowStartupValues Default { get; } = new(
        Title: null,
        X: 0,
        Y: 0,
        Width: 1920,
        Height: 1080,
        State: EWindowState.Windowed,
        LocalPlayers: ELocalPlayerIndexMask.One,
        VSync: false,
        TransparentFramebuffer: false,
        OutputHdr: null,
        UseNativeTitleBar: true,
        InteractiveResizeMode: RuntimeWindowResizeMode.Default);
}

/// <summary>Backend-neutral interactive resize policy selected by Bootstrap.</summary>
public enum RuntimeWindowResizeMode
{
    Default,
    NativeBackend,
    EngineBorderless,
}

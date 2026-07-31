namespace XREngine.Rendering;

/// <summary>
/// Optional presentation-target capability exposing services owned by a desktop
/// application window. Non-window targets must not implement this contract.
/// </summary>
public interface IRendererDesktopWindowServices
{
    /// <summary>Gets the desktop window host that owns native window lifecycle and input.</summary>
    IRuntimeRenderWindowHost Window { get; }
}

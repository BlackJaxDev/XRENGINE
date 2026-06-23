namespace XREngine.Rendering;

/// <summary>
/// Selects how a native window should behave while the user interactively resizes it.
/// </summary>
public enum EInteractiveWindowResizeStrategy
{
    /// <summary>
    /// Preserve the current windowing backend behavior without additional callbacks or hooks.
    /// </summary>
    Default,

    /// <summary>
    /// Use GLFW's native refresh callback to request repaint frames during resize/move exposure.
    /// </summary>
    GlfwRefreshCallback,

    /// <summary>
    /// Render from Silk.NET resize callbacks when the active backend delivers them during border drags.
    /// </summary>
    GlfwResizeCallbackRender,

    /// <summary>
    /// Prefer Silk.NET's SDL windowing backend for this window.
    /// </summary>
    SdlBackend,

    /// <summary>
    /// On Windows, subclass the window and render from a Win32 timer while the move/size modal loop is active.
    /// </summary>
    Win32ModalLoopTimer,

    /// <summary>
    /// Use a borderless engine-owned resize path driven by normal input events.
    /// </summary>
    EngineBorderlessResize,
}

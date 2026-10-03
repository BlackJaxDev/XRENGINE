using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

internal static class DesktopInteractiveResizeHookFactory
{
    public static IDesktopInteractiveResizeHook? Create(EInteractiveWindowResizeStrategy strategy)
        => strategy switch
        {
            EInteractiveWindowResizeStrategy.Default or EInteractiveWindowResizeStrategy.SdlBackend => null,
            EInteractiveWindowResizeStrategy.GlfwRefreshCallback => new DesktopGlfwRefreshResizeHook(),
            EInteractiveWindowResizeStrategy.GlfwResizeCallbackRender => new DesktopGlfwResizeHook(),
            EInteractiveWindowResizeStrategy.Win32ModalLoopTimer => new DesktopWin32ModalResizeHook(),
            EInteractiveWindowResizeStrategy.EngineBorderlessResize => new DesktopBorderlessResizeHook(),
            _ => throw new NotSupportedException($"Desktop interactive resize strategy '{strategy}' is unavailable."),
        };
}

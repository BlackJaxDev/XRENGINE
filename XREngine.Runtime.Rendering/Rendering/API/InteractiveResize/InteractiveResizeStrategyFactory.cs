namespace XREngine.Rendering;

internal static class InteractiveResizeStrategyFactory
{
    public static IInteractiveResizeStrategy Create(EInteractiveWindowResizeStrategy strategy)
        => strategy switch
        {
            EInteractiveWindowResizeStrategy.GlfwRefreshCallback => new GlfwRefreshInteractiveResizeStrategy(),
            EInteractiveWindowResizeStrategy.GlfwResizeCallbackRender => new GlfwResizeCallbackInteractiveResizeStrategy(),
            EInteractiveWindowResizeStrategy.SdlBackend => new SdlBackendInteractiveResizeStrategy(),
            EInteractiveWindowResizeStrategy.Win32ModalLoopTimer => new Win32ModalLoopTimerInteractiveResizeStrategy(),
            EInteractiveWindowResizeStrategy.EngineBorderlessResize => new EngineBorderlessInteractiveResizeStrategy(),
            _ => new DefaultInteractiveResizeStrategy(),
        };
}

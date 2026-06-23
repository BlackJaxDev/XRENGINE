namespace XREngine.Rendering;

public static class InteractiveWindowResizeStrategyUtility
{
    public const string EnvironmentVariableName = "XRE_INTERACTIVE_RESIZE_STRATEGY";

    public static bool TryParse(string? value, out EInteractiveWindowResizeStrategy strategy)
    {
        strategy = EInteractiveWindowResizeStrategy.Default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = Normalize(value);
        switch (normalized)
        {
            case "default":
                strategy = EInteractiveWindowResizeStrategy.Default;
                return true;
            case "glfwrefresh":
            case "glfwrefreshcallback":
                strategy = EInteractiveWindowResizeStrategy.GlfwRefreshCallback;
                return true;
            case "glfwresize":
            case "glfwresizecallback":
            case "glfwresizecallbackrender":
                strategy = EInteractiveWindowResizeStrategy.GlfwResizeCallbackRender;
                return true;
            case "sdl":
            case "sdlbackend":
                strategy = EInteractiveWindowResizeStrategy.SdlBackend;
                return true;
            case "win32":
            case "win32timer":
            case "win32modallooptimer":
                strategy = EInteractiveWindowResizeStrategy.Win32ModalLoopTimer;
                return true;
            case "borderless":
            case "engineborderless":
            case "engineborderlessresize":
                strategy = EInteractiveWindowResizeStrategy.EngineBorderlessResize;
                return true;
            default:
                return Enum.TryParse(value, ignoreCase: true, out strategy);
        }
    }

    public static string ToConfigString(EInteractiveWindowResizeStrategy strategy)
        => strategy switch
        {
            EInteractiveWindowResizeStrategy.Default => "default",
            EInteractiveWindowResizeStrategy.GlfwRefreshCallback => "glfw-refresh",
            EInteractiveWindowResizeStrategy.GlfwResizeCallbackRender => "glfw-resize",
            EInteractiveWindowResizeStrategy.SdlBackend => "sdl",
            EInteractiveWindowResizeStrategy.Win32ModalLoopTimer => "win32-timer",
            EInteractiveWindowResizeStrategy.EngineBorderlessResize => "borderless",
            _ => strategy.ToString(),
        };

    public static string ResolveEnvironmentValue()
        => Environment.GetEnvironmentVariable(EnvironmentVariableName) ?? string.Empty;

    private static string Normalize(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c is '-' or '_' or ' ' or '\t')
                continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }
}

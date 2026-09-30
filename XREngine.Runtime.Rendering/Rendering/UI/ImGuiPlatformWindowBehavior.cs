namespace XREngine.Rendering.UI;

/// <summary>Routes platform viewport behavior through the explicitly installed UI capability.</summary>
public static class ImGuiPlatformWindowBehavior
{
    private static IImGuiPlatformWindowServices? _services;

    public static IImGuiPlatformWindowServices? Services
    {
        get => Volatile.Read(ref _services);
        set => Volatile.Write(ref _services, value);
    }

    private static IImGuiPlatformWindowServices Required => Services ??
        throw new NotSupportedException("Dear ImGui platform-window services are not installed in this host.");

    public static bool IsInputTransparent(uint flags) => Required.IsInputTransparent(flags);
    public static void ConfigureNativeWindow(nint windowHandle, uint flags) => Required.ConfigureNativeWindow(windowHandle, flags);
    public static void ReleaseNativeWindow(nint windowHandle) => Required.ReleaseNativeWindow(windowHandle);
    public static bool TryShowWithoutActivation(nint windowHandle, uint flags) => Required.TryShowWithoutActivation(windowHandle, flags);
}

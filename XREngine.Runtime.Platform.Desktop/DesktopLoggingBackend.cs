using XREngine.Runtime.Diagnostics.Native;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Registers native file logging for desktop hosts.</summary>
public static class DesktopLoggingBackend
{
    public static void EnsureRegistered() => NativeDebugBackendRegistration.EnsureRegistered();
}

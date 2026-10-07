namespace XREngine.Runtime.Diagnostics.Native;

/// <summary>Registers the native log backend before shared Debug services are used.</summary>
public static class NativeDebugBackendRegistration
{
    private static readonly NativeDebugLogBackend Backend = new();

    /// <summary>Registers the built-in backend when no custom provider is installed.</summary>
    public static void EnsureRegistered()
        => RuntimeDebugLogBackendServices.RegisterIfAbsent(Backend);
}

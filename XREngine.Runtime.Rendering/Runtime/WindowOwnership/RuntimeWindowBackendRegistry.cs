namespace XREngine.Rendering;

/// <summary>Holds the desktop window factory installed by application composition.</summary>
public static class RuntimeWindowBackendRegistry
{
    private static IRuntimeWindowBackendFactory? _factory;

    public static void Install(IRuntimeWindowBackendFactory factory)
        => Volatile.Write(ref _factory, factory ?? throw new ArgumentNullException(nameof(factory)));

    public static IRuntimeWindowBackendFactory RequireFactory()
        => Volatile.Read(ref _factory) ?? throw new InvalidOperationException(
            "A desktop window backend is not installed. Register XREngine.Runtime.Platform.Desktop before creating desktop windows.");
}

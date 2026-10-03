namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Explicitly installed factory for the native OpenXR runtime implementation.</summary>
public static class OpenXrRuntimeServices
{
    private static Func<IOpenXrRuntime>? _factory;

    public static void Register(Func<IOpenXrRuntime> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public static IOpenXrRuntime Create()
        => (_factory ?? throw new InvalidOperationException(
            "The OpenXR runtime module has not been registered."))();
}

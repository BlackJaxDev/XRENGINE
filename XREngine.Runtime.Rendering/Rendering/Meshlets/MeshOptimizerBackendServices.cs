namespace XREngine.Rendering.Meshlets;

/// <summary>Stores the mesh processing provider installed by the application or cook host.</summary>
public static class MeshOptimizerBackendServices
{
    private static IMeshOptimizerBackend? _current;

    public static IMeshOptimizerBackend? Current => Volatile.Read(ref _current);

    public static IMeshOptimizerBackend Required => Current ?? throw new InvalidOperationException(
        "Mesh optimization requires an installed mesh processing backend. Install the meshoptimizer module in the application or cook host.");

    public static void Install(IMeshOptimizerBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        Volatile.Write(ref _current, backend);
    }
}

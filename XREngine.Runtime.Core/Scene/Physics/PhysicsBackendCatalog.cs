namespace XREngine.Scene.Physics;

/// <summary>
/// Stores physics modules installed by an application during composition.
/// </summary>
public sealed class PhysicsBackendCatalog
{
    private readonly Dictionary<EPhysicsLibrary, IPhysicsBackendModule> _modules = [];

    public void Register(IPhysicsBackendModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!_modules.TryAdd(module.Id, module))
            throw new InvalidOperationException($"Physics backend '{module.Id}' is already registered.");
    }

    public bool TryGet(EPhysicsLibrary id, out IPhysicsBackendModule? module)
        => _modules.TryGetValue(id, out module);

    public AbstractPhysicsScene CreateRequired(EPhysicsLibrary id)
    {
        if (!_modules.TryGetValue(id, out IPhysicsBackendModule? module))
            throw new InvalidOperationException(
                $"Physics backend '{id}' is not installed. Register its module in the application composition before selecting it.");

        PhysicsBackendPlatforms platform = OperatingSystem.IsBrowser() ? PhysicsBackendPlatforms.Browser :
            OperatingSystem.IsWindows() ? PhysicsBackendPlatforms.Windows :
            OperatingSystem.IsLinux() ? PhysicsBackendPlatforms.Linux : PhysicsBackendPlatforms.MacOS;
        if ((module.SupportedPlatforms & platform) == 0)
            throw new PlatformNotSupportedException(
                $"Physics backend '{module.DisplayName}' does not support this platform ({platform}).");

        return module.CreateScene() ?? throw new InvalidOperationException(
            $"Physics backend '{module.DisplayName}' returned no scene.");
    }
}

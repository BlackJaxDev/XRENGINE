namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Thread-safe registry that snapshots backend descriptors in deterministic resolver order.
/// </summary>
public sealed class ModelImportBackendRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ModelImportBackendDescriptor> _descriptors = new(StringComparer.Ordinal);

    public ModelImportBackendRegistry(IEnumerable<ModelImportBackendDescriptor>? descriptors = null)
    {
        if (descriptors is null)
            return;

        foreach (ModelImportBackendDescriptor descriptor in descriptors)
            Register(descriptor);
    }

    /// <summary>
    /// Gets the process registry populated with the built-in ModelingBridge backends.
    /// Upper-layer composition roots may register their own descriptors during startup.
    /// </summary>
    public static ModelImportBackendRegistry Default { get; } = new(ModelImportBackendDescriptors.BuiltIns);

    /// <summary>
    /// Registers a descriptor. Stable backend identities cannot be replaced in place.
    /// </summary>
    public void Register(ModelImportBackendDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (_sync)
        {
            if (!_descriptors.TryAdd(descriptor.StableId, descriptor))
                throw new InvalidOperationException($"A model import backend with stable ID '{descriptor.StableId}' is already registered.");
        }
    }

    /// <summary>
    /// Registers a descriptor when its stable identity is not already present.
    /// Composition roots use this for idempotent adapter registration.
    /// </summary>
    public bool TryRegister(ModelImportBackendDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (_sync)
            return _descriptors.TryAdd(descriptor.StableId, descriptor);
    }

    /// <summary>
    /// Gets an immutable descriptor snapshot sorted by descending priority and then stable ID.
    /// </summary>
    public IReadOnlyList<ModelImportBackendDescriptor> GetSnapshot()
    {
        lock (_sync)
        {
            ModelImportBackendDescriptor[] snapshot = _descriptors.Values
                .OrderByDescending(static descriptor => descriptor.Priority)
                .ThenBy(static descriptor => descriptor.StableId, StringComparer.Ordinal)
                .ToArray();
            return Array.AsReadOnly(snapshot);
        }
    }
}

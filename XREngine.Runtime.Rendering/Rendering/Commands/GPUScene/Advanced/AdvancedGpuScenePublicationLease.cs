namespace XREngine.Rendering.Commands;

/// <summary>
/// Allocation-free token retaining one sealed publication. Copies share a
/// generation-checked database slot, so only the first disposal releases it.
/// </summary>
public readonly struct AdvancedGpuScenePublicationLease : IDisposable
{
    private readonly AdvancedSharedGpuSceneDatabase? _database;
    private readonly uint _slot;
    private readonly uint _generation;

    internal AdvancedGpuScenePublicationLease(
        AdvancedSharedGpuSceneDatabase database,
        uint slot,
        uint generation,
        in AdvancedGpuScenePublicationReference reference)
    {
        _database = database;
        _slot = slot;
        _generation = generation;
        Reference = reference;
    }

    public AdvancedGpuScenePublicationReference Reference { get; }
    internal AdvancedSharedGpuSceneDatabase? Database => _database;
    public bool IsValid => _database is not null && _slot != 0u && Reference.IsValid;
    public void Dispose() => _database?.ReleasePublicationLease(_slot, _generation);
}

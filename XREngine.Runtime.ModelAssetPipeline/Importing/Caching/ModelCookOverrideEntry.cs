using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable authored per-submesh cook policy keyed by imported entity identity.
/// </summary>
public sealed class ModelCookOverrideEntry
{
    private readonly byte[] _canonicalSettings;

    public ModelCookOverrideEntry(
        ImportedEntityKey entityKey,
        MeshOptimizerSubMeshSettings settings)
    {
        ArgumentNullException.ThrowIfNull(entityKey);
        ArgumentNullException.ThrowIfNull(settings);

        EntityKey = entityKey;
        Settings = settings;
        _canonicalSettings = ModelCookCanonicalSettings.Serialize(settings);
    }

    public ImportedEntityKey EntityKey { get; }
    public MeshOptimizerSubMeshSettings Settings { get; }
    public ReadOnlyMemory<byte> CanonicalSettings => _canonicalSettings;

    internal ReadOnlySpan<byte> CanonicalSettingsSpan => _canonicalSettings;
}

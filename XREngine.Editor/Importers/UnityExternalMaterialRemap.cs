namespace XREngine.Scene.Importers;

/// <summary>
/// Material-name remap serialized in a Unity model importer .meta file.
/// </summary>
public sealed class UnityExternalMaterialRemap
{
    public string SourceMaterialName { get; init; } = string.Empty;
    public UnityAssetReference TargetMaterial { get; init; }
}

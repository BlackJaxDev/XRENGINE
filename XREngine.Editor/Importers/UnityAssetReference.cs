namespace XREngine.Scene.Importers;

/// <summary>
/// A Unity YAML object reference, including the source file identifier and external asset GUID.
/// </summary>
public readonly record struct UnityAssetReference(long FileId, string? Guid, int? Type)
{
    public bool HasExternalGuid => !string.IsNullOrWhiteSpace(Guid);
}

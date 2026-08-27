using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Stable Unity serialized object identity. Resolution is deliberately kept
/// separate so importing a clip never depends on a particular project path.
/// </summary>
[MemoryPackable]
public readonly partial record struct UnityAssetReference(
    long FileId,
    string Guid,
    int Type,
    string ResolvedAssetPath = "")
{
    public bool IsNull => FileId == 0 && string.IsNullOrEmpty(Guid);
}

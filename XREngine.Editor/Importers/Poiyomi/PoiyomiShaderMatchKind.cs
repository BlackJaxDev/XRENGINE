namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Identifies the evidence used to recognize a Poiyomi shader.
/// </summary>
public enum PoiyomiShaderMatchKind
{
    NotPoiyomi,
    ExactGuid,
    ExactUnlockedSource,
    ExactLockedSource,
    LockedPropertySignature,
    UnsupportedVersion,
}

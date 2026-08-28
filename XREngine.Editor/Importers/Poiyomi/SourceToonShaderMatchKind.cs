namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Identifies the evidence used to recognize a Poiyomi shader.
/// </summary>
public enum SourceToonShaderMatchKind
{
    NotSourceToon,
    ExactGuid,
    ExactUnlockedSource,
    ExactLockedSource,
    LockedPropertySignature,
    SourceToonFeatureLossSource,
    UnsupportedVersion,
}

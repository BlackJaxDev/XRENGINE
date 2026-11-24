namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Identifies the evidence used to recognize a source toon shader.
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

    /// <summary>A release of an admitted release line that has no exact verified identity.</summary>
    ReleaseLineSource,
}

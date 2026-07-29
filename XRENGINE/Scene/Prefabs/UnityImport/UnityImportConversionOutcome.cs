namespace XREngine.Scene.Prefabs;

/// <summary>
/// Result of converting one reached Unity dependency.
/// </summary>
public enum UnityImportConversionOutcome
{
    Pending,
    Converted,
    Downgraded,
    Reused,
    IgnoredOptional,
    Missing,
    Failed,
}

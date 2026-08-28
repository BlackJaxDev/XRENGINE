namespace XREngine.Animation.Importers;

/// <summary>
/// Describes whether source data was retained and can execute through the
/// native XRE animation path.
/// </summary>
public enum EImportedAnimationCapabilityState
{
    SupportedAndApplied = 0,
    IntentionallyDiscarded = 1,
    RequiresRuntimeAdapter = 2,
    PreservedNotExecutable = 3,
    Unsupported = 4,
}

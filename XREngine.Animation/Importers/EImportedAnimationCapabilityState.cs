namespace XREngine.Animation.Importers;

/// <summary>
/// Describes whether source data was retained and can execute through the
/// native XRE animation path.
/// </summary>
public enum EImportedAnimationCapabilityState
{
    SupportedAndApplied,
    RequiresRuntimeAdapter,
    PreservedNotExecutable,
    Unsupported,
}

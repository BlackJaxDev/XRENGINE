namespace XREngine;

/// <summary>
/// Editor-facing metadata for one environment variable recognized by XREngine.
/// </summary>
public sealed record RuntimeEnvironmentVariableDescriptor(
    string FieldName,
    string Name,
    RuntimeEnvironmentCategory Category,
    RuntimeEnvironmentValueKind ValueKind,
    RuntimeEnvironmentApplyMode ApplyMode,
    bool IsDiagnosticOrValidation,
    bool IsDowngradeOverride,
    string DefaultBehavior);

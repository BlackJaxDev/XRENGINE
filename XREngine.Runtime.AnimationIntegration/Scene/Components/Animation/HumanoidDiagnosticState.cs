namespace XREngine.Components.Animation;

/// <summary>
/// Exact mutable humanoid state retained while a diagnostic evaluator samples the live component.
/// </summary>
internal sealed class HumanoidDiagnosticState
{
    internal Dictionary<EHumanoidValue, float> MuscleValues { get; } = [];
    internal Dictionary<EHumanoidValue, float> RawHumanoidValues { get; } = [];
    internal Dictionary<EHumanoidValue, float> SettingsCurrentValues { get; } = [];
    internal HumanoidImportedBodySample CanonicalImportedBodySample;
    internal HumanoidImportedBodySample CurrentImportedMappedBodySample;
    internal HumanoidImportedBodySample StagedImportedBodySample;
    internal System.Numerics.Vector3 CurrentConvertedBodyTranslationDelta;
    internal System.Numerics.Quaternion CurrentConvertedBodyRotationDelta;
    internal float ImportedBodySampleWeight;
    internal bool IsImportedBodySampleTransactionActive;
    internal bool TransactionHasCanonicalImportedBodySample;
    internal object? CanonicalImportedBodySampleOwner;
    internal object? ImportedBodySampleTransactionOwner;
}

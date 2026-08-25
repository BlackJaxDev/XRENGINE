namespace XREngine.Components.Animation;

using XREngine.Animation.Importers;

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
    internal HumanoidProjectedRootPose CurrentProjectedRootPose;
    internal HumanoidProjectedRootPose PreviousProjectedRootPose;
    internal HumanoidRootMotionDelta CurrentRootMotionDelta;
    internal float ImportedBodySampleWeight;
    internal bool IsImportedBodySampleTransactionActive;
    internal bool TransactionHasCanonicalImportedBodySample;
    internal bool HasPreviousProjectedRootPose;
    internal object? CanonicalImportedBodySampleOwner;
    internal object? ImportedBodySampleTransactionOwner;
    internal object? ProjectedRootMotionOwner;
    internal UnityHumanoidClipRootMotionSettings? ImportedBodyProjectionSettings;
    internal string? ImportedBodyProjectionCalibrationClipName;
}

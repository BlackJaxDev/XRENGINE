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
    internal float[] ProjectionMuscleValues { get; } = new float[95];
    internal float[] CanonicalProjectionMuscleValues { get; } = new float[95];
    internal float[] AppliedMuscleValues { get; } = new float[95];
    internal HumanoidImportedBodySample CanonicalImportedBodySample;
    internal HumanoidImportedBodySample CurrentImportedMappedBodySample;
    internal HumanoidImportedBodySample StagedImportedBodySample;
    internal System.Numerics.Vector3 CurrentConvertedBodyTranslationDelta;
    internal System.Numerics.Quaternion CurrentConvertedBodyRotationDelta;
    internal HumanoidProjectedRootPose CurrentProjectedRootPose;
    internal HumanoidProjectedRootPose BodyAllocationProjectedRootPose;
    internal HumanoidProjectedRootPose PreFeetProjectedRootPose;
    internal HumanoidProjectedRootPose PreFeetBodyAllocationProjectedRootPose;
    internal HumanoidProjectedRootPose PreviousProjectedRootPose;
    internal HumanoidRootMotionDelta CurrentRootMotionDelta;
    internal float ImportedBodySampleWeight;
    internal bool IsImportedBodySampleTransactionActive;
    internal bool TransactionHasCanonicalImportedBodySample;
    internal bool HasPreviousProjectedRootPose;
    internal object? CanonicalImportedBodySampleOwner;
    internal object? ImportedBodySampleTransactionOwner;
    internal object? ProjectedRootMotionOwner;
    internal object? PendingProjectedRootMotionOwner;
    internal object? ActiveImportedBodyProjectionOwner;
    internal object? CanonicalProjectedFeetOwner;
    internal ImportedHumanoidClipRootMotionSettings? ImportedBodyProjectionSettings;
    internal HumanoidProjectedRootPose? ImportedBodyProjectionPrefix;
    internal HumanoidLoopPoseCorrection? ImportedBodyLoopPoseCorrection;
    internal HumanoidLoopPoseCorrection? PendingImportedBodyLoopPoseCorrection;
    internal HumanoidLoopPoseCorrection? ActiveImportedBodyLoopPoseCorrection;
    internal ImportedHumanoidRootMotionPolicy? ActiveImportedBodyProjectionPolicy;
    internal string? ImportedBodyProjectionCalibrationClipName;
    internal float CanonicalProjectedFeetY;
    internal bool HasCanonicalProjectedFeetY;
    internal bool HasPendingProjectedRootMotion;
    internal bool HasProjectionMuscleValueSnapshot;
    internal bool HasCanonicalProjectionMuscleValueSnapshot;
}

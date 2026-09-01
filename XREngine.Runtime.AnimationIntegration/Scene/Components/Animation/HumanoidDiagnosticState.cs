namespace XREngine.Components.Animation;

using System.Numerics;
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
    internal Vector3[] ImportedTranslationDofValues { get; } = new Vector3[21];
    internal uint ImportedTranslationDofMask;
    internal bool HasInvalidImportedTranslationDof;
    internal HumanoidImportedBodySample CanonicalImportedBodySample;
    internal HumanoidImportedBodySample CurrentImportedMappedBodySample;
    internal HumanoidImportedBodySample StagedImportedBodySample;
    internal Vector3 CurrentConvertedBodyTranslationDelta;
    internal Quaternion CurrentConvertedBodyRotationDelta;
    internal HumanoidProjectedRootPose CurrentProjectedRootPose;
    internal HumanoidProjectedRootPose BodyAllocationProjectedRootPose;
    internal HumanoidProjectedRootPose PreFeetProjectedRootPose;
    internal HumanoidProjectedRootPose PreFeetBodyAllocationProjectedRootPose;
    internal HumanoidProjectedRootPose PreviousProjectedRootPose;
    internal HumanoidBodyFrameDiagnosticState BodyFrameDiagnostic;
    internal bool LastNativeFrameAccepted;
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
    internal float CanonicalProjectedFeetY;
    internal bool HasCanonicalProjectedFeetY;
    internal bool HasPendingProjectedRootMotion;
    internal bool HasProjectionMuscleValueSnapshot;
    internal bool HasCanonicalProjectionMuscleValueSnapshot;
}

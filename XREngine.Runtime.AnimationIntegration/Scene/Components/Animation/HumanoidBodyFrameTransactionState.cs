using System.Numerics;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Value snapshot of mutable imported-body and root-motion transaction state.
/// It intentionally excludes muscle buffers and live transforms so a rejected
/// body frame can be restored without allocation or pose writes.
/// </summary>
internal struct HumanoidBodyFrameTransactionState
{
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
    internal ImportedHumanoidProjectionFootGoals ProjectionFootGoals;
    internal ImportedHumanoidProjectionFootGoals CanonicalProjectionFootGoals;
    internal HumanoidStateMachineRootMotionFrame? PendingStateMachineRootMotionFrame;
    internal object? PendingStateMachineRootMotionOwner;
}

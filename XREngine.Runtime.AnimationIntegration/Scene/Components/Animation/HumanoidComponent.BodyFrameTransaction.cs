namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    /// <summary>
    /// Captures mutable body/root bookkeeping before a body input frame is
    /// committed. This is deliberately value-only and does not read or write
    /// live transforms.
    /// </summary>
    private HumanoidBodyFrameTransactionState CaptureBodyFrameTransactionState()
        => new()
        {
            CanonicalImportedBodySample = _canonicalImportedBodySample,
            CurrentImportedMappedBodySample = _currentImportedMappedBodySample,
            StagedImportedBodySample = _stagedImportedBodySample,
            CurrentConvertedBodyTranslationDelta = _currentConvertedBodyTranslationDelta,
            CurrentConvertedBodyRotationDelta = _currentConvertedBodyRotationDelta,
            CurrentProjectedRootPose = _currentProjectedRootPose,
            BodyAllocationProjectedRootPose = _bodyAllocationProjectedRootPose,
            PreFeetProjectedRootPose = _preFeetProjectedRootPose,
            PreFeetBodyAllocationProjectedRootPose = _preFeetBodyAllocationProjectedRootPose,
            PreviousProjectedRootPose = _previousProjectedRootPose,
            CurrentRootMotionDelta = _currentRootMotionDelta,
            ImportedBodySampleWeight = _importedBodySampleWeight,
            IsImportedBodySampleTransactionActive = _isImportedBodySampleTransactionActive,
            TransactionHasCanonicalImportedBodySample = _transactionHasCanonicalImportedBodySample,
            HasPreviousProjectedRootPose = _hasPreviousProjectedRootPose,
            CanonicalImportedBodySampleOwner = _canonicalImportedBodySampleOwner,
            ImportedBodySampleTransactionOwner = _importedBodySampleTransactionOwner,
            ProjectedRootMotionOwner = _projectedRootMotionOwner,
            PendingProjectedRootMotionOwner = _pendingProjectedRootMotionOwner,
            ActiveImportedBodyProjectionOwner = _activeImportedBodyProjectionOwner,
            CanonicalProjectedFeetOwner = _canonicalProjectedFeetOwner,
            ImportedBodyProjectionSettings = _importedBodyProjectionSettings,
            ImportedBodyProjectionPrefix = _importedBodyProjectionPrefix,
            ImportedBodyLoopPoseCorrection = _importedBodyLoopPoseCorrection,
            PendingImportedBodyLoopPoseCorrection = _pendingImportedBodyLoopPoseCorrection,
            ActiveImportedBodyLoopPoseCorrection = _activeImportedBodyLoopPoseCorrection,
            ActiveImportedBodyProjectionPolicy = _activeImportedBodyProjectionPolicy,
            CanonicalProjectedFeetY = _canonicalProjectedFeetY,
            HasCanonicalProjectedFeetY = _hasCanonicalProjectedFeetY,
            HasPendingProjectedRootMotion = _hasPendingProjectedRootMotion,
            HasProjectionMuscleValueSnapshot = _hasProjectionMuscleValueSnapshot,
            HasCanonicalProjectionMuscleValueSnapshot = _hasCanonicalProjectionMuscleValueSnapshot,
            PendingStateMachineRootMotionFrame = _pendingStateMachineRootMotionFrame,
            PendingStateMachineRootMotionOwner = _pendingStateMachineRootMotionOwner,
        };

    /// <summary>
    /// Restores a rejected body input frame's bookkeeping without applying a
    /// skeletal pose or mutating transforms.
    /// </summary>
    private void RestoreBodyFrameTransactionState(in HumanoidBodyFrameTransactionState state)
    {
        _canonicalImportedBodySample = state.CanonicalImportedBodySample;
        _currentImportedMappedBodySample = state.CurrentImportedMappedBodySample;
        _stagedImportedBodySample = state.StagedImportedBodySample;
        _currentConvertedBodyTranslationDelta = state.CurrentConvertedBodyTranslationDelta;
        _currentConvertedBodyRotationDelta = state.CurrentConvertedBodyRotationDelta;
        _currentProjectedRootPose = state.CurrentProjectedRootPose;
        _bodyAllocationProjectedRootPose = state.BodyAllocationProjectedRootPose;
        _preFeetProjectedRootPose = state.PreFeetProjectedRootPose;
        _preFeetBodyAllocationProjectedRootPose = state.PreFeetBodyAllocationProjectedRootPose;
        _previousProjectedRootPose = state.PreviousProjectedRootPose;
        _currentRootMotionDelta = state.CurrentRootMotionDelta;
        _importedBodySampleWeight = state.ImportedBodySampleWeight;
        _isImportedBodySampleTransactionActive = state.IsImportedBodySampleTransactionActive;
        _transactionHasCanonicalImportedBodySample = state.TransactionHasCanonicalImportedBodySample;
        _hasPreviousProjectedRootPose = state.HasPreviousProjectedRootPose;
        _canonicalImportedBodySampleOwner = state.CanonicalImportedBodySampleOwner;
        _importedBodySampleTransactionOwner = state.ImportedBodySampleTransactionOwner;
        _projectedRootMotionOwner = state.ProjectedRootMotionOwner;
        _pendingProjectedRootMotionOwner = state.PendingProjectedRootMotionOwner;
        _activeImportedBodyProjectionOwner = state.ActiveImportedBodyProjectionOwner;
        _canonicalProjectedFeetOwner = state.CanonicalProjectedFeetOwner;
        _importedBodyProjectionSettings = state.ImportedBodyProjectionSettings;
        _importedBodyProjectionPrefix = state.ImportedBodyProjectionPrefix;
        _importedBodyLoopPoseCorrection = state.ImportedBodyLoopPoseCorrection;
        _pendingImportedBodyLoopPoseCorrection = state.PendingImportedBodyLoopPoseCorrection;
        _activeImportedBodyLoopPoseCorrection = state.ActiveImportedBodyLoopPoseCorrection;
        _activeImportedBodyProjectionPolicy = state.ActiveImportedBodyProjectionPolicy;
        _canonicalProjectedFeetY = state.CanonicalProjectedFeetY;
        _hasCanonicalProjectedFeetY = state.HasCanonicalProjectedFeetY;
        _hasPendingProjectedRootMotion = state.HasPendingProjectedRootMotion;
        _hasProjectionMuscleValueSnapshot = state.HasProjectionMuscleValueSnapshot;
        _hasCanonicalProjectionMuscleValueSnapshot = state.HasCanonicalProjectionMuscleValueSnapshot;
        _pendingStateMachineRootMotionFrame = state.PendingStateMachineRootMotionFrame;
        _pendingStateMachineRootMotionOwner = state.PendingStateMachineRootMotionOwner;
    }
}

using System.Numerics;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private HumanoidBodyFrameDiagnosticState _currentBodyFrameDiagnostic;
    private readonly object _manualBodySampleOwner = new();
    private HumanoidProjectedRootPose _stagedRootMotionInputPose;
    private float _stagedRootMotionInputWeight;
    private bool _lastNativeFrameAccepted;

    /// <summary>
    /// Last successfully committed pre-IK Body solve. A rejected frame never
    /// replaces this snapshot with its partially evaluated scratch state.
    /// </summary>
    public HumanoidBodyFrameDiagnosticState CurrentBodyFrameDiagnostic => _currentBodyFrameDiagnostic;

    internal bool WasLastNativeFrameAccepted => _lastNativeFrameAccepted;

    internal void RejectNativeFrameInput() => _lastNativeFrameAccepted = false;

    /// <summary>Only the accepted evaluator may publish this frame's authored root input.</summary>
    internal bool TryGetAcceptedRootMotionInput(object owner, out HumanoidProjectedRootPose pose, out float weight)
    {
        pose = _currentBodyFrameDiagnostic.RootMotionInputPose;
        weight = _currentBodyFrameDiagnostic.RootMotionInputWeight;
        return _lastNativeFrameAccepted && _currentBodyFrameDiagnostic.HasValue
            && _currentBodyFrameDiagnostic.HasRootMotionInput
            && ReferenceEquals(_projectedRootMotionOwner, owner);
    }

    /// <summary>Weights one complete projected pose after loop composition, matching a graph leaf.</summary>
    internal static HumanoidProjectedRootPose WeightProjectedRootPose(HumanoidProjectedRootPose pose, float weight)
        => weight <= 0.0f ? HumanoidProjectedRootPose.Identity : new(
            pose.Position * weight,
            Quaternion.Slerp(Quaternion.Identity, pose.Rotation, weight), pose.Channels);

    private static Matrix4x4 WeightBodyFrame(CompiledHumanoidAvatarDefinition compiled, Matrix4x4 body, float weight)
        => Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(
                Quaternion.Identity, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(body)), weight))
            * Matrix4x4.CreateTranslation(Vector3.Lerp(compiled.BodyDefinition.NeutralBodyFrame.Translation, body.Translation, weight));

    /// <summary>
    /// Uses the committed pre-IK Body, not Hips or post-IK bones. Scene placement
    /// is read at consumption so root motion applied after pose commit is included.
    /// </summary>
    internal bool TryGetCommittedBodyFrameWorld(out Matrix4x4 matrix, out Quaternion rotation)
    {
        matrix = Matrix4x4.Identity;
        rotation = Quaternion.Identity;
        if (!_currentBodyFrameDiagnostic.HasValue || !TryGetCompiledAvatarDefinition(out _))
            return false;
        Transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
        Matrix4x4 body = _currentBodyFrameDiagnostic.CompensatedBodyFrame;
        matrix = body * Transform.WorldMatrix;
        rotation = Quaternion.Normalize(Transform.WorldRotation * Quaternion.CreateFromRotationMatrix(body));
        return HumanoidBodyFrameMath.IsFinite(matrix) && IsFiniteNonZero(rotation);
    }

    private void ApplyManualBodySample(HumanoidImportedBodySample sample)
    {
        lock (_poseEvaluationSyncRoot)
        {
            if (!IsValidImportedBodySample(sample)
                || !BeginImportedBodySampleTransaction(_manualBodySampleOwner, HumanoidImportedBodySample.Neutral, true))
                return;
            _stagedImportedBodySample = sample;
            try
            {
                ApplyMusclePose();
            }
            finally
            {
                CancelActiveImportedBodySampleTransaction();
                ClearImportedTranslationDofState();
            }
        }
    }

    private bool TryPrepareDirectBodyFrame(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> muscles,
        bool reuseCommittedBody,
        out Matrix4x4 requested,
        out Matrix4x4 beforeProjection)
    {
        requested = Matrix4x4.Identity;
        beforeProjection = Matrix4x4.Identity;
        if (_hasPendingProjectedRootMotion
            && _activeImportedBodyProjectionPolicy is ImportedHumanoidRootMotionPolicy
            {
                BakePositionYIntoPose: false,
                PositionYBasis: EImportedHumanoidRootPositionYBasis.Feet,
            })
        {
            if (!TryPrepareCanonicalBodyFeet(compiled))
                return false;
            ReadOnlySpan<float> projectionMuscles = _hasProjectionMuscleValueSnapshot
                ? _projectionMuscleValueSnapshot : muscles;
            if (!TryEvaluateNativeHumanoidPose(compiled, projectionMuscles, includeTranslationDof: true)
                || !TryResolveProjectedFeetFromCurrentPose(compiled))
                return false;
        }

        // Projection sidecars may reuse the scratch buffer. Always finish with
        // the final blended/corrected muscle and TDoF input, never the last leaf.
        if (!TryEvaluateNativeHumanoidPose(compiled, muscles, includeTranslationDof: true))
            return false;
        if (reuseCommittedBody && _currentBodyFrameDiagnostic.HasValue)
        {
            // A muscle-only edit keeps the last accepted Body target, including
            // a blended state target that cannot be reconstructed from one leaf.
            requested = _currentBodyFrameDiagnostic.RequestedBodyFrame;
            beforeProjection = _currentBodyFrameDiagnostic.RequestedBodyBeforeProjection;
            _stagedRootMotionInputPose = _currentBodyFrameDiagnostic.RootMotionInputPose;
            _stagedRootMotionInputWeight = _currentBodyFrameDiagnostic.RootMotionInputWeight;
            return true;
        }
        requested = CalculateRequestedBodyFrame(compiled, _activeImportedBodyProjectionPolicy);
        requested = ApplyImportedBodyLoopPoseCorrection(requested,
            _hasPendingProjectedRootMotion ? _pendingImportedBodyLoopPoseCorrection : _activeImportedBodyLoopPoseCorrection);
        _stagedRootMotionInputPose = _currentProjectedRootPose;
        _stagedRootMotionInputWeight = _importedBodySampleWeight;
        if (_activeImportedBodyProjectionPolicy.HasValue)
        {
            // Body/root allocation and its projection baselines are unit-weight
            // clip inputs, just like state leaves. Blend the resulting target
            // once, independently of final weighted-muscle FK.
            requested = WeightBodyFrame(compiled, requested, _importedBodySampleWeight);
            _currentProjectedRootPose = WeightProjectedRootPose(_currentProjectedRootPose, _importedBodySampleWeight);
            _bodyAllocationProjectedRootPose = WeightProjectedRootPose(_bodyAllocationProjectedRootPose, _importedBodySampleWeight);
            _preFeetProjectedRootPose = WeightProjectedRootPose(_preFeetProjectedRootPose, _importedBodySampleWeight);
            _preFeetBodyAllocationProjectedRootPose = WeightProjectedRootPose(_preFeetBodyAllocationProjectedRootPose, _importedBodySampleWeight);
        }
        beforeProjection = requested * CreateProjectedRootMatrix(_bodyAllocationProjectedRootPose);
        return HumanoidBodyFrameMath.IsRigid(requested) && HumanoidBodyFrameMath.IsRigid(beforeProjection);
    }

    private bool TryPrepareCanonicalBodyFeet(CompiledHumanoidAvatarDefinition compiled)
    {
        if (_hasCanonicalProjectedFeetY && ReferenceEquals(_canonicalProjectedFeetOwner, _pendingProjectedRootMotionOwner))
            return true;
        if (!_hasCanonicalProjectionMuscleValueSnapshot
            || _activeImportedBodyProjectionPolicy is not ImportedHumanoidRootMotionPolicy policy)
            return false;

        HumanoidImportedBodySample current = _currentImportedMappedBodySample;
        HumanoidProjectedRootPose allocation = _bodyAllocationProjectedRootPose;
        try
        {
            _currentImportedMappedBodySample = _canonicalImportedBodySample;
            _bodyAllocationProjectedRootPose = HumanoidProjectedRootPose.Identity;
            if (!TryEvaluateNativeHumanoidPose(compiled, _canonicalProjectionMuscleValueSnapshot, includeTranslationDof: false)
                || !TryCalculateProjectedFeetHeight(compiled, policy, out float canonicalFeet))
                return false;
            _canonicalProjectedFeetY = canonicalFeet;
            _canonicalProjectedFeetOwner = _pendingProjectedRootMotionOwner;
            _hasCanonicalProjectedFeetY = true;
            return true;
        }
        finally
        {
            _currentImportedMappedBodySample = current;
            _bodyAllocationProjectedRootPose = allocation;
        }
    }

    /// <summary>
    /// Converts authored Body channels to one requested rigid frame, not a Hips
    /// delta. Neutral Body is immutable; scale, allocation and root removal each
    /// occur once. Policy inputs are unit-weight targets, weighted once after
    /// projection/loop correction. Skeletal compensation never enters locomotion.
    /// </summary>
    private Matrix4x4 CalculateRequestedBodyFrame(
        CompiledHumanoidAvatarDefinition compiled,
        ImportedHumanoidRootMotionPolicy? policy)
    {
        Vector3 neutralCenter = compiled.BodyDefinition.NeutralBodyFrame.Translation;
        if (policy is not ImportedHumanoidRootMotionPolicy rootPolicy)
            return Matrix4x4.CreateFromQuaternion(_currentConvertedBodyRotationDelta)
                * Matrix4x4.CreateTranslation(neutralCenter + _currentConvertedBodyTranslationDelta);

        Vector3 position = _currentImportedMappedBodySample.Position;
        Quaternion rotation = _currentImportedMappedBodySample.Rotation;
        if (rootPolicy.Mirror)
        {
            position = ImportedHumanoidMirrorOperator.MirrorPosition(position);
            rotation = ImportedHumanoidMirrorOperator.MirrorRotation(rotation);
        }
        Vector3 translation = new Vector3(position.X, position.Z, position.Y)
            * (compiled.HumanScale * compiled.ModelUnitsPerMeter);
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation))
            * Matrix4x4.CreateTranslation(neutralCenter + translation)
            * CreateImportedBodyAllocationMatrix(compiled, rootPolicy, 1.0f)
            * InvertProjectedRootMatrix(_bodyAllocationProjectedRootPose);
    }

    private void PublishBodyFrameDiagnostic(CompiledHumanoidAvatarDefinition compiled, Matrix4x4 beforeProjection, bool hasRootMotionInput)
    {
        _currentBodyFrameDiagnostic = new HumanoidBodyFrameDiagnosticState(
            true, compiled.BodyDefinition.ModelId, compiled.BodyDefinition.AlgorithmVersion,
            _nativePoseWorkspace.ProvisionalBodyFrame, beforeProjection,
            _nativePoseWorkspace.RequestedBodyFrame, _nativePoseWorkspace.CompensatedBodyFrame,
            _nativePoseWorkspace.BodyCompensation,
            _nativePoseWorkspace.GetLocalMatrix(EHumanoidAvatarBoneRole.Hips),
            _nativePoseWorkspace.GetModelRootMatrix(EHumanoidAvatarBoneRole.Hips),
            _currentProjectedRootPose, _stagedRootMotionInputPose, _stagedRootMotionInputWeight, hasRootMotionInput);
    }
}

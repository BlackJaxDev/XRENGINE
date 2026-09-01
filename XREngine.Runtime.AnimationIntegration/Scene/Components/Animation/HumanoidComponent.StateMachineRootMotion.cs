using System.Numerics;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private HumanoidStateMachineRootMotionFrame? _pendingStateMachineRootMotionFrame;
    private object? _pendingStateMachineRootMotionOwner;
    private bool _skipNextScenePoseAfterImmediateStateMachineEvaluation;

    internal void StageStateMachineRootMotionFrame(
        object owner,
        HumanoidStateMachineRootMotionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(frame);
        _lastNativeFrameAccepted = false;
        _skipNextScenePoseAfterImmediateStateMachineEvaluation = false;
        _pendingStateMachineRootMotionOwner = owner;
        _pendingStateMachineRootMotionFrame = frame;
        _activeImportedBodyProjectionPolicy = null;
        _activeImportedBodyProjectionOwner = owner;
        _pendingImportedBodyLoopPoseCorrection = null;
        _hasPendingProjectedRootMotion = false;
    }

    internal void ClearStateMachineRootMotionFrame(object owner)
    {
        if (!ReferenceEquals(_pendingStateMachineRootMotionOwner, owner)
            && !ReferenceEquals(_activeImportedBodyProjectionOwner, owner))
            return;

        _pendingStateMachineRootMotionFrame = null;
        _pendingStateMachineRootMotionOwner = null;
        _activeImportedBodyProjectionOwner = null;
        _activeImportedBodyProjectionPolicy = null;
        _pendingImportedBodyLoopPoseCorrection = null;
        if (ReferenceEquals(_projectedRootMotionOwner, owner))
            ResetProjectedRootMotion();
    }

    /// <summary>
    /// Completes a zero-delta state-machine seek or paused evaluation immediately.
    /// The ordinary scene pose tick must not then consume the same muscle sample a
    /// second time after its per-leaf Body/root frame has already been finalized.
    /// </summary>
    internal void ApplyCurrentStateMachineMusclePoseImmediately()
    {
        lock (_poseEvaluationSyncRoot)
        {
            try
            {
                ApplyMusclePose();
                _skipNextScenePoseAfterImmediateStateMachineEvaluation = true;
            }
            finally
            {
                ClearImportedTranslationDofState();
            }
        }
    }

    /// <summary>
    /// Blends pose-side Body targets, not compensated leaf Hips transforms.
    /// The final muscle/TDoF blend is solved and recentered exactly once.
    /// </summary>
    private bool TryPrepareStateMachineBodyFrame(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> muscles,
        out Matrix4x4 requested,
        out Matrix4x4 beforeProjection)
    {
        requested = Matrix4x4.Identity;
        beforeProjection = Matrix4x4.Identity;
        HumanoidStateMachineRootMotionFrame? frame = _pendingStateMachineRootMotionFrame;
        object? owner = _pendingStateMachineRootMotionOwner;
        if (frame is null || owner is null)
            return false;

        ReadOnlySpan<HumanoidStateMachineRootMotionLeafState?> leaves = frame.Leaves;
        for (int i = 0; i < leaves.Length; i++)
        {
            HumanoidStateMachineRootMotionLeafState? leaf = leaves[i];
            if (leaf is null)
                continue;
            if (!float.IsFinite(leaf.Weight) || leaf.Weight < 0.0f
                || !IsValidImportedBodySample(leaf.CurrentBody)
                || !IsValidImportedBodySample(leaf.CanonicalBody))
                return false;
        }
        if (!TryResolveStateMachineFeetProjection(compiled, frame))
            return false;

        Vector3 basePosition = compiled.BodyDefinition.NeutralBodyFrame.Translation;
        Vector3 position = basePosition;
        Vector3 rotationLog = Vector3.Zero;
        Vector3 convertedPosition = Vector3.Zero;
        Vector3 convertedRotationLog = Vector3.Zero;
        Vector3 projectedPosition = Vector3.Zero;
        Vector3 projectedRotationLog = Vector3.Zero;
        Vector3 withinPosition = Vector3.Zero;
        Vector3 withinRotationLog = Vector3.Zero;
        EHumanoidProjectedRootChannels projectedChannels = EHumanoidProjectedRootChannels.None;
        EHumanoidProjectedRootChannels withinChannels = EHumanoidProjectedRootChannels.None;
        Vector3 mappedPosition = Vector3.Zero;
        Vector4 mappedRotationVector = Vector4.Zero;
        float overrideWeight = 0.0f;
        for (int i = 0; i < leaves.Length; i++)
            if (leaves[i] is { ContributionType: EHumanoidMotionContributionType.Override } leaf)
                overrideWeight += leaf.Weight;
        AccumulateQuaternion(ref mappedRotationVector, Quaternion.Identity, MathF.Max(0.0f, 1.0f - overrideWeight));

        for (int i = 0; i < leaves.Length; i++)
        {
            HumanoidStateMachineRootMotionLeafState? leaf = leaves[i];
            if (leaf is null || leaf.Weight <= 0.0f)
                continue;

            SetLeafBodyEvaluationFields(leaf, useCanonicalSample: false);
            Matrix4x4 target = ApplyImportedBodyLoopPoseCorrection(
                CalculateRequestedBodyFrame(compiled, leaf.Policy), leaf.CurrentLoopPoseCorrection);
            if (!HumanoidBodyFrameMath.IsRigid(target))
                return false;
            Quaternion targetRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(target));
            Vector3 referencePosition = basePosition;
            Quaternion referenceRotation = Quaternion.Identity;
            if (leaf.ContributionType == EHumanoidMotionContributionType.Additive)
            {
                SetLeafBodyEvaluationFields(leaf, useCanonicalSample: true);
                Matrix4x4 reference = CalculateRequestedBodyFrame(compiled, leaf.Policy);
                if (!HumanoidBodyFrameMath.IsRigid(reference))
                    return false;
                referencePosition = reference.Translation;
                referenceRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(reference));
            }

            float weight = leaf.Weight;
            position += (target.Translation - referencePosition) * weight;
            rotationLog += QuaternionLog(Quaternion.Inverse(referenceRotation) * targetRotation) * weight;
            CalculateLeafConvertedBodyDelta(compiled, leaf, out Vector3 convertedDelta, out Quaternion convertedRotation);
            convertedPosition += convertedDelta * weight;
            convertedRotationLog += QuaternionLog(convertedRotation) * weight;

            HumanoidProjectedRootPose root = leaf.UnwrappedProjectedRootPose;
            projectedPosition += SelectProjectedRootPosition(root) * weight;
            if ((root.Channels & EHumanoidProjectedRootChannels.RotationYaw) != 0)
                projectedRotationLog += QuaternionLog(root.Rotation) * weight;
            projectedChannels |= root.Channels;
            HumanoidProjectedRootPose within = leaf.BodyAllocationProjectedRootPose;
            withinPosition += SelectProjectedRootPosition(within) * weight;
            if ((within.Channels & EHumanoidProjectedRootChannels.RotationYaw) != 0)
                withinRotationLog += QuaternionLog(within.Rotation) * weight;
            withinChannels |= within.Channels;
            if (leaf.ContributionType == EHumanoidMotionContributionType.Override)
            {
                mappedPosition += leaf.CurrentBody.Position * weight;
                AccumulateQuaternion(ref mappedRotationVector, leaf.CurrentBody.Rotation, weight);
            }
        }

        if (!IsFinite(position) || !IsFinite(rotationLog) || !IsFinite(projectedPosition)
            || !IsFinite(projectedRotationLog) || !IsFinite(withinPosition) || !IsFinite(withinRotationLog)
            || !IsFinite(convertedPosition) || !IsFinite(convertedRotationLog))
            return false;
        requested = Matrix4x4.CreateFromQuaternion(QuaternionExp(rotationLog)) * Matrix4x4.CreateTranslation(position);
        _bodyAllocationProjectedRootPose = new HumanoidProjectedRootPose(
            withinPosition, QuaternionExp(withinRotationLog), withinChannels);
        beforeProjection = requested * CreateProjectedRootMatrix(_bodyAllocationProjectedRootPose);
        if (!HumanoidBodyFrameMath.IsRigid(requested) || !HumanoidBodyFrameMath.IsRigid(beforeProjection)
            || !TryEvaluateNativeHumanoidPose(compiled, muscles, includeTranslationDof: true))
            return false;

        _currentImportedMappedBodySample = new HumanoidImportedBodySample
        {
            Position = mappedPosition,
            Rotation = NormalizeQuaternionVector(mappedRotationVector, Quaternion.Identity),
            Channels = overrideWeight > 0.0f ? EHumanoidImportedBodySampleChannels.All : EHumanoidImportedBodySampleChannels.None,
        };
        _currentConvertedBodyTranslationDelta = convertedPosition;
        _currentConvertedBodyRotationDelta = QuaternionExp(convertedRotationLog);
        _currentProjectedRootPose = new HumanoidProjectedRootPose(
            projectedPosition, QuaternionExp(projectedRotationLog), projectedChannels);
        _stagedRootMotionInputPose = _currentProjectedRootPose;
        _stagedRootMotionInputWeight = 1.0f;
        _preFeetProjectedRootPose = _currentProjectedRootPose;
        _preFeetBodyAllocationProjectedRootPose = _bodyAllocationProjectedRootPose;
        _pendingProjectedRootMotionOwner = owner;
        _hasPendingProjectedRootMotion = true;
        _activeImportedBodyProjectionPolicy = null;
        _activeImportedBodyProjectionOwner = owner;
        _pendingImportedBodyLoopPoseCorrection = null;
        _pendingStateMachineRootMotionFrame = null;
        _pendingStateMachineRootMotionOwner = null;
        return true;
    }

    private bool TryResolveStateMachineFeetProjection(
        CompiledHumanoidAvatarDefinition compiled, HumanoidStateMachineRootMotionFrame frame)
    {
        ReadOnlySpan<HumanoidStateMachineRootMotionLeafState?> leaves = frame.Leaves;
        for (int i = 0; i < leaves.Length; i++)
        {
            HumanoidStateMachineRootMotionLeafState? leaf = leaves[i];
            if (leaf is null || leaf.Weight <= 0.0f || leaf.Policy.BakePositionYIntoPose
                || leaf.Policy.PositionYBasis is not EImportedHumanoidRootPositionYBasis.Feet)
                continue;

            if (!leaf.TryGetCanonicalFeetY(out float canonicalFeetY))
            {
                if (!TryEvaluateNativeHumanoidPose(compiled, leaf.CanonicalProjectionMuscles, includeTranslationDof: false))
                    return false;
                SetLeafBodyEvaluationFields(leaf, useCanonicalSample: true);
                if (!TryCalculateProjectedFeetHeight(compiled, leaf.Policy, out canonicalFeetY))
                    return false;
                leaf.SetCanonicalFeetY(canonicalFeetY);
            }
            // The current projection includes the final staged TDoF, just like
            // direct playback. Canonical projection deliberately excludes it.
            if (!TryEvaluateNativeHumanoidPose(compiled, leaf.CurrentProjectionMuscles, includeTranslationDof: true))
                return false;
            SetLeafBodyEvaluationFields(leaf, useCanonicalSample: false);
            if (!TryCalculateProjectedFeetHeight(compiled, leaf.Policy, out float currentFeetY))
                return false;
            leaf.AddProjectedFeetDelta(currentFeetY - canonicalFeetY);
        }
        return true;
    }

    private static void CalculateLeafConvertedBodyDelta(
        CompiledHumanoidAvatarDefinition compiled,
        HumanoidStateMachineRootMotionLeafState leaf,
        out Vector3 translation,
        out Quaternion rotation)
    {
        Vector3 canonicalPosition = leaf.CanonicalBody.Position;
        Vector3 currentPosition = leaf.CurrentBody.Position;
        Quaternion canonicalRotation = NormalizeOrIdentity(leaf.CanonicalBody.Rotation);
        Quaternion currentRotation = NormalizeOrIdentity(leaf.CurrentBody.Rotation);
        if (leaf.Policy.Mirror)
        {
            canonicalPosition = ImportedHumanoidMirrorOperator.MirrorPosition(canonicalPosition);
            currentPosition = ImportedHumanoidMirrorOperator.MirrorPosition(currentPosition);
            canonicalRotation = ImportedHumanoidMirrorOperator.MirrorRotation(canonicalRotation);
            currentRotation = ImportedHumanoidMirrorOperator.MirrorRotation(currentRotation);
        }
        Vector3 delta = currentPosition - canonicalPosition;
        translation = new Vector3(delta.X, delta.Z, delta.Y) * (compiled.HumanScale * compiled.ModelUnitsPerMeter);
        rotation = NormalizeOrIdentity(Quaternion.Inverse(canonicalRotation) * currentRotation);
    }

    private void SetLeafBodyEvaluationFields(HumanoidStateMachineRootMotionLeafState leaf, bool useCanonicalSample)
    {
        _canonicalImportedBodySample = leaf.CanonicalBody;
        _currentImportedMappedBodySample = useCanonicalSample ? leaf.CanonicalBody : leaf.CurrentBody;
        _importedBodySampleWeight = 1.0f;
        _bodyAllocationProjectedRootPose = useCanonicalSample ? HumanoidProjectedRootPose.Identity : leaf.BodyAllocationProjectedRootPose;
        _activeImportedBodyProjectionPolicy = leaf.Policy;
    }

    private static void AccumulateQuaternion(
        ref Vector4 accumulator,
        Quaternion value,
        float weight)
    {
        if (!float.IsFinite(weight) || weight <= 0.0f)
            return;

        Quaternion normalized = CanonicalizeQuaternion(NormalizeOrIdentity(value));
        accumulator += new Vector4(
            normalized.X,
            normalized.Y,
            normalized.Z,
            normalized.W) * weight;
    }

    private static Quaternion NormalizeQuaternionVector(
        Vector4 value,
        Quaternion fallback)
    {
        Quaternion quaternion = new(value.X, value.Y, value.Z, value.W);
        return IsFiniteNonZero(quaternion)
            ? Quaternion.Normalize(quaternion)
            : NormalizeOrIdentity(fallback);
    }

    private static Quaternion CanonicalizeQuaternion(Quaternion value)
    {
        bool negate = value.W < 0.0f
            || (value.W == 0.0f && value.Z < 0.0f)
            || (value.W == 0.0f && value.Z == 0.0f && value.Y < 0.0f)
            || (value.W == 0.0f && value.Z == 0.0f && value.Y == 0.0f && value.X < 0.0f);
        return negate
            ? new Quaternion(-value.X, -value.Y, -value.Z, -value.W)
            : value;
    }

    private static Quaternion NormalizeOrIdentity(Quaternion value)
        => IsFiniteNonZero(value) ? Quaternion.Normalize(value) : Quaternion.Identity;

    private static Vector3 QuaternionLog(Quaternion value)
    {
        Quaternion normalized = CanonicalizeQuaternion(NormalizeOrIdentity(value));
        float vectorLength = MathF.Sqrt(
            normalized.X * normalized.X
            + normalized.Y * normalized.Y
            + normalized.Z * normalized.Z);
        if (vectorLength <= 1.0e-7f)
            return Vector3.Zero;

        float angle = MathF.Atan2(vectorLength, normalized.W);
        return new Vector3(normalized.X, normalized.Y, normalized.Z)
            * (angle / vectorLength);
    }

    private static Quaternion QuaternionExp(Vector3 value)
    {
        float angle = value.Length();
        if (!float.IsFinite(angle) || angle <= 1.0e-7f)
            return Quaternion.Identity;

        float scale = MathF.Sin(angle) / angle;
        return NormalizeOrIdentity(new Quaternion(
            value.X * scale,
            value.Y * scale,
            value.Z * scale,
            MathF.Cos(angle)));
    }
}

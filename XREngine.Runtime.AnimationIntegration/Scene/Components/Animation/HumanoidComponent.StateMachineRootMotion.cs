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
            ApplyMusclePose();
            _skipNextScenePoseAfterImmediateStateMachineEvaluation = true;
        }
    }

    private bool TryFinalizeStateMachineRootMotionFrame(
        CompiledHumanoidAvatarDefinition compiled,
        ReadOnlySpan<float> finalMusclePose)
    {
        HumanoidStateMachineRootMotionFrame? frame = _pendingStateMachineRootMotionFrame;
        object? owner = _pendingStateMachineRootMotionOwner;
        if (frame is null || owner is null)
            return false;

        _pendingStateMachineRootMotionFrame = null;
        _pendingStateMachineRootMotionOwner = null;
        ResolveStateMachineFeetProjection(compiled, frame, finalMusclePose);

        SceneNode? hipsNode = compiled.GetNode(EHumanoidAvatarBoneRole.Hips);
        if (hipsNode is null)
        {
            PublishComposedStateMachineRoot(owner, HumanoidProjectedRootPose.Identity);
            return true;
        }

        TransformBase hipsTransform = hipsNode.Transform;
        if (hipsTransform.IsLocalMatrixDirty)
            hipsTransform.RecalcLocal();
        Matrix4x4 baseLocal = hipsTransform.LocalMatrix;
        if (!Matrix4x4.Decompose(
            baseLocal,
            out Vector3 baseScale,
            out Quaternion baseRotation,
            out Vector3 baseTranslation))
        {
            PublishComposedStateMachineRoot(owner, HumanoidProjectedRootPose.Identity);
            return true;
        }
        baseRotation = NormalizeOrIdentity(baseRotation);

        ReadOnlySpan<HumanoidStateMachineRootMotionLeafState?> leaves = frame.Leaves;
        float overrideWeight = 0.0f;
        for (int i = 0; i < leaves.Length; i++)
        {
            HumanoidStateMachineRootMotionLeafState? leaf = leaves[i];
            if (leaf is not null
                && leaf.ContributionType == EUnityHumanoidMotionContributionType.Override
                && leaf.Weight > 0.0f)
                overrideWeight += leaf.Weight;
        }

        float baseWeight = Math.Max(0.0f, 1.0f - overrideWeight);
        Vector3 blendedTranslation = baseTranslation;
        Vector3 blendedRotationLog = Vector3.Zero;
        Vector3 convertedBodyTranslationDelta = Vector3.Zero;
        Vector3 convertedBodyRotationLog = Vector3.Zero;
        Vector3 projectedPosition = Vector3.Zero;
        Vector3 projectedRotationLog = Vector3.Zero;
        EHumanoidProjectedRootChannels projectedChannels = EHumanoidProjectedRootChannels.None;
        Vector3 mappedBodyPosition = Vector3.Zero;
        Vector4 mappedBodyRotationVector = Vector4.Zero;
        AccumulateQuaternion(ref mappedBodyRotationVector, Quaternion.Identity, baseWeight);

        for (int i = 0; i < leaves.Length; i++)
        {
            HumanoidStateMachineRootMotionLeafState? leaf = leaves[i];
            if (leaf is null
                || leaf.ContributionType != EUnityHumanoidMotionContributionType.Override
                || leaf.Weight <= 0.0f)
                continue;

            // Do not renormalize here. Direct blend trees intentionally permit raw
            // child weights when NormalizeBlendValues is disabled; normalized trees
            // and transitions already publish weights whose sum is one.
            float weight = leaf.Weight;
            if (!TryCalculateLeafHipsLocalPose(
                compiled,
                hipsNode,
                leaf,
                out Vector3 leafTranslation,
                out Quaternion leafRotation))
                continue;

            blendedTranslation += (leafTranslation - baseTranslation) * weight;
            Quaternion localDelta = NormalizeOrIdentity(
                Quaternion.Inverse(baseRotation) * leafRotation);
            blendedRotationLog += QuaternionLog(localDelta) * weight;
            CalculateLeafConvertedBodyDelta(
                compiled,
                leaf,
                out Vector3 leafConvertedTranslation,
                out Quaternion leafConvertedRotation);
            convertedBodyTranslationDelta += leafConvertedTranslation * weight;
            convertedBodyRotationLog += QuaternionLog(leafConvertedRotation) * weight;

            HumanoidProjectedRootPose projectedRoot = leaf.UnwrappedProjectedRootPose;
            projectedPosition += SelectProjectedRootPosition(projectedRoot) * weight;
            if ((projectedRoot.Channels & EHumanoidProjectedRootChannels.RotationYaw) != 0)
                projectedRotationLog += QuaternionLog(projectedRoot.Rotation) * weight;
            projectedChannels |= projectedRoot.Channels;
            mappedBodyPosition += leaf.CurrentBody.Position * weight;
            AccumulateQuaternion(ref mappedBodyRotationVector, leaf.CurrentBody.Rotation, weight);
        }

        // Tangent-space composition is order-independent and reduces exactly to
        // Slerp(base, leaf, weight) for a single contributor, matching the direct
        // evaluator's partial-weight Body/root rotation semantics.
        Quaternion blendedRotation = NormalizeOrIdentity(
            baseRotation * QuaternionExp(blendedRotationLog));
        Quaternion projectedRotation = QuaternionExp(projectedRotationLog);
        Quaternion mappedBodyRotation = NormalizeQuaternionVector(
            mappedBodyRotationVector,
            Quaternion.Identity);
        Vector3 additiveTranslation = Vector3.Zero;
        Vector3 additiveRotationLog = Vector3.Zero;
        Vector3 additiveConvertedBodyTranslation = Vector3.Zero;
        Vector3 additiveConvertedBodyRotationLog = Vector3.Zero;
        Vector3 additiveProjectedPosition = Vector3.Zero;
        Vector3 additiveProjectedRotationLog = Vector3.Zero;

        for (int i = 0; i < leaves.Length; i++)
        {
            HumanoidStateMachineRootMotionLeafState? leaf = leaves[i];
            if (leaf is null
                || leaf.ContributionType != EUnityHumanoidMotionContributionType.Additive
                || leaf.Weight <= 0.0f)
                continue;

            float weight = leaf.Weight;
            if (TryCalculateLeafHipsLocalPose(
                compiled,
                hipsNode,
                leaf,
                out Vector3 leafTranslation,
                out Quaternion leafRotation))
            {
                additiveTranslation += (leafTranslation - baseTranslation) * weight;
                Quaternion localDelta = NormalizeOrIdentity(
                    Quaternion.Inverse(baseRotation) * leafRotation);
                additiveRotationLog += QuaternionLog(localDelta) * weight;
            }

            CalculateLeafConvertedBodyDelta(
                compiled,
                leaf,
                out Vector3 leafConvertedTranslation,
                out Quaternion leafConvertedRotation);
            additiveConvertedBodyTranslation += leafConvertedTranslation * weight;
            additiveConvertedBodyRotationLog += QuaternionLog(leafConvertedRotation) * weight;

            HumanoidProjectedRootPose additiveRoot = leaf.UnwrappedProjectedRootPose;
            additiveProjectedPosition += SelectProjectedRootPosition(additiveRoot) * weight;
            if ((additiveRoot.Channels & EHumanoidProjectedRootChannels.RotationYaw) != 0)
                additiveProjectedRotationLog += QuaternionLog(additiveRoot.Rotation) * weight;
            projectedChannels |= additiveRoot.Channels;
        }

        blendedTranslation += additiveTranslation;
        blendedRotation = NormalizeOrIdentity(
            blendedRotation * QuaternionExp(additiveRotationLog));
        convertedBodyTranslationDelta += additiveConvertedBodyTranslation;
        convertedBodyRotationLog += additiveConvertedBodyRotationLog;
        projectedPosition += additiveProjectedPosition;
        projectedRotation = NormalizeOrIdentity(
            projectedRotation * QuaternionExp(additiveProjectedRotationLog));

        if (hipsNode.GetTransformAs<Transform>(true) is Transform transform)
            transform.SetLocalTranslationRotation(blendedTranslation, blendedRotation);
        else
            hipsTransform.DeriveLocalMatrix(
                Matrix4x4.CreateScale(baseScale)
                * Matrix4x4.CreateFromQuaternion(blendedRotation)
                * Matrix4x4.CreateTranslation(blendedTranslation));

        _currentImportedMappedBodySample = new HumanoidImportedBodySample
        {
            Position = mappedBodyPosition,
            Rotation = mappedBodyRotation,
            Channels = leaves.Length > 0
                ? EHumanoidImportedBodySampleChannels.All
                : EHumanoidImportedBodySampleChannels.None,
        };
        _currentConvertedBodyTranslationDelta = convertedBodyTranslationDelta;
        _currentConvertedBodyRotationDelta = QuaternionExp(convertedBodyRotationLog);
        PublishComposedStateMachineRoot(
            owner,
            new HumanoidProjectedRootPose(
                projectedPosition,
                projectedRotation,
                projectedChannels));
        return true;
    }

    private void ResolveStateMachineFeetProjection(
        CompiledHumanoidAvatarDefinition compiled,
        HumanoidStateMachineRootMotionFrame frame,
        ReadOnlySpan<float> finalMusclePose)
    {
        bool changedPose = false;
        ReadOnlySpan<HumanoidStateMachineRootMotionLeafState?> leaves = frame.Leaves;
        for (int i = 0; i < leaves.Length; i++)
        {
            HumanoidStateMachineRootMotionLeafState? leaf = leaves[i];
            if (leaf is null
                || leaf.Weight <= 0.0f
                || leaf.Policy.BakePositionYIntoPose
                || leaf.Policy.PositionYBasis is not EUnityHumanoidRootPositionYBasis.Feet)
                continue;

            _activeImportedBodyProjectionPolicy = leaf.Policy;
            _hasCanonicalProjectionMuscleValueSnapshot =
                leaf.CanonicalProjectionMuscles.Length >= MuscleValueCount;
            if (_hasCanonicalProjectionMuscleValueSnapshot)
                leaf.CanonicalProjectionMuscles[..MuscleValueCount]
                    .CopyTo(_canonicalProjectionMuscleValueSnapshot);
            if (TryEvaluateCalibratedProjectedRootYDelta(
                compiled,
                leaf.CurrentProjectionMuscles,
                out float calibratedY))
            {
                leaf.AddProjectedFeetDelta(calibratedY);
                continue;
            }

            if (!leaf.TryGetCanonicalFeetY(out float canonicalFeetY))
            {
                ApplyMuscleSnapshot(compiled, leaf.CanonicalProjectionMuscles);
                SetLeafBodyEvaluationFields(leaf, useCanonicalSample: true);
                if (!TryCalculateProjectedFeetHeight(compiled, leaf.Policy, out canonicalFeetY))
                    continue;
                leaf.SetCanonicalFeetY(canonicalFeetY);
                changedPose = true;
            }

            ApplyMuscleSnapshot(compiled, leaf.CurrentProjectionMuscles);
            SetLeafBodyEvaluationFields(leaf, useCanonicalSample: false);
            if (TryCalculateProjectedFeetHeight(compiled, leaf.Policy, out float currentFeetY))
                leaf.AddProjectedFeetDelta(currentFeetY - canonicalFeetY);
            changedPose = true;
        }

        if (changedPose)
            ApplyMuscleSnapshot(compiled, finalMusclePose);
    }

    private bool TryCalculateLeafHipsLocalPose(
        CompiledHumanoidAvatarDefinition compiled,
        SceneNode hipsNode,
        HumanoidStateMachineRootMotionLeafState leaf,
        out Vector3 translation,
        out Quaternion rotation)
    {
        SetLeafBodyEvaluationFields(leaf, useCanonicalSample: false);
        Matrix4x4 localPose = CalculateImportedBodyLocalPose(
            compiled,
            hipsNode,
            leaf.Policy,
            leaf.CurrentLoopPoseCorrection);
        if (!Matrix4x4.Decompose(localPose, out _, out rotation, out translation)
            || !IsFinite(translation)
            || !IsFiniteNonZero(rotation))
        {
            translation = Vector3.Zero;
            rotation = Quaternion.Identity;
            return false;
        }

        rotation = Quaternion.Normalize(rotation);
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
            canonicalPosition = UnityHumanoidMirrorOperator.MirrorPosition(canonicalPosition);
            currentPosition = UnityHumanoidMirrorOperator.MirrorPosition(currentPosition);
            canonicalRotation = UnityHumanoidMirrorOperator.MirrorRotation(canonicalRotation);
            currentRotation = UnityHumanoidMirrorOperator.MirrorRotation(currentRotation);
        }

        Vector3 mappedPositionDelta = currentPosition - canonicalPosition;
        translation = new Vector3(
            mappedPositionDelta.X,
            mappedPositionDelta.Z,
            mappedPositionDelta.Y)
            * (compiled.HumanScale * compiled.ModelUnitsPerMeter);
        rotation = NormalizeOrIdentity(
            Quaternion.Inverse(canonicalRotation) * currentRotation);
    }

    private void SetLeafBodyEvaluationFields(
        HumanoidStateMachineRootMotionLeafState leaf,
        bool useCanonicalSample)
    {
        _canonicalImportedBodySample = leaf.CanonicalBody;
        _currentImportedMappedBodySample = useCanonicalSample
            ? leaf.CanonicalBody
            : leaf.CurrentBody;
        _importedBodySampleWeight = 1.0f;
        _bodyAllocationProjectedRootPose = useCanonicalSample
            ? new HumanoidProjectedRootPose(
                Vector3.Zero,
                Quaternion.Identity,
                leaf.BodyAllocationProjectedRootPose.Channels)
            : leaf.BodyAllocationProjectedRootPose;
        _activeImportedBodyProjectionPolicy = leaf.Policy;
    }

    private void PublishComposedStateMachineRoot(
        object owner,
        HumanoidProjectedRootPose pose)
    {
        _currentProjectedRootPose = pose;
        _preFeetProjectedRootPose = pose;
        _bodyAllocationProjectedRootPose = HumanoidProjectedRootPose.Identity;
        _pendingProjectedRootMotionOwner = owner;
        _hasPendingProjectedRootMotion = true;
        _activeImportedBodyProjectionPolicy = null;
        _activeImportedBodyProjectionOwner = owner;
        _pendingImportedBodyLoopPoseCorrection = null;
        CommitPendingProjectedRootMotion(owner);
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

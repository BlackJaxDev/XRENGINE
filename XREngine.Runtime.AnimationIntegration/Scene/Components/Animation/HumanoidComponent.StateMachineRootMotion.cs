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

    private bool TryFinalizeStateMachineRootMotionFrame(
        CompiledHumanoidAvatarDefinition compiled)
    {
        HumanoidStateMachineRootMotionFrame? frame = _pendingStateMachineRootMotionFrame;
        object? owner = _pendingStateMachineRootMotionOwner;
        if (frame is null || owner is null)
            return false;

        _pendingStateMachineRootMotionFrame = null;
        _pendingStateMachineRootMotionOwner = null;
        SceneNode? hipsNode = compiled.GetNode(EHumanoidAvatarBoneRole.Hips);
        if (hipsNode is null)
        {
            PublishComposedStateMachineRoot(owner, HumanoidProjectedRootPose.Identity);
            return true;
        }

        // Equivalent full-weight override leaves share one Body/root allocation.
        // Use the already-finalized native workspace rather than evaluating each
        // leaf's projection-only sidecar. This keeps an identical transition or
        // blend independent of leaf count while retaining one final TDoF solve.
        if (TryGetEquivalentFullWeightOverride(frame, out HumanoidStateMachineRootMotionLeafState? directLeaf)
            && directLeaf is not null)
        {
            // A full-weight equivalent state must follow the direct clip path exactly.
            // In particular, do not first run leaf feet projection: that sidecar is
            // only for multi-leaf composition and would add a root-Y offset absent
            // from the corresponding direct exact seek.
            return ApplyFullWeightSingleOverride(compiled, hipsNode, owner, directLeaf);
        }

        ResolveStateMachineFeetProjection(compiled, frame);

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
                && leaf.ContributionType == EHumanoidMotionContributionType.Override
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
                || leaf.ContributionType != EHumanoidMotionContributionType.Override
                || leaf.Weight <= 0.0f)
                continue;

            // Do not renormalize here. Direct blend trees intentionally permit raw
            // child weights when NormalizeBlendValues is disabled; normalized trees
            // and transitions already publish weights whose sum is one.
            float weight = leaf.Weight;
            if (!TryCalculateLeafHipsLocalPose(
                compiled,
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
                || leaf.ContributionType != EHumanoidMotionContributionType.Additive
                || leaf.Weight <= 0.0f)
                continue;

            float weight = leaf.Weight;
            if (TryCalculateLeafHipsLocalPose(
                compiled,
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

    private static bool TryGetEquivalentFullWeightOverride(
        HumanoidStateMachineRootMotionFrame frame,
        out HumanoidStateMachineRootMotionLeafState? result)
    {
        result = null;
        float totalWeight = 0.0f;
        ReadOnlySpan<HumanoidStateMachineRootMotionLeafState?> leaves = frame.Leaves;
        for (int i = 0; i < leaves.Length; i++)
        {
            HumanoidStateMachineRootMotionLeafState? leaf = leaves[i];
            if (leaf is null || leaf.Weight <= 0.0f)
                continue;

            if (!float.IsFinite(leaf.Weight)
                || leaf.ContributionType != EHumanoidMotionContributionType.Override)
                return false;

            if (result is not null
                && (result.Policy != leaf.Policy
                    || !result.CanonicalBody.Equals(leaf.CanonicalBody)
                    || !result.CurrentBody.Equals(leaf.CurrentBody)
                    || result.SourceLoopCycle != leaf.SourceLoopCycle
                    || result.CurrentLoopPoseCorrection != leaf.CurrentLoopPoseCorrection
                    || result.BodyAllocationProjectedRootPose != leaf.BodyAllocationProjectedRootPose
                    || result.UnwrappedProjectedRootPose != leaf.UnwrappedProjectedRootPose
                    || !result.CanonicalProjectionMuscles.SequenceEqual(leaf.CanonicalProjectionMuscles)
                    || !result.CurrentProjectionMuscles.SequenceEqual(leaf.CurrentProjectionMuscles)))
                return false;

            result = leaf;
            totalWeight += leaf.Weight;
        }

        return result is not null && MathF.Abs(totalWeight - 1.0f) <= 0.000001f;
    }

    private bool ApplyFullWeightSingleOverride(
        CompiledHumanoidAvatarDefinition compiled,
        SceneNode hipsNode,
        object owner,
        HumanoidStateMachineRootMotionLeafState leaf)
    {
        SetLeafBodyEvaluationFields(leaf, useCanonicalSample: false);
        Matrix4x4 localPose = ApplyImportedBodyLoopPoseCorrection(
            CalculateImportedBodyAllocatedLocalPose(
                compiled,
                _nativePoseWorkspace.GetLocalMatrix(EHumanoidAvatarBoneRole.Hips),
                leaf.Policy),
            leaf.CurrentLoopPoseCorrection);
        if (!Matrix4x4.Decompose(localPose, out _, out Quaternion rotation, out Vector3 translation)
            || !IsFinite(translation)
            || !IsFiniteNonZero(rotation))
        {
            LogRejectedImportedHumanoidFrame("state-machine Body/root allocation was non-finite or unsolvable");
            return false;
        }

        TransformBase hipsTransform = hipsNode.Transform;
        rotation = Quaternion.Normalize(rotation);
        if (hipsNode.GetTransformAs<Transform>(true) is Transform transform)
            transform.SetLocalTranslationRotation(translation, rotation);
        else
        {
            if (hipsTransform.IsLocalMatrixDirty)
                hipsTransform.RecalcLocal();
            if (!Matrix4x4.Decompose(hipsTransform.LocalMatrix, out Vector3 scale, out _, out _))
                return false;
            hipsTransform.DeriveLocalMatrix(
                Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(translation));
        }

        _currentImportedMappedBodySample = leaf.CurrentBody;
        CalculateLeafConvertedBodyDelta(compiled, leaf, out _currentConvertedBodyTranslationDelta, out _currentConvertedBodyRotationDelta);
        HumanoidProjectedRootPose projectedRoot = leaf.UnwrappedProjectedRootPose;
        if (leaf.SourceLoopCycle == 0L)
        {
            // The first exact state seek may expose an initialized-but-stale
            // unwrapped cache. Reconstruct the within-cycle projection from the same
            // sampled Body pair and compiled policy as direct playback. A nonzero
            // source cycle retains the leaf's temporal unwrapped accumulation.
            projectedRoot = CalculateProjectedRootPose(
                leaf.CurrentBody.Position,
                leaf.CanonicalBody.Position,
                leaf.CurrentBody.Rotation,
                leaf.CanonicalBody.Rotation,
                compiled.HumanScale * compiled.ModelUnitsPerMeter,
                1.0f,
                leaf.Policy);
        }
        if (!leaf.Policy.BakePositionYIntoPose
            && leaf.Policy.PositionYBasis is EImportedHumanoidRootPositionYBasis.Feet)
        {
            // Direct playback exposes the feet-projection channel even when its
            // canonical-relative height is zero. The single-state path bypasses
            // multi-leaf feet sampling, but preserves that channel contract.
            projectedRoot = new HumanoidProjectedRootPose(
                projectedRoot.Position,
                projectedRoot.Rotation,
                projectedRoot.Channels | EHumanoidProjectedRootChannels.PositionY);
        }
        PublishComposedStateMachineRoot(owner, projectedRoot);
        return true;
    }

    private void ResolveStateMachineFeetProjection(
        CompiledHumanoidAvatarDefinition compiled,
        HumanoidStateMachineRootMotionFrame frame)
    {
        ReadOnlySpan<HumanoidStateMachineRootMotionLeafState?> leaves = frame.Leaves;
        for (int i = 0; i < leaves.Length; i++)
        {
            HumanoidStateMachineRootMotionLeafState? leaf = leaves[i];
            if (leaf is null
                || leaf.Weight <= 0.0f
                || leaf.Policy.BakePositionYIntoPose
                || leaf.Policy.PositionYBasis is not EImportedHumanoidRootPositionYBasis.Feet)
                continue;

            if (!leaf.TryGetCanonicalFeetY(out float canonicalFeetY))
            {
                if (!TryEvaluateNativeHumanoidPose(
                        compiled,
                        leaf.CanonicalProjectionMuscles,
                        includeTranslationDof: false,
                        commit: false))
                    continue;
                SetLeafBodyEvaluationFields(leaf, useCanonicalSample: true);
                if (!TryCalculateProjectedFeetHeight(compiled, leaf.Policy, out canonicalFeetY))
                    continue;
                leaf.SetCanonicalFeetY(canonicalFeetY);
            }

            if (!TryEvaluateNativeHumanoidPose(
                    compiled,
                    leaf.CurrentProjectionMuscles,
                    // Translation DoF is staged only after leaf blending. Applying the
                    // final blended value to every leaf would make root composition
                    // depend on leaf count and order; the final authored pose still
                    // consumes the blended DoF through the shared native solver.
                    includeTranslationDof: false,
                    commit: false))
                continue;
            SetLeafBodyEvaluationFields(leaf, useCanonicalSample: false);
            if (TryCalculateProjectedFeetHeight(compiled, leaf.Policy, out float currentFeetY))
                leaf.AddProjectedFeetDelta(currentFeetY - canonicalFeetY);
        }
    }

    private bool TryCalculateLeafHipsLocalPose(
        CompiledHumanoidAvatarDefinition compiled,
        HumanoidStateMachineRootMotionLeafState leaf,
        out Vector3 translation,
        out Quaternion rotation)
    {
        if (!TryEvaluateNativeHumanoidPose(
                compiled,
                leaf.CurrentProjectionMuscles,
                includeTranslationDof: false,
                commit: false))
        {
            translation = Vector3.Zero;
            rotation = Quaternion.Identity;
            return false;
        }

        SetLeafBodyEvaluationFields(leaf, useCanonicalSample: false);
        Matrix4x4 localPose = ApplyImportedBodyLoopPoseCorrection(
            CalculateImportedBodyAllocatedLocalPose(
                compiled,
                _nativePoseWorkspace.GetLocalMatrix(EHumanoidAvatarBoneRole.Hips),
                leaf.Policy),
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
            canonicalPosition = ImportedHumanoidMirrorOperator.MirrorPosition(canonicalPosition);
            currentPosition = ImportedHumanoidMirrorOperator.MirrorPosition(currentPosition);
            canonicalRotation = ImportedHumanoidMirrorOperator.MirrorRotation(canonicalRotation);
            currentRotation = ImportedHumanoidMirrorOperator.MirrorRotation(currentRotation);
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

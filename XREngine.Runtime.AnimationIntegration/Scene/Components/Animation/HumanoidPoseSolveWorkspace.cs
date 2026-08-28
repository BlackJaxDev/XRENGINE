using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Per-humanoid reusable scratch state for the native avatar solver. The
/// workspace is bound when an avatar definition is compiled, so pose evaluation
/// performs no allocation, hierarchy search, or matrix decomposition.
/// </summary>
internal sealed class HumanoidPoseSolveWorkspace
{
    private readonly Vector3[] _localScales = new Vector3[CompiledHumanoidAvatarDefinition.RoleCount];
    private readonly Quaternion[] _localRotations = new Quaternion[CompiledHumanoidAvatarDefinition.RoleCount];
    private readonly Vector3[] _localTranslations = new Vector3[CompiledHumanoidAvatarDefinition.RoleCount];
    private readonly Matrix4x4[] _localMatrices = new Matrix4x4[CompiledHumanoidAvatarDefinition.RoleCount];
    private readonly Matrix4x4[] _modelRootMatrices = new Matrix4x4[CompiledHumanoidAvatarDefinition.RoleCount];
    private readonly Vector3[] _semanticDegrees = new Vector3[CompiledHumanoidAvatarDefinition.RoleCount];
    private readonly Vector3[] _translationDof = new Vector3[CompiledHumanoidAvatarDefinition.RoleCount];
    private readonly Quaternion[] _previousCommittedRotations = new Quaternion[CompiledHumanoidAvatarDefinition.RoleCount];
    private readonly bool[] _hasPreviousCommittedRotation = new bool[CompiledHumanoidAvatarDefinition.RoleCount];

    private CompiledHumanoidAvatarDefinition? _definition;
    private float[] _auxiliaryTwistDegrees = [];
    private Quaternion[] _auxiliaryRotations = [];
    private Quaternion[] _previousCommittedAuxiliaryRotations = [];
    private bool[] _hasPreviousCommittedAuxiliaryRotation = [];
    private Matrix4x4[] _auxiliaryLocalMatrices = [];

    public void BindDefinition(CompiledHumanoidAvatarDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
        Array.Clear(_hasPreviousCommittedRotation);
        int auxiliaryCount = definition.AuxiliaryBones.Length;
        _auxiliaryTwistDegrees = new float[auxiliaryCount];
        _auxiliaryRotations = new Quaternion[auxiliaryCount];
        _previousCommittedAuxiliaryRotations = new Quaternion[auxiliaryCount];
        _hasPreviousCommittedAuxiliaryRotation = new bool[auxiliaryCount];
        _auxiliaryLocalMatrices = new Matrix4x4[auxiliaryCount];
    }

    public void UnbindDefinition()
    {
        _definition = null;
        Array.Clear(_hasPreviousCommittedRotation);
        _auxiliaryTwistDegrees = [];
        _auxiliaryRotations = [];
        _previousCommittedAuxiliaryRotations = [];
        _hasPreviousCommittedAuxiliaryRotation = [];
        _auxiliaryLocalMatrices = [];
    }

    public void Begin(CompiledHumanoidAvatarDefinition definition)
    {
        if (!ReferenceEquals(_definition, definition))
            throw new InvalidOperationException("Humanoid pose workspace is not bound to the compiled avatar definition.");

        Array.Clear(_semanticDegrees);
        Array.Clear(_translationDof);
        Array.Clear(_auxiliaryTwistDegrees);
        for (int i = 0; i < definition.BoneSolvePlans.Length; i++)
        {
            ref readonly CompiledHumanoidBoneSolvePlan plan = ref definition.BoneSolvePlans[i];
            _localScales[i] = plan.NeutralScale;
            _localRotations[i] = plan.NeutralRotation;
            _localTranslations[i] = plan.NeutralTranslation;
            _localMatrices[i] = Matrix4x4.Identity;
            _modelRootMatrices[i] = Matrix4x4.Identity;
        }
    }

    /// <summary>
    /// Stores semantic twist/front-back/left-right angles for one mapped role.
    /// </summary>
    public void SetMuscleDegrees(
        EHumanoidAvatarBoneRole role,
        float twistDegrees,
        float frontBackDegrees,
        float leftRightDegrees)
    {
        int index = (int)role;
        if ((uint)index >= (uint)_semanticDegrees.Length)
            return;

        _semanticDegrees[index] = new Vector3(twistDegrees, frontBackDegrees, leftRightDegrees);
    }

    public void SetTranslationDof(EHumanoidAvatarBoneRole role, Vector3 translation)
    {
        int index = (int)role;
        if ((uint)index < (uint)_translationDof.Length)
            _translationDof[index] = translation;
    }

    public bool TrySolve(CompiledHumanoidAvatarDefinition definition)
    {
        DistributeTwist(definition);

        // Auxiliary locals are evaluated first because they are dynamic bridge
        // segments for semantic descendants during the scratch FK below.
        for (int i = 0; i < definition.AuxiliaryBones.Length; i++)
        {
            CompiledHumanoidAvatarAuxiliaryBone auxiliary = definition.AuxiliaryBones[i];
            float radians = _auxiliaryTwistDegrees[i] * (MathF.PI / 180.0f);
            Quaternion delta = Quaternion.CreateFromAxisAngle(auxiliary.LocalAxis, radians);
            Quaternion rotation = Quaternion.Normalize(auxiliary.NeutralRotation * delta);
            if (Quaternion.Dot(rotation, auxiliary.NeutralRotation) < 0.0f)
                rotation = Negate(rotation);
            Matrix4x4 local = Matrix4x4.CreateScale(auxiliary.NeutralScale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(auxiliary.NeutralTranslation);
            if (!IsFinite(rotation) || !IsFinite(local))
                return false;

            _auxiliaryRotations[i] = rotation;
            _auxiliaryLocalMatrices[i] = local;
        }

        for (int i = 0; i < definition.BoneSolvePlans.Length; i++)
        {
            ref readonly CompiledHumanoidBoneSolvePlan plan = ref definition.BoneSolvePlans[i];
            Vector3 degrees = _semanticDegrees[i];
            Quaternion rotation = CompiledHumanoidPoseSolver.EvaluateLocalRotation(
                plan,
                degrees.X,
                degrees.Y,
                degrees.Z);
            Vector3 translation = definition.SolverSettings.HasTranslationDoF
                ? CompiledHumanoidPoseSolver.EvaluateLocalTranslation(
                    plan,
                    _translationDof[i],
                    definition.HumanScale,
                    definition.ModelUnitsPerMeter)
                : plan.NeutralTranslation;
            Matrix4x4 local = Matrix4x4.CreateScale(plan.NeutralScale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(translation);
            if (!IsFinite(rotation) || !IsFinite(translation) || !IsFinite(local))
                return false;

            _localRotations[i] = rotation;
            _localTranslations[i] = translation;
            _localMatrices[i] = local;
        }

        ReadOnlySpan<int> order = definition.BoneSolvePlanOrder;
        for (int orderIndex = 0; orderIndex < order.Length; orderIndex++)
        {
            int roleIndex = order[orderIndex];
            ref readonly CompiledHumanoidBoneSolvePlan plan = ref definition.BoneSolvePlans[roleIndex];
            Matrix4x4 bridge = Matrix4x4.Identity;
            ReadOnlySpan<CompiledHumanoidParentBridgeSegment> segments = plan.ParentBridgeSegments;
            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                ref readonly CompiledHumanoidParentBridgeSegment segment = ref segments[segmentIndex];
                bridge *= segment.AuxiliaryBoneIndex >= 0
                    ? _auxiliaryLocalMatrices[segment.AuxiliaryBoneIndex]
                    : segment.NeutralLocalTransform;
            }
            Matrix4x4 relative = _localMatrices[roleIndex] * bridge;
            Matrix4x4 modelRoot = plan.MappedAncestorPlanIndex >= 0
                ? relative * _modelRootMatrices[plan.MappedAncestorPlanIndex]
                : relative;
            if (!IsFinite(modelRoot))
                return false;

            _modelRootMatrices[roleIndex] = modelRoot;
        }

        return true;
    }

    public void Commit(CompiledHumanoidAvatarDefinition definition)
    {
        ReadOnlySpan<int> order = definition.ConcreteCommitOrder;
        for (int orderIndex = 0; orderIndex < order.Length; orderIndex++)
        {
            CompiledHumanoidConcreteCommitTarget target = definition.ConcreteCommitTargets[order[orderIndex]];
            if (target.IsAuxiliary)
            {
                CommitAuxiliary(definition.AuxiliaryBones[target.Index], target.Index);
                continue;
            }

            int roleIndex = target.Index;
            ref readonly CompiledHumanoidBoneSolvePlan plan = ref definition.BoneSolvePlans[roleIndex];
            if (plan.Node is null)
                continue;

            Quaternion rotation = _localRotations[roleIndex];
            if (_hasPreviousCommittedRotation[roleIndex]
                && Quaternion.Dot(rotation, _previousCommittedRotations[roleIndex]) < 0.0f)
                rotation = Negate(rotation);
            _previousCommittedRotations[roleIndex] = rotation;
            _hasPreviousCommittedRotation[roleIndex] = true;
            _localRotations[roleIndex] = rotation;

            if (plan.Node.GetTransformAs<Transform>(true) is Transform transform)
            {
                transform.Scale = _localScales[roleIndex];
                transform.SetLocalTranslationRotation(_localTranslations[roleIndex], rotation);
            }
            else
            {
                plan.Node.Transform.DeriveLocalMatrix(
                    Matrix4x4.CreateScale(_localScales[roleIndex])
                    * Matrix4x4.CreateFromQuaternion(rotation)
                    * Matrix4x4.CreateTranslation(_localTranslations[roleIndex]));
            }
        }

    }

    private void CommitAuxiliary(CompiledHumanoidAvatarAuxiliaryBone auxiliary, int index)
    {
        Quaternion rotation = _auxiliaryRotations[index];
        if (_hasPreviousCommittedAuxiliaryRotation[index]
            && Quaternion.Dot(rotation, _previousCommittedAuxiliaryRotations[index]) < 0.0f)
            rotation = Negate(rotation);
        _previousCommittedAuxiliaryRotations[index] = rotation;
        _hasPreviousCommittedAuxiliaryRotation[index] = true;
        _auxiliaryRotations[index] = rotation;
        if (auxiliary.Node.GetTransformAs<Transform>(true) is Transform transform)
        {
            transform.Scale = auxiliary.NeutralScale;
            transform.SetLocalTranslationRotation(auxiliary.NeutralTranslation, rotation);
            return;
        }

        auxiliary.Node.Transform.DeriveLocalMatrix(
            Matrix4x4.CreateScale(auxiliary.NeutralScale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(auxiliary.NeutralTranslation));
    }

    public Matrix4x4 GetLocalMatrix(EHumanoidAvatarBoneRole role)
    {
        int index = (int)role;
        return (uint)index < (uint)_localMatrices.Length
            ? _localMatrices[index]
            : Matrix4x4.Identity;
    }

    public Matrix4x4 GetModelRootMatrix(EHumanoidAvatarBoneRole role)
    {
        int index = (int)role;
        return (uint)index < (uint)_modelRootMatrices.Length
            ? _modelRootMatrices[index]
            : Matrix4x4.Identity;
    }

    private void DistributeTwist(CompiledHumanoidAvatarDefinition definition)
    {
        ReadOnlySpan<CompiledHumanoidAvatarTwistChain> chains = definition.TwistChains;
        for (int i = 0; i < chains.Length; i++)
        {
            CompiledHumanoidAvatarTwistChain chain = chains[i];
            int proximalIndex = (int)chain.ProximalRole;
            int distalIndex = (int)chain.DistalRole;
            int endIndex = (int)chain.EndRole;
            float proximalTwist = _semanticDegrees[proximalIndex].X;
            float distalTwist = _semanticDegrees[distalIndex].X;
            float proximalShare = Math.Clamp(chain.ProximalDistribution, 0.0f, 1.0f);
            float distalShare = Math.Clamp(chain.DistalDistribution, 0.0f, 1.0f);

            _semanticDegrees[proximalIndex].X = proximalTwist * proximalShare;
            float proximalRemainder = DistributeAuxiliaryTwist(
                chain,
                chain.ProximalRole,
                proximalTwist * (1.0f - proximalShare));
            _semanticDegrees[distalIndex].X = distalTwist * distalShare + proximalRemainder;
            float distalRemainder = DistributeAuxiliaryTwist(
                chain,
                chain.DistalRole,
                distalTwist * (1.0f - distalShare));
            _semanticDegrees[endIndex].X += distalRemainder;
        }
    }

    private float DistributeAuxiliaryTwist(
        CompiledHumanoidAvatarTwistChain chain,
        EHumanoidAvatarBoneRole parentRole,
        float twistDegrees)
    {
        float totalWeight = 0.0f;
        ReadOnlySpan<CompiledHumanoidAvatarAuxiliaryBone> auxiliaryBones = chain.AuxiliaryBones;
        for (int i = 0; i < auxiliaryBones.Length; i++)
            if (auxiliaryBones[i].ParentRole == parentRole)
                totalWeight += Math.Clamp(auxiliaryBones[i].DistributionWeight, 0.0f, 1.0f);

        float normalization = totalWeight > 1.0f ? 1.0f / totalWeight : 1.0f;
        for (int i = 0; i < auxiliaryBones.Length; i++)
        {
            CompiledHumanoidAvatarAuxiliaryBone auxiliary = auxiliaryBones[i];
            if (auxiliary.ParentRole != parentRole)
                continue;

            float weight = Math.Clamp(auxiliary.DistributionWeight, 0.0f, 1.0f) * normalization;
            _auxiliaryTwistDegrees[auxiliary.Index] += twistDegrees * weight;
        }

        return twistDegrees * MathF.Max(0.0f, 1.0f - MathF.Min(totalWeight, 1.0f));
    }

    private static Quaternion Negate(Quaternion value)
        => new(-value.X, -value.Y, -value.Z, -value.W);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
            && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
            && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
            && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

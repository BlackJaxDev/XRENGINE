using System.Numerics;
using System.Text;
using XREngine.Animation.Importers;
using XREngine.Scene;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private static void DecomposeNeutralTransform(
        Matrix4x4 transform,
        out Vector3 scale,
        out Quaternion rotation,
        out Vector3 position)
    {
        if (Matrix4x4.Decompose(transform, out scale, out rotation, out position)
            && IsFiniteNonZero(rotation))
        {
            rotation = Quaternion.Normalize(rotation);
            return;
        }

        scale = Vector3.One;
        rotation = Quaternion.Identity;
        position = transform.Translation;
    }

    private static EHumanoidAvatarBoneRole? GetParentRole(EHumanoidAvatarBoneRole role)
        => role switch
        {
            EHumanoidAvatarBoneRole.Hips => null,
            EHumanoidAvatarBoneRole.Spine => EHumanoidAvatarBoneRole.Hips,
            EHumanoidAvatarBoneRole.Chest => EHumanoidAvatarBoneRole.Spine,
            EHumanoidAvatarBoneRole.UpperChest => EHumanoidAvatarBoneRole.Chest,
            EHumanoidAvatarBoneRole.Neck => EHumanoidAvatarBoneRole.UpperChest,
            EHumanoidAvatarBoneRole.Head => EHumanoidAvatarBoneRole.Neck,
            EHumanoidAvatarBoneRole.Jaw or EHumanoidAvatarBoneRole.LeftEye or EHumanoidAvatarBoneRole.RightEye
                => EHumanoidAvatarBoneRole.Head,
            EHumanoidAvatarBoneRole.LeftShoulder => EHumanoidAvatarBoneRole.UpperChest,
            EHumanoidAvatarBoneRole.LeftUpperArm => EHumanoidAvatarBoneRole.LeftShoulder,
            EHumanoidAvatarBoneRole.LeftLowerArm => EHumanoidAvatarBoneRole.LeftUpperArm,
            EHumanoidAvatarBoneRole.LeftHand => EHumanoidAvatarBoneRole.LeftLowerArm,
            EHumanoidAvatarBoneRole.RightShoulder => EHumanoidAvatarBoneRole.UpperChest,
            EHumanoidAvatarBoneRole.RightUpperArm => EHumanoidAvatarBoneRole.RightShoulder,
            EHumanoidAvatarBoneRole.RightLowerArm => EHumanoidAvatarBoneRole.RightUpperArm,
            EHumanoidAvatarBoneRole.RightHand => EHumanoidAvatarBoneRole.RightLowerArm,
            EHumanoidAvatarBoneRole.LeftUpperLeg => EHumanoidAvatarBoneRole.Hips,
            EHumanoidAvatarBoneRole.LeftLowerLeg => EHumanoidAvatarBoneRole.LeftUpperLeg,
            EHumanoidAvatarBoneRole.LeftFoot => EHumanoidAvatarBoneRole.LeftLowerLeg,
            EHumanoidAvatarBoneRole.LeftToes => EHumanoidAvatarBoneRole.LeftFoot,
            EHumanoidAvatarBoneRole.RightUpperLeg => EHumanoidAvatarBoneRole.Hips,
            EHumanoidAvatarBoneRole.RightLowerLeg => EHumanoidAvatarBoneRole.RightUpperLeg,
            EHumanoidAvatarBoneRole.RightFoot => EHumanoidAvatarBoneRole.RightLowerLeg,
            EHumanoidAvatarBoneRole.RightToes => EHumanoidAvatarBoneRole.RightFoot,
            >= EHumanoidAvatarBoneRole.LeftThumbProximal and <= EHumanoidAvatarBoneRole.LeftLittleDistal
                => GetFingerParentRole(role, EHumanoidAvatarBoneRole.LeftHand),
            >= EHumanoidAvatarBoneRole.RightThumbProximal and <= EHumanoidAvatarBoneRole.RightLittleDistal
                => GetFingerParentRole(role, EHumanoidAvatarBoneRole.RightHand),
            _ => null,
        };

    private static EHumanoidAvatarBoneRole GetFingerParentRole(
        EHumanoidAvatarBoneRole role,
        EHumanoidAvatarBoneRole handRole)
    {
        int offset = role >= EHumanoidAvatarBoneRole.RightThumbProximal
            ? (int)role - (int)EHumanoidAvatarBoneRole.RightThumbProximal
            : (int)role - (int)EHumanoidAvatarBoneRole.LeftThumbProximal;
        int segment = offset % 3;
        return segment == 0 ? handRole : (EHumanoidAvatarBoneRole)((int)role - 1);
    }

    private HumanoidAvatarJointLimit CreateDefaultJointLimit(
        EHumanoidAvatarBoneRole role,
        SceneNode? node)
        => new()
        {
            UseDefaultValues = true,
            AxisLength = node is null ? 0.0f : EstimateJointAxisLength(role, node),
        };

    private float EstimateJointAxisLength(EHumanoidAvatarBoneRole role, SceneNode node)
    {
        EHumanoidAvatarBoneRole? childRole = GetPrimaryChildRole(role);
        if (childRole.HasValue && GetBoneDefinition(childRole.Value).Node is SceneNode semanticChild)
            return Vector3.Distance(GetHumanoidBindWorldPose(node).Translation, GetHumanoidBindWorldPose(semanticChild).Translation);

        float shortest = float.PositiveInfinity;
        foreach (var childTransform in node.Transform.Children)
        {
            SceneNode? child = childTransform.SceneNode;
            if (child is null)
                continue;
            float distance = Vector3.Distance(
                GetHumanoidBindWorldPose(node).Translation,
                GetHumanoidBindWorldPose(child).Translation);
            if (distance > 1e-6f && distance < shortest)
                shortest = distance;
        }
        return float.IsFinite(shortest) ? shortest : 0.0f;
    }

    private static EHumanoidAvatarBoneRole? GetPrimaryChildRole(EHumanoidAvatarBoneRole role)
        => role switch
        {
            EHumanoidAvatarBoneRole.Hips => EHumanoidAvatarBoneRole.Spine,
            EHumanoidAvatarBoneRole.Spine => EHumanoidAvatarBoneRole.Chest,
            EHumanoidAvatarBoneRole.Chest => EHumanoidAvatarBoneRole.UpperChest,
            EHumanoidAvatarBoneRole.UpperChest => EHumanoidAvatarBoneRole.Neck,
            EHumanoidAvatarBoneRole.Neck => EHumanoidAvatarBoneRole.Head,
            EHumanoidAvatarBoneRole.LeftShoulder => EHumanoidAvatarBoneRole.LeftUpperArm,
            EHumanoidAvatarBoneRole.LeftUpperArm => EHumanoidAvatarBoneRole.LeftLowerArm,
            EHumanoidAvatarBoneRole.LeftLowerArm => EHumanoidAvatarBoneRole.LeftHand,
            EHumanoidAvatarBoneRole.RightShoulder => EHumanoidAvatarBoneRole.RightUpperArm,
            EHumanoidAvatarBoneRole.RightUpperArm => EHumanoidAvatarBoneRole.RightLowerArm,
            EHumanoidAvatarBoneRole.RightLowerArm => EHumanoidAvatarBoneRole.RightHand,
            EHumanoidAvatarBoneRole.LeftUpperLeg => EHumanoidAvatarBoneRole.LeftLowerLeg,
            EHumanoidAvatarBoneRole.LeftLowerLeg => EHumanoidAvatarBoneRole.LeftFoot,
            EHumanoidAvatarBoneRole.LeftFoot => EHumanoidAvatarBoneRole.LeftToes,
            EHumanoidAvatarBoneRole.RightUpperLeg => EHumanoidAvatarBoneRole.RightLowerLeg,
            EHumanoidAvatarBoneRole.RightLowerLeg => EHumanoidAvatarBoneRole.RightFoot,
            EHumanoidAvatarBoneRole.RightFoot => EHumanoidAvatarBoneRole.RightToes,
            _ => null,
        };

    private static HumanoidAvatarMuscleLimit? FindMuscleLimit(
        HumanoidAvatarMuscleLimit[]? limits,
        EHumanoidValue value)
    {
        if (limits is null)
            return null;
        for (int i = 0; i < limits.Length; i++)
            if (limits[i].Muscle == value)
                return limits[i];
        return null;
    }

    private static void ValidateRequiredChainOrder(SceneNode?[] nodes, List<string> diagnostics)
    {
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.Hips, EHumanoidAvatarBoneRole.Spine, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.Spine, EHumanoidAvatarBoneRole.Head, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.Spine, EHumanoidAvatarBoneRole.LeftUpperArm, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.LeftUpperArm, EHumanoidAvatarBoneRole.LeftLowerArm, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.LeftLowerArm, EHumanoidAvatarBoneRole.LeftHand, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.Spine, EHumanoidAvatarBoneRole.RightUpperArm, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.RightUpperArm, EHumanoidAvatarBoneRole.RightLowerArm, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.RightLowerArm, EHumanoidAvatarBoneRole.RightHand, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.Hips, EHumanoidAvatarBoneRole.LeftUpperLeg, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.LeftUpperLeg, EHumanoidAvatarBoneRole.LeftLowerLeg, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.LeftLowerLeg, EHumanoidAvatarBoneRole.LeftFoot, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.Hips, EHumanoidAvatarBoneRole.RightUpperLeg, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.RightUpperLeg, EHumanoidAvatarBoneRole.RightLowerLeg, diagnostics);
        ValidateAncestor(nodes, EHumanoidAvatarBoneRole.RightLowerLeg, EHumanoidAvatarBoneRole.RightFoot, diagnostics);
    }

    private static void ValidateOptionalDependencies(SceneNode?[] nodes, List<string> diagnostics)
    {
        for (int roleIndex = 0; roleIndex < nodes.Length; roleIndex++)
        {
            SceneNode? node = nodes[roleIndex];
            if (node is null)
                continue;

            EHumanoidAvatarBoneRole role = (EHumanoidAvatarBoneRole)roleIndex;
            EHumanoidAvatarBoneRole? parentRole = GetParentRole(role);
            if (!parentRole.HasValue || nodes[(int)parentRole.Value] is not SceneNode parent)
                continue;
            if (!IsDescendantOrSelf(parent, node) || ReferenceEquals(parent, node))
                diagnostics.Add($"Error: optional role {role} is not below its mapped semantic parent {parentRole.Value}.");
        }
    }

    private void ValidateAuxiliaryBones(
        SceneNode?[] semanticNodes,
        HumanoidAvatarAuxiliaryBoneBinding[] auxiliaryBones,
        List<string> diagnostics)
    {
        var semanticNodeSet = new HashSet<SceneNode>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < semanticNodes.Length; i++)
            if (semanticNodes[i] is SceneNode semanticNode)
                semanticNodeSet.Add(semanticNode);

        var auxiliaryNodeSet = new HashSet<SceneNode>(ReferenceEqualityComparer.Instance);
        var structuralIdentities = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < auxiliaryBones.Length; i++)
        {
            HumanoidAvatarAuxiliaryBoneBinding binding = auxiliaryBones[i];
            int parentIndex = (int)binding.ParentRole;
            if ((uint)parentIndex >= (uint)semanticNodes.Length
                || semanticNodes[parentIndex] is not SceneNode parentNode)
            {
                diagnostics.Add(
                    $"Error: auxiliary bone '{binding.NodeName}' references unmapped parent role {binding.ParentRole}.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(binding.StructuralSha256))
                diagnostics.Add($"Error: auxiliary bone '{binding.NodeName}' has no stable structural identity.");
            else if (!structuralIdentities.Add(binding.StructuralSha256))
                diagnostics.Add(
                    $"Error: auxiliary structural identity '{binding.StructuralSha256}' is assigned more than once.");

            SceneNode? node = ResolveAuxiliaryBoneNode(binding);
            if (node is null)
            {
                diagnostics.Add(
                    $"Error: auxiliary bone '{binding.NodeName}' ({binding.Kind}) cannot be resolved by path and structural identity.");
                continue;
            }

            if (semanticNodeSet.Contains(node))
                diagnostics.Add($"Error: auxiliary bone '{binding.NodeName}' is also assigned to a semantic humanoid role.");
            if (!auxiliaryNodeSet.Add(node))
                diagnostics.Add($"Error: scene node '{node.Name}' is assigned to more than one auxiliary binding.");
            if (ReferenceEquals(parentNode, node) || !IsDescendantOrSelf(parentNode, node))
                diagnostics.Add(
                    $"Error: auxiliary bone '{binding.NodeName}' is not below its semantic parent {binding.ParentRole}.");
            if (!IsFiniteInvertible(binding.NeutralLocalTransform))
                diagnostics.Add($"Error: auxiliary bone '{binding.NodeName}' has an invalid neutral local transform.");
            if (!IsFiniteVector(binding.LocalAxis) || binding.LocalAxis.LengthSquared() <= 1e-8f)
                diagnostics.Add($"Error: auxiliary bone '{binding.NodeName}' has an invalid local axis.");
            if (!float.IsFinite(binding.DistributionWeight)
                || binding.DistributionWeight < 0.0f
                || binding.DistributionWeight > 1.0f)
                diagnostics.Add(
                    $"Error: auxiliary bone '{binding.NodeName}' has distribution weight {binding.DistributionWeight}; expected [0, 1].");
        }
    }

    private static void ValidateTwistChains(
        SceneNode?[] semanticNodes,
        HumanoidAvatarTwistChain[] twistChains,
        HumanoidAvatarAuxiliaryBoneBinding[] auxiliaryBones,
        List<string> diagnostics)
    {
        var auxiliaryByHash = new Dictionary<string, HumanoidAvatarAuxiliaryBoneBinding>(
            auxiliaryBones.Length,
            StringComparer.Ordinal);
        for (int i = 0; i < auxiliaryBones.Length; i++)
            if (!string.IsNullOrWhiteSpace(auxiliaryBones[i].StructuralSha256))
                auxiliaryByHash.TryAdd(auxiliaryBones[i].StructuralSha256, auxiliaryBones[i]);

        for (int i = 0; i < twistChains.Length; i++)
        {
            HumanoidAvatarTwistChain chain = twistChains[i];
            int proximalIndex = (int)chain.ProximalRole;
            int distalIndex = (int)chain.DistalRole;
            int endIndex = (int)chain.EndRole;
            if ((uint)proximalIndex >= (uint)semanticNodes.Length
                || (uint)distalIndex >= (uint)semanticNodes.Length
                || (uint)endIndex >= (uint)semanticNodes.Length)
            {
                diagnostics.Add($"Error: twist chain '{chain.Name}' contains an invalid semantic role.");
                continue;
            }

            SceneNode? proximalNode = semanticNodes[proximalIndex];
            SceneNode? distalNode = semanticNodes[distalIndex];
            SceneNode? endNode = semanticNodes[endIndex];
            if (proximalNode is null || distalNode is null || endNode is null)
            {
                diagnostics.Add($"Error: twist chain '{chain.Name}' references an unmapped semantic role.");
                continue;
            }
            if (!IsDescendantOrSelf(proximalNode, distalNode)
                || ReferenceEquals(proximalNode, distalNode)
                || !IsDescendantOrSelf(distalNode, endNode)
                || ReferenceEquals(distalNode, endNode))
                diagnostics.Add($"Error: twist chain '{chain.Name}' has invalid proximal/distal/end ancestry.");

            if (!IsUnitInterval(chain.ProximalDistribution)
                || !IsUnitInterval(chain.DistalDistribution))
                diagnostics.Add($"Error: twist chain '{chain.Name}' distribution values must be finite and within [0, 1].");

            string[] auxiliaryHashes = chain.AuxiliaryStructuralSha256 ?? [];
            var chainHashes = new HashSet<string>(StringComparer.Ordinal);
            for (int j = 0; j < auxiliaryHashes.Length; j++)
            {
                string hash = auxiliaryHashes[j];
                if (!chainHashes.Add(hash))
                    diagnostics.Add($"Error: twist chain '{chain.Name}' references auxiliary bone '{hash}' more than once.");
                if (!auxiliaryByHash.TryGetValue(hash, out HumanoidAvatarAuxiliaryBoneBinding? auxiliary))
                {
                    diagnostics.Add($"Error: twist chain '{chain.Name}' references unknown auxiliary bone '{hash}'.");
                    continue;
                }
                if (auxiliary.ParentRole != chain.ProximalRole
                    && auxiliary.ParentRole != chain.DistalRole)
                    diagnostics.Add(
                        $"Error: twist chain '{chain.Name}' auxiliary '{auxiliary.NodeName}' belongs to unrelated role {auxiliary.ParentRole}.");
            }
        }
    }

    private static bool IsUnitInterval(float value)
        => float.IsFinite(value) && value is >= 0.0f and <= 1.0f;

    private static void ValidateAncestor(
        SceneNode?[] nodes,
        EHumanoidAvatarBoneRole ancestorRole,
        EHumanoidAvatarBoneRole descendantRole,
        List<string> diagnostics)
    {
        SceneNode? ancestor = nodes[(int)ancestorRole];
        SceneNode? descendant = nodes[(int)descendantRole];
        if (ancestor is null || descendant is null)
            return;
        if (!ReferenceEquals(ancestor, descendant) && IsDescendantOrSelf(ancestor, descendant))
            return;
        diagnostics.Add($"Error: role {descendantRole} is not below {ancestorRole}; the humanoid chain order is invalid.");
    }

    private static void ValidateBilateralSymmetry(
        SceneNode?[] nodes,
        HumanoidAvatarBodyAxes bodyAxes,
        List<string> diagnostics)
    {
        SceneNode? hips = nodes[(int)EHumanoidAvatarBoneRole.Hips];
        if (hips is null || !bodyAxes.IsFiniteOrthonormal())
            return;

        Vector3 origin = hips.Transform.BindMatrix.Translation;
        ValidateSymmetryPair(nodes, origin, bodyAxes.Right, EHumanoidAvatarBoneRole.LeftUpperArm, EHumanoidAvatarBoneRole.RightUpperArm, diagnostics);
        ValidateSymmetryPair(nodes, origin, bodyAxes.Right, EHumanoidAvatarBoneRole.LeftUpperLeg, EHumanoidAvatarBoneRole.RightUpperLeg, diagnostics);
        ValidateSymmetryPair(nodes, origin, bodyAxes.Right, EHumanoidAvatarBoneRole.LeftHand, EHumanoidAvatarBoneRole.RightHand, diagnostics);
        ValidateSymmetryPair(nodes, origin, bodyAxes.Right, EHumanoidAvatarBoneRole.LeftFoot, EHumanoidAvatarBoneRole.RightFoot, diagnostics);
    }

    private static void ValidateSymmetryPair(
        SceneNode?[] nodes,
        Vector3 origin,
        Vector3 bodyRight,
        EHumanoidAvatarBoneRole leftRole,
        EHumanoidAvatarBoneRole rightRole,
        List<string> diagnostics)
    {
        SceneNode? left = nodes[(int)leftRole];
        SceneNode? right = nodes[(int)rightRole];
        if (left is null || right is null)
            return;

        Vector3 leftOffset = left.Transform.BindMatrix.Translation - origin;
        Vector3 rightOffset = right.Transform.BindMatrix.Translation - origin;
        float leftSide = Vector3.Dot(leftOffset, bodyRight);
        float rightSide = Vector3.Dot(rightOffset, bodyRight);
        if (leftSide * rightSide >= 0.0f)
            diagnostics.Add($"Error: {leftRole} and {rightRole} do not lie on opposite sides of the avatar.");

        float leftLength = leftOffset.Length();
        float rightLength = rightOffset.Length();
        float maximum = MathF.Max(leftLength, rightLength);
        if (maximum > 1e-5f && MathF.Abs(leftLength - rightLength) / maximum > 0.45f)
            diagnostics.Add($"Review: {leftRole}/{rightRole} bind geometry is strongly asymmetric.");
    }

    private static void ValidateCanonicalPoseQuality(
        SceneNode?[] nodes,
        HumanoidAvatarBoneBinding[] bindings,
        HumanoidAvatarBodyAxes bodyAxes,
        List<string> diagnostics)
    {
        if (!bodyAxes.IsFiniteOrthonormal())
            return;

        ValidateArmCanonicalDirection(
            nodes,
            bindings,
            EHumanoidAvatarBoneRole.LeftUpperArm,
            EHumanoidAvatarBoneRole.LeftLowerArm,
            -bodyAxes.Right,
            diagnostics);
        ValidateArmCanonicalDirection(
            nodes,
            bindings,
            EHumanoidAvatarBoneRole.RightUpperArm,
            EHumanoidAvatarBoneRole.RightLowerArm,
            bodyAxes.Right,
            diagnostics);
    }

    private static void ValidateArmCanonicalDirection(
        SceneNode?[] nodes,
        HumanoidAvatarBoneBinding[] bindings,
        EHumanoidAvatarBoneRole upperRole,
        EHumanoidAvatarBoneRole lowerRole,
        Vector3 expectedDirection,
        List<string> diagnostics)
    {
        SceneNode? upper = nodes[(int)upperRole];
        SceneNode? lower = nodes[(int)lowerRole];
        if (upper is null || lower is null)
            return;
        Vector3 direction = lower.Transform.BindMatrix.Translation - upper.Transform.BindMatrix.Translation;
        if (direction.LengthSquared() <= 1e-8f)
        {
            diagnostics.Add($"Error: {upperRole} has a zero-length bind chain.");
            return;
        }
        direction = Vector3.Normalize(direction);
        if (Vector3.Dot(direction, expectedDirection) >= 0.35f)
            return;

        HumanoidAvatarBoneBinding? binding = FindBinding(bindings, upperRole);
        if (binding is not null && QuaternionAngleDegrees(binding.CanonicalPoseCorrection) > 0.1f)
            return;
        diagnostics.Add($"Review: {upperRole} is not close to a canonical T-pose and has no explicit canonical-pose correction.");
    }

    private static void ValidateMuscleLimits(
        HumanoidAvatarMuscleLimit[] limits,
        List<string> diagnostics)
    {
        var seen = new HashSet<EHumanoidValue>();
        for (int i = 0; i < limits.Length; i++)
        {
            HumanoidAvatarMuscleLimit limit = limits[i];
            if (!seen.Add(limit.Muscle))
                diagnostics.Add($"Error: muscle {limit.Muscle} has more than one serialized limit.");
            if (!float.IsFinite(limit.NegativeDegrees) || !float.IsFinite(limit.PositiveDegrees))
                diagnostics.Add($"Error: muscle {limit.Muscle} has a non-finite endpoint limit.");
        }
        if (limits.Length == 0)
            diagnostics.Add("Error: the avatar definition contains no humanoid muscle limits.");
    }

    private static bool IsFiniteJointLimit(HumanoidAvatarJointLimit? limit)
    {
        if (limit is null
            || !IsFiniteVector(limit.CenterDegrees)
            || !IsFiniteVector(limit.MinimumDegrees)
            || !IsFiniteVector(limit.MaximumDegrees)
            || !float.IsFinite(limit.AxisLength)
            || limit.AxisLength < 0.0f)
            return false;

        return limit.UseDefaultValues
            || IsValidCustomJointAxis(limit.MinimumDegrees.X, limit.MaximumDegrees.X)
                && IsValidCustomJointAxis(limit.MinimumDegrees.Y, limit.MaximumDegrees.Y)
                && IsValidCustomJointAxis(limit.MinimumDegrees.Z, limit.MaximumDegrees.Z);
    }

    private static bool IsValidCustomJointAxis(float minimumDegrees, float maximumDegrees)
        => minimumDegrees is >= -180.0f and <= 0.0f
        && maximumDegrees is >= 0.0f and <= 180.0f
        && minimumDegrees <= maximumDegrees;

    private static bool IsFiniteVector(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void AppendJointLimit(StringBuilder canonical, HumanoidAvatarJointLimit? limit)
    {
        limit ??= new HumanoidAvatarJointLimit();
        AppendCanonical(canonical, limit.UseDefaultValues);
        AppendVector(canonical, limit.CenterDegrees);
        AppendVector(canonical, limit.MinimumDegrees);
        AppendVector(canonical, limit.MaximumDegrees);
        AppendCanonical(canonical, limit.AxisLength);
    }

}

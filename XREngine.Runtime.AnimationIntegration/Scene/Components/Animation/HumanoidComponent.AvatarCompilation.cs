using System.Numerics;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private CompiledHumanoidAvatarDefinition? _compiledAvatarDefinition;

    /// <summary>
    /// Invalidates derived role-indexed data. The next explicit definition
    /// validation rebuilds it; the frame loop never scans or remaps a skeleton.
    /// </summary>
    private void InvalidateCompiledAvatarDefinition()
    {
        _compiledAvatarDefinition = null;
        _nativePoseWorkspace.UnbindDefinition();
        _currentBodyFrameDiagnostic = default;
        _lastNativeFrameAccepted = false;
        _hasCanonicalProjectedFeetY = false;
        _canonicalProjectedFeetOwner = null;
    }

    internal bool TryGetCompiledAvatarDefinition(out CompiledHumanoidAvatarDefinition compiled)
    {
        compiled = _compiledAvatarDefinition!;
        return compiled is not null
            && AvatarDefinition.IsFinalized
            && compiled.SchemaVersion == AvatarDefinition.SchemaVersion
            && compiled.DefinitionRevision == AvatarDefinition.DefinitionRevision
            && string.Equals(
                compiled.DefinitionContentSha256,
                AvatarDefinition.DefinitionContentSha256,
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Exposes only the validated, compiled IK inputs needed by the post-pose
    /// humanoid solver. Callers must not read mutable authoring metadata during
    /// evaluation because it may not match the definition that produced the pose.
    /// </summary>
    internal bool TryGetCompiledAvatarIKSettings(
        out float armStretch,
        out float legStretch,
        out float feetSpacing,
        out Vector3 bodyRight,
        out float modelUnitsPerMeter,
        out int schemaVersion,
        out int definitionRevision,
        out string definitionContentSha256)
    {
        if (TryGetCompiledAvatarDefinition(out CompiledHumanoidAvatarDefinition compiled))
        {
            HumanoidAvatarSolverSettings settings = compiled.SolverSettings;
            armStretch = settings.ArmStretch;
            legStretch = settings.LegStretch;
            feetSpacing = settings.FeetSpacing;
            bodyRight = compiled.BodyAxes.Right;
            modelUnitsPerMeter = compiled.ModelUnitsPerMeter;
            schemaVersion = compiled.SchemaVersion;
            definitionRevision = compiled.DefinitionRevision;
            definitionContentSha256 = compiled.DefinitionContentSha256;
            return true;
        }

        armStretch = 0.0f;
        legStretch = 0.0f;
        feetSpacing = 0.0f;
        bodyRight = -Vector3.UnitX;
        modelUnitsPerMeter = 0.0f;
        schemaVersion = 0;
        definitionRevision = 0;
        definitionContentSha256 = string.Empty;
        return false;
    }

    /// <summary>
    /// Gets the immutable, neutral-body-relative rotation that maps a canonical
    /// humanoid IK goal orientation into a compiled end-effector basis.
    /// </summary>
    internal bool TryGetCompiledAvatarIKGoalRotationOffset(
        EHumanoidAvatarBoneRole goalRole,
        out Quaternion rotationOffset)
    {
        if (!TryGetCompiledAvatarDefinition(out CompiledHumanoidAvatarDefinition compiled)
            || compiled.GetNode(goalRole) is null
            || !TryGetFiniteRotation(
                compiled.ZeroMuscleModelRootTransforms[(int)goalRole] * compiled.BodyDefinition.InverseNeutralBodyFrame,
                out Quaternion goalRotation))
        {
            rotationOffset = Quaternion.Identity;
            return false;
        }

        rotationOffset = goalRotation;
        if (!IsFinite(rotationOffset))
        {
            rotationOffset = Quaternion.Identity;
            return false;
        }

        return true;
    }

    private static bool TryGetFiniteRotation(Matrix4x4 matrix, out Quaternion rotation)
    {
        if (!Matrix4x4.Decompose(matrix, out _, out rotation, out _)
            || !IsFinite(rotation)
            || rotation.LengthSquared() <= 1e-8f)
        {
            rotation = Quaternion.Identity;
            return false;
        }

        rotation = Quaternion.Normalize(rotation);
        return true;
    }

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && float.IsFinite(value.W);

    private bool TryCompileAvatarDefinition(out string diagnostic)
    {
        HumanoidAvatarDefinitionMetadata definition = AvatarDefinition;
        if (definition.Status != EHumanoidAvatarDefinitionStatus.Valid)
        {
            InvalidateCompiledAvatarDefinition();
            diagnostic = $"Avatar definition status is {definition.Status}.";
            return false;
        }

        var nodes = new SceneNode?[CompiledHumanoidAvatarDefinition.RoleCount];
        var neutralLocal = new Matrix4x4[CompiledHumanoidAvatarDefinition.RoleCount];
        var neutralWorld = new Matrix4x4[CompiledHumanoidAvatarDefinition.RoleCount];
        var canonicalCorrections = new Quaternion[CompiledHumanoidAvatarDefinition.RoleCount];
        var preRotations = new Quaternion[CompiledHumanoidAvatarDefinition.RoleCount];
        var postRotations = new Quaternion[CompiledHumanoidAvatarDefinition.RoleCount];
        var rotationOrders = new EHumanoidAvatarRotationOrder[CompiledHumanoidAvatarDefinition.RoleCount];
        var hasTranslationDegreesOfFreedom = new bool[CompiledHumanoidAvatarDefinition.RoleCount];
        var axisMappings = new BoneAxisMapping[CompiledHumanoidAvatarDefinition.RoleCount];
        var hasAxisMappings = new bool[CompiledHumanoidAvatarDefinition.RoleCount];
        var jointBases = new Quaternion[CompiledHumanoidAvatarDefinition.RoleCount];
        var hasContinuousJointBases = new bool[CompiledHumanoidAvatarDefinition.RoleCount];
        var jointLimits = new HumanoidAvatarJointLimit[CompiledHumanoidAvatarDefinition.RoleCount];
        var semanticParents = new EHumanoidAvatarBoneRole?[CompiledHumanoidAvatarDefinition.RoleCount];

        for (int i = 0; i < nodes.Length; i++)
        {
            neutralLocal[i] = Matrix4x4.Identity;
            neutralWorld[i] = Matrix4x4.Identity;
            canonicalCorrections[i] = Quaternion.Identity;
            preRotations[i] = Quaternion.Identity;
            postRotations[i] = Quaternion.Identity;
            rotationOrders[i] = EHumanoidAvatarRotationOrder.ZXY;
            axisMappings[i] = BoneAxisMapping.Default;
            jointBases[i] = Quaternion.Identity;
            jointLimits[i] = new HumanoidAvatarJointLimit();
        }

        for (int i = 0; i < definition.Bones.Length; i++)
        {
            HumanoidAvatarBoneBinding binding = definition.Bones[i];
            int roleIndex = (int)binding.Role;
            if ((uint)roleIndex >= (uint)nodes.Length)
                continue;

            nodes[roleIndex] = GetBoneDefinition(binding.Role).Node;
            neutralLocal[roleIndex] = binding.NeutralLocalTransform;
            neutralWorld[roleIndex] = binding.NeutralWorldTransform;
            canonicalCorrections[roleIndex] = NormalizeFiniteQuaternion(binding.CanonicalPoseCorrection);
            preRotations[roleIndex] = NormalizeFiniteQuaternion(binding.PreRotation);
            postRotations[roleIndex] = NormalizeFiniteQuaternion(binding.PostRotation);
            rotationOrders[roleIndex] = binding.RotationOrder;
            hasTranslationDegreesOfFreedom[roleIndex] = binding.HasTranslationDoF;
            axisMappings[roleIndex] = binding.AxisMapping;
            hasAxisMappings[roleIndex] = binding.HasAxisMapping;
            jointLimits[roleIndex] = CopyJointLimit(binding.JointLimit);
            semanticParents[roleIndex] = binding.ParentRole;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i] is null)
                continue;

            EHumanoidAvatarBoneRole role = (EHumanoidAvatarBoneRole)i;
            if (!HumanoidCanonicalPoseAuthoring.TryCreateJointBasis(
                    definition.Bones,
                    definition.BodyAxes,
                    role,
                    out jointBases[i]))
            {
                InvalidateCompiledAvatarDefinition();
                diagnostic = $"Canonical joint frame for humanoid role {role} is degenerate.";
                return false;
            }
            hasContinuousJointBases[i] = true;
        }

        var muscleRanges = new Vector2[CompiledHumanoidAvatarDefinition.MuscleCount];
        for (int i = 0; i < definition.MuscleLimits.Length; i++)
        {
            HumanoidAvatarMuscleLimit limit = definition.MuscleLimits[i];
            int muscleIndex = (int)limit.Muscle;
            if ((uint)muscleIndex < (uint)muscleRanges.Length)
                muscleRanges[muscleIndex] = new Vector2(limit.NegativeDegrees, limit.PositiveDegrees);
        }

        // Auxiliary nodes must be bound first: concrete semantic bridges refer to
        // these immutable indices instead of searching hierarchy at evaluation.
        if (!TryCompileAuxiliaryBones(
                definition.AuxiliaryBones,
                out CompiledHumanoidAvatarAuxiliaryBone[] auxiliaryBones,
                out diagnostic)
            || !TryCompileBoneSolvePlans(
                nodes,
                neutralLocal,
                neutralWorld,
                canonicalCorrections,
                preRotations,
                postRotations,
                rotationOrders,
                hasTranslationDegreesOfFreedom,
                axisMappings,
                hasAxisMappings,
                jointBases,
                hasContinuousJointBases,
                jointLimits,
                semanticParents,
                auxiliaryBones,
                out CompiledHumanoidBoneSolvePlan[] boneSolvePlans,
                out int[] boneSolvePlanOrder,
                out CompiledHumanoidConcreteCommitTarget[] concreteCommitTargets,
                out int[] concreteCommitOrder,
                out Matrix4x4 hipsParentInModelRootFrame,
                out Matrix4x4 inverseHipsParentInModelRootFrame,
                out diagnostic))
        {
            InvalidateCompiledAvatarDefinition();
            return false;
        }

        Matrix4x4[] neutralBodyPose = CreateZeroMuscleModelRootPose(
            boneSolvePlans,
            boneSolvePlanOrder,
            auxiliaryBones);
        if (!TryCompileTwistChains(
                definition.TwistChains,
                auxiliaryBones,
                boneSolvePlans,
                neutralBodyPose,
                out CompiledHumanoidAvatarTwistChain[] twistChains,
                out diagnostic))
        {
            InvalidateCompiledAvatarDefinition();
            return false;
        }

        if (!TryCompileBodyHierarchyGuards(nodes, auxiliaryBones, out CompiledHumanoidHierarchyGuard[] bodyHierarchyGuards, out diagnostic)
            || !CompiledHumanoidBodyDefinition.TryCompile(
                definition.BodyDefinition, boneSolvePlans, neutralBodyPose,
                out CompiledHumanoidBodyDefinition bodyDefinition, out diagnostic))
        {
            InvalidateCompiledAvatarDefinition();
            AvatarDefinitionPlaybackDiagnostic = diagnostic;
            definition.Status = EHumanoidAvatarDefinitionStatus.Invalid;
            definition.EditorConfirmed = false;
            definition.Diagnostics = [.. definition.Diagnostics, $"Error: {diagnostic}"];
            return false;
        }
        var hipsParentChain = new List<TransformBase>();
        for (TransformBase? parent = nodes[(int)EHumanoidAvatarBoneRole.Hips]?.Transform.Parent;
            parent is not null && !ReferenceEquals(parent, SceneNode.Transform); parent = parent.Parent)
            hipsParentChain.Add(parent);

        var compiledDefinition = new CompiledHumanoidAvatarDefinition(
            definition.SchemaVersion,
            definition.DefinitionRevision,
            definition.DefinitionContentSha256,
            nodes,
            neutralLocal,
            neutralWorld,
            neutralBodyPose,
            canonicalCorrections,
            preRotations,
            postRotations,
            rotationOrders,
            hasTranslationDegreesOfFreedom,
            axisMappings,
            hasAxisMappings,
            jointLimits,
            muscleRanges,
            CopySolverSettings(definition.SolverSettings),
            CopyBodyAxes(definition.BodyAxes),
            bodyDefinition,
            definition.HumanScale,
            definition.ModelUnitsPerMeter,
            twistChains,
            auxiliaryBones,
            boneSolvePlans,
            boneSolvePlanOrder,
            concreteCommitTargets,
            concreteCommitOrder,
            hipsParentInModelRootFrame,
            inverseHipsParentInModelRootFrame,
            SceneNode.Transform,
            hipsParentChain.ToArray(),
            bodyHierarchyGuards);
        _compiledAvatarDefinition = compiledDefinition;
        _nativePoseWorkspace.BindDefinition(compiledDefinition);
        diagnostic = string.Empty;
        return true;
    }

    /// <summary>Compiles the exact zero-muscle FK reference, including canonical-pose corrections.</summary>
    private static Matrix4x4[] CreateZeroMuscleModelRootPose(
        CompiledHumanoidBoneSolvePlan[] plans, int[] order, CompiledHumanoidAvatarAuxiliaryBone[] auxiliaries)
    {
        var result = new Matrix4x4[plans.Length];
        for (int i = 0; i < order.Length; i++)
        {
            int index = order[i];
            ref readonly CompiledHumanoidBoneSolvePlan plan = ref plans[index];
            Matrix4x4 local = Matrix4x4.CreateScale(plan.NeutralScale)
                * Matrix4x4.CreateFromQuaternion(plan.ZeroMuscleRotation)
                * Matrix4x4.CreateTranslation(plan.NeutralTranslation);
            ReadOnlySpan<CompiledHumanoidParentBridgeSegment> segments = plan.ParentBridgeSegments;
            for (int j = 0; j < segments.Length; j++)
                local *= segments[j].AuxiliaryBoneIndex >= 0
                    ? auxiliaries[segments[j].AuxiliaryBoneIndex].NeutralLocalTransform
                    : segments[j].NeutralLocalTransform;
            result[index] = plan.MappedAncestorPlanIndex >= 0
                ? local * result[plan.MappedAncestorPlanIndex] : local;
        }
        return result;
    }

    private bool TryCompileBoneSolvePlans(
        SceneNode?[] nodes,
        Matrix4x4[] neutralLocal,
        Matrix4x4[] neutralWorld,
        Quaternion[] canonicalCorrections,
        Quaternion[] preRotations,
        Quaternion[] postRotations,
        EHumanoidAvatarRotationOrder[] rotationOrders,
        bool[] hasTranslationDegreesOfFreedom,
        BoneAxisMapping[] axisMappings,
        bool[] hasAxisMappings,
        Quaternion[] jointBases,
        bool[] hasContinuousJointBases,
        HumanoidAvatarJointLimit[] jointLimits,
        EHumanoidAvatarBoneRole?[] semanticParents,
        CompiledHumanoidAvatarAuxiliaryBone[] auxiliaryBones,
        out CompiledHumanoidBoneSolvePlan[] plans,
        out int[] planOrder,
        out CompiledHumanoidConcreteCommitTarget[] concreteCommitTargets,
        out int[] concreteCommitOrder,
        out Matrix4x4 hipsParentInModelRootFrame,
        out Matrix4x4 inverseHipsParentInModelRootFrame,
        out string diagnostic)
    {
        var rolesByNode = new Dictionary<SceneNode, int>(nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i] is SceneNode node)
                rolesByNode.TryAdd(node, i);

        var auxiliaryByNode = new Dictionary<SceneNode, int>(auxiliaryBones.Length);
        for (int i = 0; i < auxiliaryBones.Length; i++)
            auxiliaryByNode.TryAdd(auxiliaryBones[i].Node, i);

        plans = new CompiledHumanoidBoneSolvePlan[nodes.Length];
        for (int i = 0; i < plans.Length; i++)
        {
            if (ReferenceEquals(nodes[i], SceneNode))
            {
                hipsParentInModelRootFrame = Matrix4x4.Identity;
                inverseHipsParentInModelRootFrame = Matrix4x4.Identity;
                planOrder = [];
                concreteCommitTargets = [];
                concreteCommitOrder = [];
                diagnostic = $"Humanoid role {(EHumanoidAvatarBoneRole)i} cannot map to the HumanoidComponent model-root node.";
                return false;
            }

            if (!TryDecomposeExactNeutralTrs(
                    neutralLocal[i],
                    out Vector3 scale,
                    out Quaternion rotation,
                    out Vector3 translation))
            {
                hipsParentInModelRootFrame = Matrix4x4.Identity;
                inverseHipsParentInModelRootFrame = Matrix4x4.Identity;
                planOrder = [];
                concreteCommitTargets = [];
                concreteCommitOrder = [];
                diagnostic = $"Neutral local transform for humanoid role {(EHumanoidAvatarBoneRole)i} is not a finite exact TRS transform.";
                return false;
            }

            int ancestorIndex = FindNearestMappedAncestor(nodes[i], rolesByNode);
            if (!IsSemanticParentConcreteAncestor(nodes[i], semanticParents[i], nodes))
            {
                hipsParentInModelRootFrame = Matrix4x4.Identity;
                inverseHipsParentInModelRootFrame = Matrix4x4.Identity;
                planOrder = [];
                concreteCommitTargets = [];
                concreteCommitOrder = [];
                diagnostic = $"Semantic parent for humanoid role {(EHumanoidAvatarBoneRole)i} is not a concrete skeleton ancestor.";
                return false;
            }

            Matrix4x4 ancestorWorld = ancestorIndex >= 0 ? neutralWorld[ancestorIndex] : Matrix4x4.Identity;
            if (!Matrix4x4.Invert(ancestorWorld, out Matrix4x4 inverseAncestorWorld))
            {
                hipsParentInModelRootFrame = Matrix4x4.Identity;
                inverseHipsParentInModelRootFrame = Matrix4x4.Identity;
                planOrder = [];
                concreteCommitTargets = [];
                concreteCommitOrder = [];
                diagnostic = $"Neutral world transform for mapped ancestor of humanoid role {(EHumanoidAvatarBoneRole)i} is non-invertible.";
                return false;
            }

            if (!Matrix4x4.Invert(neutralLocal[i], out Matrix4x4 inverseNeutralLocal))
            {
                hipsParentInModelRootFrame = Matrix4x4.Identity;
                inverseHipsParentInModelRootFrame = Matrix4x4.Identity;
                planOrder = [];
                concreteCommitTargets = [];
                concreteCommitOrder = [];
                diagnostic = $"Neutral local transform for humanoid role {(EHumanoidAvatarBoneRole)i} is non-invertible.";
                return false;
            }

            Matrix4x4 neutralRelativeToAncestor = neutralWorld[i] * inverseAncestorWorld;
            if (!TryCompileParentBridge(
                    nodes[i],
                    ancestorIndex >= 0 ? nodes[ancestorIndex] : null,
                    auxiliaryByNode,
                    auxiliaryBones,
                    out CompiledHumanoidParentBridgeSegment[] bridgeSegments,
                    out Matrix4x4 bridgeProduct,
                    out diagnostic))
            {
                hipsParentInModelRootFrame = Matrix4x4.Identity;
                inverseHipsParentInModelRootFrame = Matrix4x4.Identity;
                planOrder = [];
                concreteCommitTargets = [];
                concreteCommitOrder = [];
                return false;
            }

            Matrix4x4 expectedRelative = neutralLocal[i] * bridgeProduct;
            if (!MatrixApproximatelyEqual(expectedRelative, neutralRelativeToAncestor))
            {
                hipsParentInModelRootFrame = Matrix4x4.Identity;
                inverseHipsParentInModelRootFrame = Matrix4x4.Identity;
                planOrder = [];
                concreteCommitTargets = [];
                concreteCommitOrder = [];
                diagnostic = $"Neutral concrete bridge for humanoid role {(EHumanoidAvatarBoneRole)i} does not reproduce its finalized neutral hierarchy product.";
                return false;
            }

            HumanoidAvatarJointLimit limit = jointLimits[i];
            var compiledLimit = new CompiledHumanoidJointLimit(
                limit.UseDefaultValues,
                limit.CenterDegrees,
                limit.MinimumDegrees,
                limit.MaximumDegrees,
                limit.AxisLength);
            Quaternion zeroMuscleRotation = Quaternion.Normalize(rotation * canonicalCorrections[i]);
            Quaternion restJoint = CompiledHumanoidPoseSolver.CreateRestJoint(
                compiledLimit,
                rotationOrders[i],
                preRotations[i],
                postRotations[i]);
            Quaternion inverseRestJoint = Quaternion.Inverse(restJoint);
            if (!IsFiniteNonZero(zeroMuscleRotation)
                || !IsFiniteNonZero(inverseRestJoint)
                || !QuaternionApproximatelyEqual(
                    CompiledHumanoidPoseSolver.EvaluateLocalRotation(
                        new CompiledHumanoidBoneSolvePlan(
                            (EHumanoidAvatarBoneRole)i, nodes[i], scale, rotation, translation,
                            neutralRelativeToAncestor, bridgeSegments, canonicalCorrections[i],
                            preRotations[i], postRotations[i], rotationOrders[i],
                            hasTranslationDegreesOfFreedom[i], axisMappings[i], hasAxisMappings[i],
                            compiledLimit, semanticParents[i],
                            ancestorIndex >= 0 ? (EHumanoidAvatarBoneRole)ancestorIndex : null,
                            ancestorIndex, zeroMuscleRotation, inverseRestJoint,
                            jointBases[i], hasContinuousJointBases[i]),
                        0.0f, 0.0f, 0.0f),
                    zeroMuscleRotation))
            {
                hipsParentInModelRootFrame = Matrix4x4.Identity;
                inverseHipsParentInModelRootFrame = Matrix4x4.Identity;
                planOrder = [];
                concreteCommitTargets = [];
                concreteCommitOrder = [];
                diagnostic = $"Zero-muscle rotation invariant failed for humanoid role {(EHumanoidAvatarBoneRole)i}.";
                return false;
            }
            plans[i] = new CompiledHumanoidBoneSolvePlan(
                (EHumanoidAvatarBoneRole)i,
                nodes[i],
                scale,
                rotation,
                translation,
                neutralRelativeToAncestor,
                bridgeSegments,
                canonicalCorrections[i],
                preRotations[i],
                postRotations[i],
                rotationOrders[i],
                hasTranslationDegreesOfFreedom[i],
                axisMappings[i],
                hasAxisMappings[i],
                compiledLimit,
                semanticParents[i],
                ancestorIndex >= 0 ? (EHumanoidAvatarBoneRole)ancestorIndex : null,
                ancestorIndex,
                zeroMuscleRotation,
                inverseRestJoint,
                jointBases[i],
                hasContinuousJointBases[i]);
        }

        int hipsIndex = (int)EHumanoidAvatarBoneRole.Hips;
        if (!Matrix4x4.Invert(neutralLocal[hipsIndex], out Matrix4x4 inverseNeutralHipsLocal))
        {
            hipsParentInModelRootFrame = Matrix4x4.Identity;
            inverseHipsParentInModelRootFrame = Matrix4x4.Identity;
            planOrder = [];
            concreteCommitTargets = [];
            concreteCommitOrder = [];
            diagnostic = "Neutral Hips local transform is non-invertible; cannot compile canonical Hips-parent model-root frame.";
            return false;
        }

        hipsParentInModelRootFrame = inverseNeutralHipsLocal * neutralWorld[hipsIndex];
        if (!Matrix4x4.Invert(hipsParentInModelRootFrame, out inverseHipsParentInModelRootFrame))
        {
            planOrder = [];
            concreteCommitTargets = [];
            concreteCommitOrder = [];
            diagnostic = "Canonical neutral Hips-parent model-root frame is non-invertible.";
            return false;
        }

        planOrder = CreateTopologicalPlanOrder(plans);
        if (!TryCreateConcreteCommitOrder(nodes, auxiliaryBones, out concreteCommitTargets, out concreteCommitOrder, out diagnostic))
            return false;
        diagnostic = string.Empty;
        return true;
    }

    private static int FindNearestMappedAncestor(SceneNode? node, Dictionary<SceneNode, int> rolesByNode)
    {
        for (SceneNode? ancestor = node?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (rolesByNode.TryGetValue(ancestor, out int roleIndex))
                return roleIndex;

        return -1;
    }

    private static bool IsSemanticParentConcreteAncestor(
        SceneNode? node,
        EHumanoidAvatarBoneRole? semanticParent,
        SceneNode?[] nodes)
    {
        // Optional roles may be absent. Their semantic descendants then bind to
        // the nearest actual mapped ancestor found from the concrete hierarchy.
        // Only an explicitly bound role can contradict its declared parent.
        if (node is null || !semanticParent.HasValue)
            return true;

        int parentIndex = (int)semanticParent.Value;
        if ((uint)parentIndex >= (uint)nodes.Length || nodes[parentIndex] is not SceneNode expectedParent)
            return true;

        for (SceneNode? ancestor = node?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, expectedParent))
                return true;

        return false;
    }

    private bool TryCompileParentBridge(
        SceneNode? node,
        SceneNode? mappedAncestor,
        Dictionary<SceneNode, int> auxiliaryByNode,
        CompiledHumanoidAvatarAuxiliaryBone[] auxiliaryBones,
        out CompiledHumanoidParentBridgeSegment[] segments,
        out Matrix4x4 product,
        out string diagnostic)
    {
        var reversed = new List<CompiledHumanoidParentBridgeSegment>();
        product = Matrix4x4.Identity;
        if (node is null)
        {
            segments = [];
            diagnostic = string.Empty;
            return true;
        }

        SceneNode boundary = mappedAncestor ?? SceneNode;
        bool reachedBoundary = false;
        // Neutral world transforms are already relative to this component's
        // model-root frame, so an unmapped root parent is deliberately excluded.
        for (SceneNode? current = node.Parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, boundary))
            {
                reachedBoundary = true;
                break;
            }

            if (auxiliaryByNode.TryGetValue(current, out int auxiliaryIndex))
            {
                reversed.Add(new CompiledHumanoidParentBridgeSegment(
                    auxiliaryBones[auxiliaryIndex].NeutralLocalTransform,
                    auxiliaryIndex));
                product *= auxiliaryBones[auxiliaryIndex].NeutralLocalTransform;
                continue;
            }

            if (!_humanoidBindLocalPoses.TryGetValue(current, out Matrix4x4 neutralLocal))
            {
                segments = [];
                diagnostic = $"Unmapped helper '{current.Name}' has no captured humanoid bind local transform.";
                return false;
            }
            if (!IsFinite(neutralLocal))
            {
                segments = [];
                diagnostic = $"Unmapped helper '{current.Name}' has a non-finite neutral local transform.";
                return false;
            }

            reversed.Add(new CompiledHumanoidParentBridgeSegment(neutralLocal, -1));
            product *= neutralLocal;
        }

        if (!reachedBoundary)
        {
            segments = [];
            diagnostic = mappedAncestor is null
                ? "Humanoid role is not a descendant of the HumanoidComponent model-root node."
                : "Nearest mapped ancestor is not a concrete skeleton ancestor.";
            return false;
        }

        segments = reversed.ToArray();
        diagnostic = string.Empty;
        return true;
    }

    private static bool IsConcreteAncestor(SceneNode node, SceneNode expectedAncestor)
    {
        for (SceneNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, expectedAncestor))
                return true;

        return false;
    }

    private static bool TryCreateConcreteCommitOrder(
        SceneNode?[] nodes,
        CompiledHumanoidAvatarAuxiliaryBone[] auxiliaryBones,
        out CompiledHumanoidConcreteCommitTarget[] targets,
        out int[] order,
        out string diagnostic)
    {
        var targetByNode = new Dictionary<SceneNode, int>(nodes.Length + auxiliaryBones.Length);
        var candidates = new List<CompiledHumanoidConcreteCommitTarget>(nodes.Length + auxiliaryBones.Length);
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i] is SceneNode node)
            {
                targetByNode.Add(node, candidates.Count);
                candidates.Add(new CompiledHumanoidConcreteCommitTarget(false, i, -1));
            }
        for (int i = 0; i < auxiliaryBones.Length; i++)
        {
            targetByNode.Add(auxiliaryBones[i].Node, candidates.Count);
            candidates.Add(new CompiledHumanoidConcreteCommitTarget(true, i, -1));
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            SceneNode node = candidates[i].IsAuxiliary
                ? auxiliaryBones[candidates[i].Index].Node
                : nodes[candidates[i].Index]!;
            int parentTarget = -1;
            for (SceneNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (targetByNode.TryGetValue(ancestor, out int foundParentTarget))
                {
                    parentTarget = foundParentTarget;
                    break;
                }
            candidates[i] = new CompiledHumanoidConcreteCommitTarget(
                candidates[i].IsAuxiliary,
                candidates[i].Index,
                parentTarget);
        }

        targets = candidates.ToArray();
        var emitted = new bool[targets.Length];
        order = new int[targets.Length];
        int count = 0;
        while (count < order.Length)
        {
            bool progressed = false;
            for (int i = 0; i < targets.Length; i++)
            {
                if (emitted[i] || (targets[i].ParentTargetIndex >= 0 && !emitted[targets[i].ParentTargetIndex]))
                    continue;
                emitted[i] = true;
                order[count++] = i;
                progressed = true;
            }
            if (progressed)
                continue;

            targets = [];
            order = [];
            diagnostic = "Concrete humanoid commit graph contains a parent cycle.";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool MatrixApproximatelyEqual(Matrix4x4 left, Matrix4x4 right)
        => MathF.Abs(left.M11 - right.M11) <= 1e-4f && MathF.Abs(left.M12 - right.M12) <= 1e-4f
        && MathF.Abs(left.M13 - right.M13) <= 1e-4f && MathF.Abs(left.M14 - right.M14) <= 1e-4f
        && MathF.Abs(left.M21 - right.M21) <= 1e-4f && MathF.Abs(left.M22 - right.M22) <= 1e-4f
        && MathF.Abs(left.M23 - right.M23) <= 1e-4f && MathF.Abs(left.M24 - right.M24) <= 1e-4f
        && MathF.Abs(left.M31 - right.M31) <= 1e-4f && MathF.Abs(left.M32 - right.M32) <= 1e-4f
        && MathF.Abs(left.M33 - right.M33) <= 1e-4f && MathF.Abs(left.M34 - right.M34) <= 1e-4f
        && MathF.Abs(left.M41 - right.M41) <= 1e-4f && MathF.Abs(left.M42 - right.M42) <= 1e-4f
        && MathF.Abs(left.M43 - right.M43) <= 1e-4f && MathF.Abs(left.M44 - right.M44) <= 1e-4f;

    private static bool QuaternionApproximatelyEqual(Quaternion left, Quaternion right)
        => MathF.Abs(MathF.Abs(Quaternion.Dot(left, right)) - 1.0f) <= 1e-4f;

    private static bool TryDecomposeExactNeutralTrs(
        Matrix4x4 matrix,
        out Vector3 scale,
        out Quaternion rotation,
        out Vector3 translation)
    {
        scale = Vector3.One;
        rotation = Quaternion.Identity;
        translation = Vector3.Zero;
        if (!IsFinite(matrix)
            || !Matrix4x4.Decompose(matrix, out scale, out rotation, out translation)
            || !IsFinite(scale)
            || !IsFinite(translation)
            || !IsFiniteNonZero(rotation))
            return false;

        rotation = Quaternion.Normalize(rotation);
        return MatrixApproximatelyEqual(
            Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation),
            matrix);
    }

    private static int[] CreateTopologicalPlanOrder(CompiledHumanoidBoneSolvePlan[] plans)
    {
        var order = new int[plans.Length];
        var emitted = new bool[plans.Length];
        int count = 0;
        while (count < order.Length)
        {
            bool progressed = false;
            for (int i = 0; i < plans.Length; i++)
            {
                if (emitted[i])
                    continue;

                int parent = plans[i].MappedAncestorPlanIndex;
                if (parent >= 0 && !emitted[parent])
                    continue;

                emitted[i] = true;
                order[count++] = i;
                progressed = true;
            }

            if (progressed)
                continue;

            // Avatar validation rejects hierarchy cycles. Preserve deterministic
            // behavior if malformed runtime data nevertheless reaches compilation.
            for (int i = 0; i < plans.Length; i++)
                if (!emitted[i])
                {
                    emitted[i] = true;
                    order[count++] = i;
                }
        }

        return order;
    }

    private bool TryCompileAuxiliaryBones(
        HumanoidAvatarAuxiliaryBoneBinding[] source,
        out CompiledHumanoidAvatarAuxiliaryBone[] compiled,
        out string diagnostic)
    {
        compiled = new CompiledHumanoidAvatarAuxiliaryBone[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            HumanoidAvatarAuxiliaryBoneBinding binding = source[i];
            SceneNode? node = ResolveAuxiliaryBoneNode(binding);
            if (node is null)
            {
                compiled = [];
                diagnostic =
                    $"Auxiliary humanoid bone '{binding.NodeName}' ({binding.Kind}) no longer matches the finalized skeleton.";
                return false;
            }

            if (ReferenceEquals(node, SceneNode))
            {
                compiled = [];
                diagnostic = $"Auxiliary humanoid bone '{binding.NodeName}' cannot map to the HumanoidComponent model-root node.";
                return false;
            }

            if (!TryDecomposeExactNeutralTrs(
                    binding.NeutralLocalTransform,
                    out Vector3 neutralScale,
                    out Quaternion neutralRotation,
                    out Vector3 neutralTranslation))
            {
                compiled = [];
                diagnostic = $"Auxiliary humanoid bone '{binding.NodeName}' has a non-finite or non-TRS neutral transform.";
                return false;
            }

            Vector3 localAxis = Vector3.Normalize(binding.LocalAxis);
            compiled[i] = new CompiledHumanoidAvatarAuxiliaryBone(
                i,
                binding.Kind,
                binding.ParentRole,
                node,
                binding.NeutralLocalTransform,
                neutralScale,
                neutralRotation,
                neutralTranslation,
                localAxis,
                binding.DistributionWeight,
                binding.StructuralSha256);
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryCompileTwistChains(
        HumanoidAvatarTwistChain[] source,
        CompiledHumanoidAvatarAuxiliaryBone[] auxiliaryBones,
        CompiledHumanoidBoneSolvePlan[] boneSolvePlans,
        Matrix4x4[] zeroMuscleModelRootTransforms,
        out CompiledHumanoidAvatarTwistChain[] compiled,
        out string diagnostic)
    {
        var auxiliaryByHash = new Dictionary<string, CompiledHumanoidAvatarAuxiliaryBone>(
            auxiliaryBones.Length,
            StringComparer.Ordinal);
        for (int i = 0; i < auxiliaryBones.Length; i++)
            auxiliaryByHash.TryAdd(auxiliaryBones[i].StructuralSha256, auxiliaryBones[i]);

        compiled = new CompiledHumanoidAvatarTwistChain[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            HumanoidAvatarTwistChain chain = source[i];
            string[] auxiliaryHashes = chain.AuxiliaryStructuralSha256 ?? [];
            var chainAuxiliaryBones = new CompiledHumanoidAvatarAuxiliaryBone[auxiliaryHashes.Length];
            for (int j = 0; j < auxiliaryHashes.Length; j++)
            {
                if (auxiliaryByHash.TryGetValue(auxiliaryHashes[j], out var auxiliary))
                {
                    chainAuxiliaryBones[j] = auxiliary;
                    continue;
                }

                compiled = [];
                diagnostic =
                    $"Twist chain '{chain.Name}' references unknown auxiliary bone '{auxiliaryHashes[j]}'.";
                return false;
            }

            if (!TryCompileTransportedTwistAxis(
                    chain.ProximalRole,
                    chain.DistalRole,
                    boneSolvePlans,
                    zeroMuscleModelRootTransforms,
                    out Vector3 proximalRemainderAxis)
                || !TryCompileTransportedTwistAxis(
                    chain.DistalRole,
                    chain.EndRole,
                    boneSolvePlans,
                    zeroMuscleModelRootTransforms,
                    out Vector3 distalRemainderAxis))
            {
                compiled = [];
                diagnostic = $"Twist chain '{chain.Name}' has a degenerate zero-pose segment axis.";
                return false;
            }

            compiled[i] = new CompiledHumanoidAvatarTwistChain(
                chain.Name,
                chain.ProximalRole,
                chain.DistalRole,
                chain.EndRole,
                chain.ProximalDistribution,
                chain.DistalDistribution,
                proximalRemainderAxis,
                distalRemainderAxis,
                chainAuxiliaryBones);
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryCompileTransportedTwistAxis(
        EHumanoidAvatarBoneRole sourceRole,
        EHumanoidAvatarBoneRole destinationRole,
        ReadOnlySpan<CompiledHumanoidBoneSolvePlan> plans,
        ReadOnlySpan<Matrix4x4> zeroMuscleModelRootTransforms,
        out Vector3 axisInDestinationParent)
    {
        axisInDestinationParent = Vector3.Zero;
        int sourceIndex = (int)sourceRole;
        int destinationIndex = (int)destinationRole;
        if ((uint)sourceIndex >= (uint)plans.Length
            || (uint)destinationIndex >= (uint)plans.Length
            || plans[sourceIndex].Node is null
            || plans[destinationIndex].Node is null
            || (uint)sourceIndex >= (uint)zeroMuscleModelRootTransforms.Length
            || (uint)destinationIndex >= (uint)zeroMuscleModelRootTransforms.Length)
            return false;

        Matrix4x4 source = zeroMuscleModelRootTransforms[sourceIndex];
        Matrix4x4 destination = zeroMuscleModelRootTransforms[destinationIndex];
        if (!Matrix4x4.Decompose(source, out _, out Quaternion sourceRotation, out _)
            || !Matrix4x4.Decompose(destination, out _, out Quaternion destinationRotation, out _)
            || !IsFiniteNonZero(sourceRotation)
            || !IsFiniteNonZero(destinationRotation))
            return false;

        Vector3 sourceAxisLocal = Vector3.Transform(
            Vector3.UnitY,
            plans[sourceIndex].JointBasisToZeroLocal);
        Vector3 modelRootAxis = Vector3.Transform(
            sourceAxisLocal,
            Quaternion.Normalize(sourceRotation));
        Vector3 axisInDestinationLocal = Vector3.Transform(
            modelRootAxis,
            Quaternion.Inverse(Quaternion.Normalize(destinationRotation)));
        axisInDestinationParent = Vector3.Transform(
            axisInDestinationLocal,
            plans[destinationIndex].ZeroMuscleRotation);
        float localLengthSquared = axisInDestinationParent.LengthSquared();
        if (!float.IsFinite(localLengthSquared) || localLengthSquared <= 1e-12f)
            return false;

        axisInDestinationParent /= MathF.Sqrt(localLengthSquared);
        return float.IsFinite(axisInDestinationParent.X)
            && float.IsFinite(axisInDestinationParent.Y)
            && float.IsFinite(axisInDestinationParent.Z);
    }

    private static HumanoidAvatarJointLimit CopyJointLimit(HumanoidAvatarJointLimit? source)
    {
        source ??= new HumanoidAvatarJointLimit();
        return new HumanoidAvatarJointLimit
        {
            UseDefaultValues = source.UseDefaultValues,
            CenterDegrees = source.CenterDegrees,
            MinimumDegrees = source.MinimumDegrees,
            MaximumDegrees = source.MaximumDegrees,
            AxisLength = source.AxisLength,
        };
    }

    private static Quaternion NormalizeFiniteQuaternion(Quaternion value)
        => IsFiniteNonZero(value) ? Quaternion.Normalize(value) : Quaternion.Identity;
}

using System.Numerics;
using XREngine.Animation.Importers;
using XREngine.Scene;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private CompiledHumanoidAvatarDefinition? _compiledAvatarDefinition;

    /// <summary>
    /// Invalidates derived role-indexed data. The next explicit definition
    /// validation rebuilds it; the frame loop never scans or remaps a skeleton.
    /// </summary>
    private void InvalidateCompiledAvatarDefinition()
        => _compiledAvatarDefinition = null;

    private bool TryGetCompiledAvatarDefinition(out CompiledHumanoidAvatarDefinition compiled)
    {
        compiled = _compiledAvatarDefinition!;
        return compiled is not null
            && compiled.SchemaVersion == AvatarDefinition.SchemaVersion
            && compiled.DefinitionRevision == AvatarDefinition.DefinitionRevision
            && string.Equals(
                compiled.DefinitionContentSha256,
                AvatarDefinition.DefinitionContentSha256,
                StringComparison.Ordinal);
    }

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
        var jointLimits = new HumanoidAvatarJointLimit[CompiledHumanoidAvatarDefinition.RoleCount];

        for (int i = 0; i < nodes.Length; i++)
        {
            neutralLocal[i] = Matrix4x4.Identity;
            neutralWorld[i] = Matrix4x4.Identity;
            canonicalCorrections[i] = Quaternion.Identity;
            preRotations[i] = Quaternion.Identity;
            postRotations[i] = Quaternion.Identity;
            rotationOrders[i] = EHumanoidAvatarRotationOrder.ZXY;
            axisMappings[i] = BoneAxisMapping.Default;
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
        }

        var muscleRanges = new Vector2[CompiledHumanoidAvatarDefinition.MuscleCount];
        for (int i = 0; i < definition.MuscleLimits.Length; i++)
        {
            HumanoidAvatarMuscleLimit limit = definition.MuscleLimits[i];
            int muscleIndex = (int)limit.Muscle;
            if ((uint)muscleIndex < (uint)muscleRanges.Length)
                muscleRanges[muscleIndex] = new Vector2(limit.NegativeDegrees, limit.PositiveDegrees);
        }

        var legacyCalibrations = new HumanoidAvatarLegacyBoneCalibration?[CompiledHumanoidAvatarDefinition.RoleCount];
        HumanoidAvatarLegacyCalibration? legacy = definition.LegacyCalibration;
        ImportedHumanoidRootMotionPolicy? legacyRootMotionPolicy = legacy?.CalibrationRootMotionSettings is { } legacySettings
            && ImportedHumanoidRootMotionPolicy.TryCreate(legacySettings, out ImportedHumanoidRootMotionPolicy compiledLegacyPolicy, out _)
                ? compiledLegacyPolicy
                : null;
        bool hasLegacyRootAllocationFrame = TryCompileLegacyRootAllocationFrame(
            legacy?.RootAllocationFrame,
            definition.ModelUnitsPerMeter,
            out Matrix4x4 legacyRootAllocationFrame,
            out Matrix4x4 inverseLegacyRootAllocationFrame);
        if (legacy is not null)
        {
            for (int i = 0; i < legacy.Bones.Length; i++)
            {
                HumanoidAvatarLegacyBoneCalibration calibration = legacy.Bones[i];
                int roleIndex = (int)calibration.Role;
                if ((uint)roleIndex < (uint)legacyCalibrations.Length)
                    legacyCalibrations[roleIndex] = calibration;
            }
        }

        if (!TryCompileAuxiliaryBones(
                definition.AuxiliaryBones,
                out CompiledHumanoidAvatarAuxiliaryBone[] auxiliaryBones,
                out diagnostic)
            || !TryCompileTwistChains(
                definition.TwistChains,
                auxiliaryBones,
                out CompiledHumanoidAvatarTwistChain[] twistChains,
                out diagnostic))
        {
            InvalidateCompiledAvatarDefinition();
            return false;
        }

        _compiledAvatarDefinition = new CompiledHumanoidAvatarDefinition(
            definition.SchemaVersion,
            definition.DefinitionRevision,
            definition.DefinitionContentSha256,
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
            jointLimits,
            muscleRanges,
            CopySolverSettings(definition.SolverSettings),
            CopyBodyAxes(definition.BodyAxes),
            definition.HumanScale,
            definition.ModelUnitsPerMeter,
            definition.MuscleInputScale,
            twistChains,
            auxiliaryBones,
            legacyCalibrations,
            legacy?.CalibrationClipName ?? string.Empty,
            legacyRootMotionPolicy,
            legacyRootAllocationFrame,
            inverseLegacyRootAllocationFrame,
            hasLegacyRootAllocationFrame);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryCompileLegacyRootAllocationFrame(
        ImportedHumanoidRootAllocationFrame? source,
        float modelUnitsPerMeter,
        out Matrix4x4 frame,
        out Matrix4x4 inverseFrame)
    {
        frame = Matrix4x4.Identity;
        inverseFrame = Matrix4x4.Identity;
        if (source is null)
            return false;

        Quaternion rotation = NormalizeFiniteQuaternion(source.HipsParentRotationInAnimatorRoot);
        frame = Matrix4x4.CreateScale(source.HipsParentScaleInAnimatorRoot)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(source.HipsParentPositionInAnimatorRoot * modelUnitsPerMeter);
        return Matrix4x4.Invert(frame, out inverseFrame);
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

            Vector3 localAxis = Vector3.Normalize(binding.LocalAxis);
            compiled[i] = new CompiledHumanoidAvatarAuxiliaryBone(
                binding.Kind,
                binding.ParentRole,
                node,
                binding.NeutralLocalTransform,
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

            compiled[i] = new CompiledHumanoidAvatarTwistChain(
                chain.Name,
                chain.ProximalRole,
                chain.DistalRole,
                chain.EndRole,
                chain.ProximalDistribution,
                chain.DistalDistribution,
                chainAuxiliaryBones);
        }

        diagnostic = string.Empty;
        return true;
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

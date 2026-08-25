using System.Numerics;
using XREngine.Scene;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private readonly List<string> _avatarMigrationDiagnostics = [];

    private void MigratePendingLegacyAvatarProfile()
    {
        if (Settings.UnityAvatarProfile is UnityHumanoidAvatarProfile profile)
            MigrateLegacyUnityAvatarProfile(profile, applyPreview: false);
    }

    /// <summary>
    /// Converts the former Unity-specific v3 profile into the generic component
    /// avatar definition. Transform names are consumed once as migration input;
    /// the resulting runtime definition is role/structure indexed.
    /// </summary>
    private void MigrateLegacyUnityAvatarProfile(
        UnityHumanoidAvatarProfile profile,
        bool applyPreview)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.BuildDenseLookups();
        _avatarMigrationDiagnostics.Clear();

        for (int roleIndex = 0; roleIndex < (int)EUnityHumanoidAvatarRole.Count; roleIndex++)
        {
            EUnityHumanoidAvatarRole legacyRole = (EUnityHumanoidAvatarRole)roleIndex;
            EHumanoidAvatarBoneRole role = (EHumanoidAvatarBoneRole)roleIndex;
            HumanoidAvatarBoneBinding? existing = FindBinding(AvatarDefinition.Bones, role);
            if (existing?.Locked == true && GetBoneDefinition(role).Node is not null)
                continue;
            if (!profile.TryGetRole(legacyRole, out UnityHumanoidAvatarRoleProfile roleProfile)
                || string.IsNullOrWhiteSpace(roleProfile.TransformName))
                continue;

            SceneNode? node = ResolveUniqueDescendantByName(roleProfile.TransformName, out int matchCount);
            if (node is null)
            {
                string severity = roleProfile.Required ? "Error" : "Review";
                _avatarMigrationDiagnostics.Add(
                    $"{severity}: migrated semantic role {role} expected one transform named " +
                    $"'{roleProfile.TransformName}', but found {matchCount}.");
                continue;
            }

            BoneDef bone = GetBoneDefinition(role);
            bone.Node = node;
            RefreshBoneBindPose(bone);
            _autoMappingEvidence[roleIndex] = new HumanoidAvatarRoleMappingEvidence
            {
                Source = EHumanoidAvatarMappingSource.ImportedSemanticMetadata,
                Confidence = 1.0f,
                ImportedMetadataScore = 1.0f,
                TopologyScore = 1.0f,
                GeometryScore = 1.0f,
                AxisScore = 1.0f,
                SymmetryScore = 1.0f,
                AliasScore = 0.0f,
                Summary = $"Migrated Unity v{profile.SchemaVersion} semantic role metadata.",
            };
        }

        float modelUnitsPerMeter = CalculateMigratedModelUnitsPerMeter(profile);
        _unityProfileUnitsPerMeter = modelUnitsPerMeter;

        Settings.ArmTwistDistribution = profile.AvatarSettings.UpperArmTwist;
        Settings.ForearmTwistDistribution = profile.AvatarSettings.LowerArmTwist;
        Settings.UpperLegTwistDistribution = profile.AvatarSettings.UpperLegTwist;
        Settings.LowerLegTwistDistribution = profile.AvatarSettings.LowerLegTwist;
        Settings.ProfileSource = "migrated-v3-profile";

        HumanoidAvatarDefinitionMetadata definition = AvatarDefinition;
        definition.SchemaVersion = HumanoidAvatarDefinitionMetadata.CurrentSchemaVersion;
        definition.AutoMappingAlgorithmVersion = HumanoidAvatarDefinitionMetadata.CurrentAutoMappingAlgorithmVersion;
        definition.Status = EHumanoidAvatarDefinitionStatus.NeedsReview;
        definition.Source = string.IsNullOrWhiteSpace(profile.Source)
            ? "LegacyCalibrationProfile"
            : profile.Source;
        definition.HumanScale = float.IsFinite(profile.HumanScale) && profile.HumanScale > 0.0f
            ? profile.HumanScale
            : 0.0f;
        definition.ModelUnitsPerMeter = modelUnitsPerMeter;
        definition.SolverSettings = CopySolverSettings(profile.AvatarSettings);
        definition.BodyAxes = CopyBodyAxes(profile.BodyAxes);
        definition.TwistChains = ConvertLegacyTwistChains(profile.TwistChains);
        definition.LegacyCalibration = ConvertLegacyCalibration(profile);

        // Clear the compatibility input before refreshing. From this point on,
        // only AvatarDefinition owns mapping, scale, axes, and fitted payloads.
        Settings.UnityAvatarProfile = null;
        RefreshAvatarDefinition();

        HumanoidAvatarLegacyBoneCalibration[] migratedBones =
            AvatarDefinition.LegacyCalibration?.Bones ?? [];
        for (int i = 0; i < migratedBones.Length; i++)
        {
            HumanoidAvatarLegacyBoneCalibration calibration = migratedBones[i];
            HumanoidAvatarBoneBinding? binding = FindBinding(AvatarDefinition.Bones, calibration.Role);
            if (binding is null)
                continue;
            if (calibration.HasNeutralRotation)
                binding.CanonicalPoseCorrection = NormalizeFiniteQuaternion(calibration.NeutralRotation);
            if (_autoMappingEvidence[(int)calibration.Role]?.Source
                == EHumanoidAvatarMappingSource.ImportedSemanticMetadata)
            {
                binding.MappingSource = EHumanoidAvatarMappingSource.ImportedSemanticMetadata;
                binding.Confidence = 1.0f;
                binding.ImportedMetadataScore = 1.0f;
                binding.MappingEvidence = $"Migrated Unity v{profile.SchemaVersion} semantic role metadata.";
            }
        }
        RehashDefinitionAfterEditorChange();

        if (applyPreview)
            ApplyPosePreviewMode();
    }

    private SceneNode? ResolveUniqueDescendantByName(string name, out int matchCount)
    {
        SceneNode? firstMatch = null;
        matchCount = CountDescendantsNamed(SceneNode, name, ref firstMatch);
        return matchCount == 1 ? firstMatch : null;
    }

    private float CalculateMigratedModelUnitsPerMeter(UnityHumanoidAvatarProfile profile)
    {
        Span<float> ratios = stackalloc float[(int)EUnityHumanoidAvatarRole.Count];
        int count = 0;
        for (int roleIndex = 0; roleIndex < (int)EUnityHumanoidAvatarRole.Count; roleIndex++)
        {
            EUnityHumanoidAvatarRole legacyRole = (EUnityHumanoidAvatarRole)roleIndex;
            if (!profile.TryGetNeutralPosition(legacyRole, out Vector3 unityPosition))
                continue;
            float unityLength = unityPosition.Length();
            SceneNode? node = GetBoneDefinition((EHumanoidAvatarBoneRole)roleIndex).Node;
            if (node is null
                || unityLength <= 1e-5f
                || !TryGetHumanoidBindLocalState(node, out _, out _, out Vector3 enginePosition))
                continue;
            float ratio = enginePosition.Length() / unityLength;
            if (float.IsFinite(ratio) && ratio > 1e-5f)
                ratios[count++] = ratio;
        }

        if (count == 0)
            return 1.0f;
        Span<float> populated = ratios[..count];
        populated.Sort();
        return count % 2 == 0
            ? 0.5f * (populated[count / 2 - 1] + populated[count / 2])
            : populated[count / 2];
    }

    private static HumanoidAvatarTwistChain[] ConvertLegacyTwistChains(
        List<UnityHumanoidTwistChainProfile>? legacyChains)
    {
        if (legacyChains is not { Count: > 0 })
            return [];
        var result = new HumanoidAvatarTwistChain[legacyChains.Count];
        for (int i = 0; i < result.Length; i++)
        {
            UnityHumanoidTwistChainProfile source = legacyChains[i];
            result[i] = new HumanoidAvatarTwistChain
            {
                Name = source.Name ?? string.Empty,
                ProximalRole = (EHumanoidAvatarBoneRole)(int)source.ProximalRole,
                DistalRole = (EHumanoidAvatarBoneRole)(int)source.DistalRole,
                EndRole = (EHumanoidAvatarBoneRole)(int)source.EndRole,
                ProximalDistribution = source.ProximalDistribution,
                DistalDistribution = source.DistalDistribution,
            };
        }
        return result;
    }

    private static HumanoidAvatarLegacyCalibration ConvertLegacyCalibration(
        UnityHumanoidAvatarProfile profile)
    {
        List<HumanoidAvatarLegacyBoneCalibration> bones = [];
        for (int roleIndex = 0; roleIndex < (int)EUnityHumanoidAvatarRole.Count; roleIndex++)
        {
            EUnityHumanoidAvatarRole legacyRole = (EUnityHumanoidAvatarRole)roleIndex;
            bool hasRotation = profile.TryGetNeutralRotation(legacyRole, out Quaternion neutralRotation);
            bool hasPosition = profile.TryGetNeutralPosition(legacyRole, out Vector3 neutralPosition);
            UnityHumanoidBoneResponseProfile? response = profile.TryGetBoneResponse(legacyRole, out var roleResponse)
                ? roleResponse
                : null;
            UnityHumanoidCoupledBoneModel? coupled = profile.TryGetCoupledBoneModel(legacyRole, out var roleCoupled)
                ? roleCoupled
                : null;
            if (!hasRotation && !hasPosition && response is null && coupled is null)
                continue;

            bones.Add(new HumanoidAvatarLegacyBoneCalibration
            {
                Role = (EHumanoidAvatarBoneRole)roleIndex,
                HasNeutralRotation = hasRotation,
                NeutralRotation = hasRotation ? neutralRotation : Quaternion.Identity,
                HasNeutralPosition = hasPosition,
                NeutralPosition = hasPosition ? neutralPosition : Vector3.Zero,
                BoneResponse = response,
                CoupledBoneModel = coupled,
            });
        }

        return new HumanoidAvatarLegacyCalibration
        {
            SourceSchemaVersion = profile.SchemaVersion,
            Source = profile.Source ?? string.Empty,
            AvatarName = profile.AvatarName ?? string.Empty,
            CalibrationClipName = profile.CalibrationClipName ?? string.Empty,
            Bones = [.. bones],
        };
    }
}

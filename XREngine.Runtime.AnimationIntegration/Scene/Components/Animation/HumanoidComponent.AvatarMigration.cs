using XREngine.Animation.Importers;
using XREngine.Scene;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private readonly List<string> _avatarMigrationDiagnostics = [];

    private void MigratePendingLegacyAvatarProfile()
    {
        if (Settings.ImportedAvatarProfile is ImportedHumanoidAvatarProfile profile)
            MigrateLegacyImportedAvatarProfile(profile, applyPreview: false);
    }

    /// <summary>
    /// Converts the former Unity-specific v3 profile into semantic avatar metadata.
    /// Captured neutral samples and fitted response coefficients are deliberately not
    /// migrated; canonical pose geometry is rebuilt from the target skeleton.
    /// </summary>
    private void MigrateLegacyImportedAvatarProfile(
        ImportedHumanoidAvatarProfile profile,
        bool applyPreview)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.BuildDenseLookups();
        _avatarMigrationDiagnostics.Clear();

        for (int roleIndex = 0; roleIndex < (int)EHumanoidAvatarRole.Count; roleIndex++)
        {
            EHumanoidAvatarRole legacyRole = (EHumanoidAvatarRole)roleIndex;
            EHumanoidAvatarBoneRole role = (EHumanoidAvatarBoneRole)roleIndex;
            HumanoidAvatarBoneBinding? existing = FindBinding(AvatarDefinition.Bones, role);
            if (existing?.Locked == true && GetBoneDefinition(role).Node is not null)
                continue;
            if (!profile.TryGetRole(legacyRole, out ImportedHumanoidAvatarRoleProfile roleProfile)
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

        float modelUnitsPerMeter = float.IsFinite(AvatarDefinition.ModelUnitsPerMeter)
            && AvatarDefinition.ModelUnitsPerMeter > 1.0e-5f
                ? AvatarDefinition.ModelUnitsPerMeter
                : 1.0f;
        _sourceProfileUnitsPerMeter = modelUnitsPerMeter;

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
            ? "migrated-v3-profile"
            : profile.Source;
        definition.HumanScale = float.IsFinite(profile.HumanScale) && profile.HumanScale > 0.0f
            ? profile.HumanScale
            : 0.0f;
        definition.ModelUnitsPerMeter = modelUnitsPerMeter;
        definition.SolverSettings = CopySolverSettings(profile.AvatarSettings);
        definition.BodyAxes = CopyBodyAxes(profile.BodyAxes);
        definition.TwistChains = ConvertLegacyTwistChains(profile.TwistChains);
        definition.LegacyCalibration = null;

        // The importer can attach the legacy profile after the component's initial
        // geometry profile has already run. Profile the completed semantic mapping
        // here so roles introduced by migration receive validated local axes before
        // the generic avatar definition is compiled.
        AvatarHumanoidProfileBuilder.ProfileResult profileResult =
            AvatarHumanoidProfileBuilder.BuildProfile(this);

        // Clear the compatibility input before refreshing. From this point on,
        // only AvatarDefinition owns semantic mapping, scale, axes, and twist metadata.
        Settings.ImportedAvatarProfile = null;
        RefreshAvatarDefinition(profileResult);

        for (int roleIndex = 0; roleIndex < (int)EHumanoidAvatarRole.Count; roleIndex++)
        {
            EHumanoidAvatarBoneRole role = (EHumanoidAvatarBoneRole)roleIndex;
            HumanoidAvatarBoneBinding? binding = FindBinding(AvatarDefinition.Bones, role);
            if (binding is null
                || _autoMappingEvidence[roleIndex]?.Source
                    != EHumanoidAvatarMappingSource.ImportedSemanticMetadata)
                continue;

            binding.MappingSource = EHumanoidAvatarMappingSource.ImportedSemanticMetadata;
            binding.Confidence = 1.0f;
            binding.ImportedMetadataScore = 1.0f;
            binding.MappingEvidence = $"Migrated Unity v{profile.SchemaVersion} semantic role metadata.";
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

    private static HumanoidAvatarTwistChain[] ConvertLegacyTwistChains(
        List<ImportedHumanoidTwistChainProfile>? legacyChains)
    {
        if (legacyChains is not { Count: > 0 })
            return [];
        var result = new HumanoidAvatarTwistChain[legacyChains.Count];
        for (int i = 0; i < result.Length; i++)
        {
            ImportedHumanoidTwistChainProfile source = legacyChains[i];
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

}

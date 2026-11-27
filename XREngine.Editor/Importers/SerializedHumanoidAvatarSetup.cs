using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Adds and confirms a <see cref="HumanoidComponent"/> on an imported Unity avatar. The import creates the bone nodes
/// but no humanoid, so an animation clip with humanoid muscle data has nothing to drive until this setup runs. The cook
/// command and the editor MCP tools use the same steps, so a cooked avatar and an avatar in an edited scene get the
/// same bone mapping.
/// </summary>
public static class SerializedHumanoidAvatarSetup
{
    /// <summary>
    /// Returns the node that gets the humanoid: the node of the first <see cref="HumanoidAuthoredTPoseComponent"/> under
    /// <paramref name="rootNode"/>, or <paramref name="rootNode"/> when the import found no authored T-pose. The importer
    /// adds that component to the model root of the avatar, which is the node that the Unity animator drives.
    /// </summary>
    public static SceneNode FindHumanoidTargetNode(SceneNode rootNode)
    {
        SceneNode? matched = null;
        rootNode.IterateComponents<HumanoidAuthoredTPoseComponent>(component =>
        {
            if (matched is null && component.SceneNode is not null)
                matched = component.SceneNode;
        }, iterateChildHierarchy: true);
        return matched ?? rootNode;
    }

    /// <summary>
    /// Returns the first FBX file that a Unity prefab import read, and its SHA-256 from the import manifest. The manifest
    /// lists each source file of the import. Returns <c>(null, "")</c> when the manifest is null or lists no FBX file.
    /// </summary>
    public static (string? FbxPath, string Sha256) FindPrimaryModelDependency(SerializedPrefabImportManifest? manifest)
    {
        if (manifest is null)
            return (null, string.Empty);

        foreach (SourceImportDependencyManifestEntry dependency in manifest.Dependencies)
        {
            if (!dependency.NormalizedPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                continue;

            string? resolved = manifest.ResolveDependencySourcePath(dependency.NormalizedPath);
            string sha256 = !string.IsNullOrWhiteSpace(dependency.Sha256)
                ? dependency.Sha256
                : resolved is not null && File.Exists(resolved)
                    ? ComputeFileSha256(resolved)
                    : string.Empty;
            return (resolved, sha256);
        }

        return (null, string.Empty);
    }

    /// <summary>
    /// Returns the path of the first FBX model file that a model component under <paramref name="rootNode"/> was
    /// imported from, or null when no model records an FBX source. The Unity import settings of that file
    /// (<c>.fbx.meta</c>) hold the humanoid bone names.
    /// </summary>
    public static string? FindSourceModelPath(SceneNode rootNode)
    {
        string? found = null;
        rootNode.IterateComponents<ModelComponent>(component =>
        {
            if (found is not null)
                return;

            string? path = component.Model?.OriginalPath ?? component.Model?.FilePath;
            if (!string.IsNullOrWhiteSpace(path)
                && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
                && File.Exists(path))
            {
                found = path;
            }
        }, iterateChildHierarchy: true);
        return found;
    }

    /// <summary>
    /// Adds a <see cref="HumanoidComponent"/> to <paramref name="avatarRoot"/> (or uses the existing one), maps its bones,
    /// and confirms its avatar definition.
    /// </summary>
    /// <param name="avatarRoot">The node that gets the humanoid. Use <see cref="FindHumanoidTargetNode"/> to find it.</param>
    /// <param name="sourceModelPath">
    /// Optional FBX file of the avatar. When its <c>.fbx.meta</c> file has a Unity humanoid bone list, those names set the
    /// bone of each role and lock it against automatic correction. Without it, the humanoid maps the bones by name.
    /// </param>
    /// <param name="sourceModelSha256">
    /// Optional SHA-256 of the FBX file. When it is empty and <paramref name="sourceModelPath"/> exists, the method
    /// computes it. The humanoid stores it to detect a changed source model.
    /// </param>
    /// <param name="mappedRoleCount">The number of roles that the Unity bone list set.</param>
    /// <exception cref="InvalidOperationException">The humanoid cannot be added, or its avatar definition fails the checks.</exception>
    public static HumanoidComponent Configure(
        SceneNode avatarRoot,
        string? sourceModelPath,
        string? sourceModelSha256,
        out int mappedRoleCount)
    {
        (Dictionary<EHumanoidAvatarBoneRole, SceneNode?> explicitMappings,
            Dictionary<EHumanoidAvatarBoneRole, MetaHumanLimit> customLimits) = !string.IsNullOrWhiteSpace(sourceModelPath)
            ? ReadMetaHumanDescription(avatarRoot, sourceModelPath + ".meta")
            : ([], []);
        mappedRoleCount = explicitMappings.Count;

        // The humanoid captures each bone bind pose into its avatar definition, and a cooked pak load writes those
        // neutral poses back into the bones. Some import paths leave prefab bones with identity bind matrices, which
        // would store zero translations for bones such as Hips. Save the bind matrices from the authored rest pose
        // first, so the definition and the validation both read the real skeleton.
        avatarRoot.Transform
            .RecalculateMatrixHierarchy(forceWorldRecalc: true, setRenderMatrixNow: true, childRecalcType: ELoopType.Sequential)
            .GetAwaiter()
            .GetResult();
        SaveBindStateRecursive(avatarRoot.Transform);

        HumanoidComponent humanoid;
        using (HumanoidComponent.BeginDeferredSceneNodeInitialization())
        {
            humanoid = avatarRoot.GetComponent<HumanoidComponent>()
                ?? avatarRoot.AddComponent<HumanoidComponent>("Humanoid")
                ?? throw new InvalidOperationException($"Failed to add HumanoidComponent to '{avatarRoot.Name}'.");
        }

        string sha256 = sourceModelSha256 ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sha256) && !string.IsNullOrWhiteSpace(sourceModelPath) && File.Exists(sourceModelPath))
            sha256 = ComputeFileSha256(sourceModelPath);
        if (!string.IsNullOrWhiteSpace(sha256))
            humanoid.SetSourceModelContentSha256(sha256);

        humanoid.InitializeSceneNodeBindings();
        if (explicitMappings.Count > 0)
            humanoid.SetAvatarBoneMappings(explicitMappings, lockEditorCorrections: true);
        if (customLimits.Count > 0)
            ApplyMetaMuscleLimits(humanoid, customLimits);

        if (!humanoid.ConfirmAvatarDefinition(out string confirmDiagnostic))
            throw new InvalidOperationException(
                $"Humanoid avatar definition confirmation failed: {confirmDiagnostic} " +
                $"Mapped bind positions: {DescribeSymmetryRoles(humanoid)}");

        // An animation clip writes the animated pose only when the humanoid previews it.
        humanoid.PosePreviewMode = EHumanoidPosePreviewMode.AnimatedPose;
        return humanoid;
    }

    /// <summary>Computes the SHA-256 of a file as an uppercase hexadecimal string.</summary>
    public static string ComputeFileSha256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void SaveBindStateRecursive(TransformBase transform)
    {
        transform.SaveBindState();
        foreach (TransformBase child in transform.Children)
            SaveBindStateRecursive(child);
    }

    /// <summary>
    /// Lists the node and bind-pose translation of each role that the humanoid symmetry check uses, so a failed
    /// confirmation shows which source bone the mapper chose for each side.
    /// </summary>
    public static string DescribeSymmetryRoles(HumanoidComponent humanoid)
    {
        EHumanoidAvatarBoneRole[] roles =
        [
            EHumanoidAvatarBoneRole.Hips,
            EHumanoidAvatarBoneRole.LeftUpperArm, EHumanoidAvatarBoneRole.RightUpperArm,
            EHumanoidAvatarBoneRole.LeftUpperLeg, EHumanoidAvatarBoneRole.RightUpperLeg,
            EHumanoidAvatarBoneRole.LeftHand, EHumanoidAvatarBoneRole.RightHand,
        ];
        List<string> parts = new(roles.Length + 1);
        HumanoidAvatarBodyAxes? axes = humanoid.AvatarDefinition?.BodyAxes;
        parts.Add(axes is null
            ? "bodyRight=<unavailable>"
            : string.Create(CultureInfo.InvariantCulture, $"bodyRight={axes.Right}"));
        foreach (EHumanoidAvatarBoneRole role in roles)
        {
            SceneNode? node = humanoid.GetAvatarBoneNode(role);
            parts.Add(node is null
                ? $"{role}=<none>"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{role}='{node.Name}' type={node.Transform.GetType().Name} bind={node.Transform.BindMatrix.Translation} local={node.Transform.LocalMatrix.Translation} world={node.Transform.WorldMatrix.Translation} parent='{node.Parent?.Name}'"));
        }

        return string.Join("; ", parts);
    }

    private readonly record struct MetaHumanLimit(Vector3 MinDegrees, Vector3 MaxDegrees);

    /// <summary>
    /// Reads modified Unity humanoid bone rotation limits from the avatar's <c>.fbx.meta</c> file and applies them
    /// to <see cref="HumanoidSettings.MuscleRotationDegRanges"/>. Call before confirming or initializing the avatar
    /// definition so the compiled definition captures the imported ranges. Returns the number of bones whose modified
    /// limits were applied.
    /// </summary>
    public static int ApplyMetaMuscleLimits(HumanoidComponent humanoid, string? sourceModelPath = null)
    {
        ArgumentNullException.ThrowIfNull(humanoid);

        string? resolvedPath = !string.IsNullOrWhiteSpace(sourceModelPath)
            ? sourceModelPath
            : humanoid.SceneNode is SceneNode root
                ? FindSourceModelPath(root)
                : null;
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return 0;

        Dictionary<EHumanoidAvatarBoneRole, MetaHumanLimit> customLimits = ReadMetaCustomLimits(resolvedPath + ".meta");
        if (customLimits.Count == 0)
            return 0;

        ApplyMetaMuscleLimits(humanoid, customLimits);
        return customLimits.Count;
    }

    private static Dictionary<EHumanoidAvatarBoneRole, MetaHumanLimit> ReadMetaCustomLimits(string metaPath)
    {
        Dictionary<EHumanoidAvatarBoneRole, MetaHumanLimit> customLimits = [];
        if (TryLoadHumanSequence(metaPath) is not YamlSequenceNode humanSequence)
            return customLimits;

        foreach (YamlNode item in humanSequence.Children)
        {
            if (item is not YamlMappingNode entry)
                continue;

            string? humanName = (TryGetYamlChild(entry, "humanName") as YamlScalarNode)?.Value;
            if (string.IsNullOrWhiteSpace(humanName))
                continue;

            string normalizedRoleName = humanName.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!Enum.TryParse(normalizedRoleName, ignoreCase: true, out EHumanoidAvatarBoneRole role))
                continue;

            if (TryReadModifiedLimit(entry, out MetaHumanLimit limit))
                customLimits[role] = limit;
        }

        return customLimits;
    }

    /// <summary>
    /// Reads the Unity humanoid bone list (<c>ModelImporter.humanDescription.human</c>) from a model import settings
    /// file and returns the node mappings and any modified per-bone rotation limits in degrees.
    /// </summary>
    private static (Dictionary<EHumanoidAvatarBoneRole, SceneNode?> Mappings, Dictionary<EHumanoidAvatarBoneRole, MetaHumanLimit> CustomLimits)
        ReadMetaHumanDescription(SceneNode avatarRoot, string metaPath)
    {
        Dictionary<EHumanoidAvatarBoneRole, SceneNode?> mappings = [];
        Dictionary<EHumanoidAvatarBoneRole, MetaHumanLimit> customLimits = [];
        if (TryLoadHumanSequence(metaPath) is not YamlSequenceNode humanSequence)
            return (mappings, customLimits);

        Dictionary<string, SceneNode> nodesByName = new(StringComparer.Ordinal);
        avatarRoot.IterateHierarchy(node =>
        {
            if (!string.IsNullOrWhiteSpace(node.Name))
                nodesByName.TryAdd(node.Name, node);
        });

        foreach (YamlNode item in humanSequence.Children)
        {
            if (item is not YamlMappingNode entry)
                continue;

            string? boneName = (TryGetYamlChild(entry, "boneName") as YamlScalarNode)?.Value;
            string? humanName = (TryGetYamlChild(entry, "humanName") as YamlScalarNode)?.Value;
            if (string.IsNullOrWhiteSpace(boneName) || string.IsNullOrWhiteSpace(humanName))
                continue;

            string normalizedRoleName = humanName.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!Enum.TryParse(normalizedRoleName, ignoreCase: true, out EHumanoidAvatarBoneRole role))
                continue;

            if (nodesByName.TryGetValue(boneName, out SceneNode? targetNode))
                mappings[role] = targetNode;

            if (TryReadModifiedLimit(entry, out MetaHumanLimit limit))
                customLimits[role] = limit;
        }

        return (mappings, customLimits);
    }

    private static YamlSequenceNode? TryLoadHumanSequence(string metaPath)
    {
        if (!File.Exists(metaPath))
            return null;

        YamlStream yaml = new();
        // Unity .meta files can contain unquoted '#' characters inside names such as material labels.
        string sanitizedYaml = SerializedYamlPlainScalars.QuoteValuesWithHash(File.ReadAllText(metaPath));
        using (StringReader reader = new(sanitizedYaml))
            yaml.Load(reader);

        if (yaml.Documents.Count == 0
            || yaml.Documents[0].RootNode is not YamlMappingNode root
            || TryGetYamlChild(root, "ModelImporter") is not YamlMappingNode importer
            || TryGetYamlChild(importer, "humanDescription") is not YamlMappingNode humanDescription
            || TryGetYamlChild(humanDescription, "human") is not YamlSequenceNode humanSequence)
        {
            return null;
        }

        return humanSequence;
    }

    /// <summary>
    /// Applies modified Unity <c>HumanLimit</c> degree bounds onto <see cref="HumanoidSettings.MuscleRotationDegRanges"/>
    /// before avatar confirmation compiles and persists those limits into the avatar definition.
    /// </summary>
    private static void ApplyMetaMuscleLimits(
        HumanoidComponent humanoid,
        Dictionary<EHumanoidAvatarBoneRole, MetaHumanLimit> customLimits)
    {
        foreach ((EHumanoidAvatarBoneRole role, MetaHumanLimit limit) in customLimits)
        {
            (EHumanoidValue? twist, EHumanoidValue? swing1, EHumanoidValue? swing2) = GetUnityHumanLimitMuscles(role);
            ApplyAxisMuscleLimit(humanoid.Settings, twist, limit.MinDegrees.X, limit.MaxDegrees.X);
            ApplyAxisMuscleLimit(humanoid.Settings, swing1, limit.MinDegrees.Y, limit.MaxDegrees.Y);
            ApplyAxisMuscleLimit(humanoid.Settings, swing2, limit.MinDegrees.Z, limit.MaxDegrees.Z);
        }
    }

    private static void ApplyAxisMuscleLimit(
        HumanoidSettings settings,
        EHumanoidValue? muscle,
        float minDegrees,
        float maxDegrees)
    {
        if (!muscle.HasValue || !float.IsFinite(minDegrees) || !float.IsFinite(maxDegrees) || minDegrees > maxDegrees)
            return;

        // Preserve each channel's existing sign orientation in HumanoidSettings. Some limb channels store an inverted
        // (positive, negative) range so normalized muscle inputs rotate in the engine's canonical direction.
        Vector2 defaultRange = settings.GetResolvedMuscleRotationDegRange(muscle.Value);
        bool invertedSign = defaultRange.X > 0.0f && defaultRange.Y < 0.0f;
        Vector2 resolvedRange = invertedSign
            ? new Vector2(-minDegrees, -maxDegrees)
            : new Vector2(minDegrees, maxDegrees);
        settings.SetMuscleRotationDegRange(muscle.Value, resolvedRange);

        // Keep the explicit eye-range inspector properties aligned with the muscle override map.
        switch (muscle.Value)
        {
            case EHumanoidValue.LeftEyeDownUp:
                settings.LeftEyeDownUpRange = resolvedRange;
                break;
            case EHumanoidValue.LeftEyeInOut:
                settings.LeftEyeInOutRange = resolvedRange;
                break;
            case EHumanoidValue.RightEyeDownUp:
                settings.RightEyeDownUpRange = resolvedRange;
                break;
            case EHumanoidValue.RightEyeInOut:
                settings.RightEyeInOutRange = resolvedRange;
                break;
        }
    }

    /// <summary>
    /// Maps a Unity humanoid bone role's <c>HumanLimit</c> axes (<c>x</c> twist, <c>y</c> swing1, <c>z</c> swing2) to
    /// the corresponding <see cref="EHumanoidValue"/> muscle channels.
    /// </summary>
    private static (EHumanoidValue? Twist, EHumanoidValue? Swing1, EHumanoidValue? Swing2) GetUnityHumanLimitMuscles(
        EHumanoidAvatarBoneRole role)
        => role switch
        {
            EHumanoidAvatarBoneRole.LeftEye => (null, EHumanoidValue.LeftEyeDownUp, EHumanoidValue.LeftEyeInOut),
            EHumanoidAvatarBoneRole.RightEye => (null, EHumanoidValue.RightEyeDownUp, EHumanoidValue.RightEyeInOut),
            EHumanoidAvatarBoneRole.Jaw => (null, EHumanoidValue.JawClose, EHumanoidValue.JawLeftRight),
            EHumanoidAvatarBoneRole.Spine => (EHumanoidValue.SpineTwistLeftRight, EHumanoidValue.SpineLeftRight, EHumanoidValue.SpineFrontBack),
            EHumanoidAvatarBoneRole.Chest => (EHumanoidValue.ChestTwistLeftRight, EHumanoidValue.ChestLeftRight, EHumanoidValue.ChestFrontBack),
            EHumanoidAvatarBoneRole.UpperChest => (EHumanoidValue.UpperChestTwistLeftRight, EHumanoidValue.UpperChestLeftRight, EHumanoidValue.UpperChestFrontBack),
            EHumanoidAvatarBoneRole.Neck => (EHumanoidValue.NeckTurnLeftRight, EHumanoidValue.NeckTiltLeftRight, EHumanoidValue.NeckNodDownUp),
            EHumanoidAvatarBoneRole.Head => (EHumanoidValue.HeadTurnLeftRight, EHumanoidValue.HeadTiltLeftRight, EHumanoidValue.HeadNodDownUp),
            EHumanoidAvatarBoneRole.LeftShoulder => (null, EHumanoidValue.LeftShoulderFrontBack, EHumanoidValue.LeftShoulderDownUp),
            EHumanoidAvatarBoneRole.RightShoulder => (null, EHumanoidValue.RightShoulderFrontBack, EHumanoidValue.RightShoulderDownUp),
            EHumanoidAvatarBoneRole.LeftUpperArm => (EHumanoidValue.LeftArmTwistInOut, EHumanoidValue.LeftArmFrontBack, EHumanoidValue.LeftArmDownUp),
            EHumanoidAvatarBoneRole.RightUpperArm => (EHumanoidValue.RightArmTwistInOut, EHumanoidValue.RightArmFrontBack, EHumanoidValue.RightArmDownUp),
            EHumanoidAvatarBoneRole.LeftLowerArm => (EHumanoidValue.LeftForearmTwistInOut, null, EHumanoidValue.LeftForearmStretch),
            EHumanoidAvatarBoneRole.RightLowerArm => (EHumanoidValue.RightForearmTwistInOut, null, EHumanoidValue.RightForearmStretch),
            EHumanoidAvatarBoneRole.LeftHand => (null, EHumanoidValue.LeftHandInOut, EHumanoidValue.LeftHandDownUp),
            EHumanoidAvatarBoneRole.RightHand => (null, EHumanoidValue.RightHandInOut, EHumanoidValue.RightHandDownUp),
            EHumanoidAvatarBoneRole.LeftUpperLeg => (EHumanoidValue.LeftUpperLegTwistInOut, EHumanoidValue.LeftUpperLegInOut, EHumanoidValue.LeftUpperLegFrontBack),
            EHumanoidAvatarBoneRole.RightUpperLeg => (EHumanoidValue.RightUpperLegTwistInOut, EHumanoidValue.RightUpperLegInOut, EHumanoidValue.RightUpperLegFrontBack),
            EHumanoidAvatarBoneRole.LeftLowerLeg => (EHumanoidValue.LeftLowerLegTwistInOut, null, EHumanoidValue.LeftLowerLegStretch),
            EHumanoidAvatarBoneRole.RightLowerLeg => (EHumanoidValue.RightLowerLegTwistInOut, null, EHumanoidValue.RightLowerLegStretch),
            EHumanoidAvatarBoneRole.LeftFoot => (EHumanoidValue.LeftFootTwistInOut, null, EHumanoidValue.LeftFootUpDown),
            EHumanoidAvatarBoneRole.RightFoot => (EHumanoidValue.RightFootTwistInOut, null, EHumanoidValue.RightFootUpDown),
            EHumanoidAvatarBoneRole.LeftToes => (null, null, EHumanoidValue.LeftToesUpDown),
            EHumanoidAvatarBoneRole.RightToes => (null, null, EHumanoidValue.RightToesUpDown),
            EHumanoidAvatarBoneRole.LeftThumbProximal => (null, EHumanoidValue.LeftHandThumbSpread, EHumanoidValue.LeftHandThumb1Stretched),
            EHumanoidAvatarBoneRole.LeftThumbIntermediate => (null, null, EHumanoidValue.LeftHandThumb2Stretched),
            EHumanoidAvatarBoneRole.LeftThumbDistal => (null, null, EHumanoidValue.LeftHandThumb3Stretched),
            EHumanoidAvatarBoneRole.LeftIndexProximal => (null, EHumanoidValue.LeftHandIndexSpread, EHumanoidValue.LeftHandIndex1Stretched),
            EHumanoidAvatarBoneRole.LeftIndexIntermediate => (null, null, EHumanoidValue.LeftHandIndex2Stretched),
            EHumanoidAvatarBoneRole.LeftIndexDistal => (null, null, EHumanoidValue.LeftHandIndex3Stretched),
            EHumanoidAvatarBoneRole.LeftMiddleProximal => (null, EHumanoidValue.LeftHandMiddleSpread, EHumanoidValue.LeftHandMiddle1Stretched),
            EHumanoidAvatarBoneRole.LeftMiddleIntermediate => (null, null, EHumanoidValue.LeftHandMiddle2Stretched),
            EHumanoidAvatarBoneRole.LeftMiddleDistal => (null, null, EHumanoidValue.LeftHandMiddle3Stretched),
            EHumanoidAvatarBoneRole.LeftRingProximal => (null, EHumanoidValue.LeftHandRingSpread, EHumanoidValue.LeftHandRing1Stretched),
            EHumanoidAvatarBoneRole.LeftRingIntermediate => (null, null, EHumanoidValue.LeftHandRing2Stretched),
            EHumanoidAvatarBoneRole.LeftRingDistal => (null, null, EHumanoidValue.LeftHandRing3Stretched),
            EHumanoidAvatarBoneRole.LeftLittleProximal => (null, EHumanoidValue.LeftHandLittleSpread, EHumanoidValue.LeftHandLittle1Stretched),
            EHumanoidAvatarBoneRole.LeftLittleIntermediate => (null, null, EHumanoidValue.LeftHandLittle2Stretched),
            EHumanoidAvatarBoneRole.LeftLittleDistal => (null, null, EHumanoidValue.LeftHandLittle3Stretched),
            EHumanoidAvatarBoneRole.RightThumbProximal => (null, EHumanoidValue.RightHandThumbSpread, EHumanoidValue.RightHandThumb1Stretched),
            EHumanoidAvatarBoneRole.RightThumbIntermediate => (null, null, EHumanoidValue.RightHandThumb2Stretched),
            EHumanoidAvatarBoneRole.RightThumbDistal => (null, null, EHumanoidValue.RightHandThumb3Stretched),
            EHumanoidAvatarBoneRole.RightIndexProximal => (null, EHumanoidValue.RightHandIndexSpread, EHumanoidValue.RightHandIndex1Stretched),
            EHumanoidAvatarBoneRole.RightIndexIntermediate => (null, null, EHumanoidValue.RightHandIndex2Stretched),
            EHumanoidAvatarBoneRole.RightIndexDistal => (null, null, EHumanoidValue.RightHandIndex3Stretched),
            EHumanoidAvatarBoneRole.RightMiddleProximal => (null, EHumanoidValue.RightHandMiddleSpread, EHumanoidValue.RightHandMiddle1Stretched),
            EHumanoidAvatarBoneRole.RightMiddleIntermediate => (null, null, EHumanoidValue.RightHandMiddle2Stretched),
            EHumanoidAvatarBoneRole.RightMiddleDistal => (null, null, EHumanoidValue.RightHandMiddle3Stretched),
            EHumanoidAvatarBoneRole.RightRingProximal => (null, EHumanoidValue.RightHandRingSpread, EHumanoidValue.RightHandRing1Stretched),
            EHumanoidAvatarBoneRole.RightRingIntermediate => (null, null, EHumanoidValue.RightHandRing2Stretched),
            EHumanoidAvatarBoneRole.RightRingDistal => (null, null, EHumanoidValue.RightHandRing3Stretched),
            EHumanoidAvatarBoneRole.RightLittleProximal => (null, EHumanoidValue.RightHandLittleSpread, EHumanoidValue.RightHandLittle1Stretched),
            EHumanoidAvatarBoneRole.RightLittleIntermediate => (null, null, EHumanoidValue.RightHandLittle2Stretched),
            EHumanoidAvatarBoneRole.RightLittleDistal => (null, null, EHumanoidValue.RightHandLittle3Stretched),
            _ => (null, null, null),
        };

    private static bool TryReadModifiedLimit(YamlMappingNode boneEntry, out MetaHumanLimit limit)
    {
        limit = default;
        if (TryGetYamlChild(boneEntry, "limit") is not YamlMappingNode limitMap)
            return false;

        string? modifiedText = (TryGetYamlChild(limitMap, "modified") as YamlScalarNode)?.Value;
        if (!int.TryParse(modifiedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int modified)
            || modified == 0)
            return false;

        if (!TryReadYamlVector3(limitMap, "min", out Vector3 minDegrees)
            || !TryReadYamlVector3(limitMap, "max", out Vector3 maxDegrees))
            return false;

        if (!float.IsFinite(minDegrees.X) || !float.IsFinite(minDegrees.Y) || !float.IsFinite(minDegrees.Z)
            || !float.IsFinite(maxDegrees.X) || !float.IsFinite(maxDegrees.Y) || !float.IsFinite(maxDegrees.Z)
            || minDegrees.X > maxDegrees.X
            || minDegrees.Y > maxDegrees.Y
            || minDegrees.Z > maxDegrees.Z)
            return false;

        limit = new MetaHumanLimit(minDegrees, maxDegrees);
        return true;
    }

    private static bool TryReadYamlVector3(YamlMappingNode parent, string key, out Vector3 value)
    {
        value = Vector3.Zero;
        if (TryGetYamlChild(parent, key) is not YamlMappingNode vectorMap)
            return false;

        if (!TryReadYamlFloat(vectorMap, "x", out float x)
            || !TryReadYamlFloat(vectorMap, "y", out float y)
            || !TryReadYamlFloat(vectorMap, "z", out float z))
            return false;

        value = new Vector3(x, y, z);
        return true;
    }

    private static bool TryReadYamlFloat(YamlMappingNode mapping, string key, out float value)
    {
        value = 0.0f;
        string? text = (TryGetYamlChild(mapping, key) as YamlScalarNode)?.Value;
        return !string.IsNullOrWhiteSpace(text)
            && float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    private static YamlNode? TryGetYamlChild(YamlMappingNode mapping, string key)
    {
        foreach ((YamlNode yamlKey, YamlNode value) in mapping.Children)
        {
            if (string.Equals((yamlKey as YamlScalarNode)?.Value, key, StringComparison.Ordinal))
                return value;
        }

        return null;
    }
}

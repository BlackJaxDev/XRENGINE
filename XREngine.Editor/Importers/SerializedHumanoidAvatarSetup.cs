using System.Globalization;
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
        Dictionary<EHumanoidAvatarBoneRole, SceneNode?> explicitMappings = !string.IsNullOrWhiteSpace(sourceModelPath)
            ? ReadMetaBoneMappings(avatarRoot, sourceModelPath + ".meta")
            : [];
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

    /// <summary>
    /// Reads the Unity humanoid bone list (<c>ModelImporter.humanDescription.human</c>) of a model import settings file
    /// and returns the node of each role whose bone name exists under <paramref name="avatarRoot"/>.
    /// </summary>
    private static Dictionary<EHumanoidAvatarBoneRole, SceneNode?> ReadMetaBoneMappings(SceneNode avatarRoot, string metaPath)
    {
        Dictionary<EHumanoidAvatarBoneRole, SceneNode?> mappings = [];
        if (!File.Exists(metaPath))
            return mappings;

        YamlStream yaml = new();
        using (StreamReader reader = new(metaPath))
            yaml.Load(reader);

        if (yaml.Documents.Count == 0
            || yaml.Documents[0].RootNode is not YamlMappingNode root
            || TryGetYamlChild(root, "ModelImporter") is not YamlMappingNode importer
            || TryGetYamlChild(importer, "humanDescription") is not YamlMappingNode humanDescription
            || TryGetYamlChild(humanDescription, "human") is not YamlSequenceNode humanSequence)
        {
            return mappings;
        }

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
        }

        return mappings;
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

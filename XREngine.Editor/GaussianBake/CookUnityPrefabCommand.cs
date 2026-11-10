using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Imaging;
using XREngine.Rendering;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Prefabs;
using YamlDotNet.RepresentationModel;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Imports a Unity <c>.prefab</c> file into an <see cref="XRPrefabSource"/> with
/// <see cref="SerializedSceneImporter.ImportPrefabWithManifest(string, string?, string?, XREngine.Rendering.Models.FbxImportBackend)"/>,
/// hydrates active material textures, optionally binds and confirms a Unity humanoid <c>.anim</c> clip, and cooks the
/// prefab into a <c>.pak</c> archive for <see cref="BakeSplatPreviewCommand"/>.
/// </summary>
public static class CookUnityPrefabCommand
{
    /// <summary>The command-line flag that starts a Unity prefab cook.</summary>
    public const string CommandFlag = "--cook-unity-prefab";

    private const int DefaultMaxTextureSize = 2048;

    /// <summary>
    /// Runs the command when <paramref name="args"/> contains <see cref="CommandFlag"/>, then exits with the
    /// command status code. Returns false for all other arguments.
    /// </summary>
    public static bool TryRun(string[] args)
    {
        if (!ContainsCommandFlag(args))
            return false;

        int exitCode = Run(args);
        Environment.Exit(exitCode);
        return true;
    }

    private static bool ContainsCommandFlag(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, CommandFlag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int Run(string[] args)
    {
        if (!TryParseArgs(
                args,
                out string? unityProjectPath,
                out string? prefabPath,
                out string? outputPakPath,
                out string assetPath,
                out uint maxTextureSize,
                out string? animFilePath,
                out string? error))
        {
            Console.Error.WriteLine($"Unity prefab cook failed: {error}");
            return 2;
        }

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string resolvedProjectPath = Path.GetFullPath(unityProjectPath!);
            string resolvedPrefabPath = Path.GetFullPath(prefabPath!);
            string resolvedOutputPakPath = Path.GetFullPath(outputPakPath!);
            string? expectedClipName = string.IsNullOrWhiteSpace(animFilePath)
                ? null
                : Path.GetFileNameWithoutExtension(animFilePath);

            if (!args.Contains("--verify-only", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[CookUnityPrefab] Importing '{resolvedPrefabPath}' (project='{resolvedProjectPath}')...");
                SerializedPrefabConversionResult conversion = SerializedSceneImporter.ImportPrefabWithManifest(
                    resolvedPrefabPath,
                    outputDestination: null,
                    explicitProjectOrAssetsRoot: resolvedProjectPath);

                SceneNode rootNode = conversion.RootNode
                    ?? throw new InvalidDataException($"Unity prefab import produced no root node for '{resolvedPrefabPath}'.");

                Console.WriteLine(
                    $"[CookUnityPrefab] Imported root '{rootNode.Name}' (tier={conversion.Manifest?.CompletionTier}, " +
                    $"diagnostics={conversion.Manifest?.Diagnostics.Count ?? 0}, meshletsCooked={conversion.MeshletCookingCompleted}).");

                PruneInactiveModelComponents(rootNode, out int activeModelCount, out int removedInactiveModelCount);
                int hydratedTextureCount = HydrateActiveModelTexturesAndMeshes(rootNode, maxTextureSize);
                Console.WriteLine(
                    $"[CookUnityPrefab] Prepared {activeModelCount} active ModelComponent(s), removed {removedInactiveModelCount} " +
                    $"inactive ModelComponent(s), and hydrated {hydratedTextureCount} texture(s) (maxSize={maxTextureSize}).");

                if (!string.IsNullOrWhiteSpace(animFilePath))
                    expectedClipName = AttachHumanoidAnimationClip(rootNode, conversion.Manifest, animFilePath);

                XRPrefabSource prefab = new()
                {
                    Name = Path.GetFileNameWithoutExtension(resolvedPrefabPath),
                    RootNode = rootNode,
                };

                WriteCookedPrefabPak(prefab, resolvedOutputPakPath, assetPath);
            }

            CookedBakeInput verifyInput = CookedBakeInputLoader.Load(resolvedOutputPakPath, assetPath, GaussianBakeKind.Avatar);
            if (!BakeWorldFactory.TryComputeContentBounds(verifyInput, out AABB bounds))
                throw new InvalidOperationException($"Cooked prefab '{assetPath}' in '{resolvedOutputPakPath}' has no valid content bounds.");

            if (verifyInput.PrefabRoot is { } verifyRoot)
            {
                List<string> motionWarnings = [];
                BakeMotionChoice motionChoice = BakeMotionSelector.ChooseForPrefab(
                    verifyRoot,
                    expectedClipName,
                    GaussianBakeBudget.For(GaussianBakeKind.Avatar),
                    motionWarnings);
                try
                {
                    Console.WriteLine(
                        $"[CookUnityPrefab] Verified motion: clip='{motionChoice.ClipName ?? "<still>"}', " +
                        $"source={motionChoice.Source}, duration={motionChoice.DurationSeconds:F2}s, " +
                        $"frames={motionChoice.FrameCount}, warnings=[{string.Join(", ", motionWarnings)}].");
                    if (motionWarnings.Count > 0)
                    {
                        verifyRoot.IterateComponents<AnimationClipComponent>(animComp =>
                        {
                            if (!string.IsNullOrWhiteSpace(animComp.PlaybackCapabilityDiagnostic))
                            {
                                Console.WriteLine(
                                    $"[CookUnityPrefab] Clip diagnostic ('{animComp.Animation?.Name}'): {animComp.PlaybackCapabilityDiagnostic}");
                            }
                        }, iterateChildHierarchy: true);
                    }

                    if (expectedClipName is not null
                        && !string.Equals(motionChoice.ClipName, expectedClipName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Cooked prefab did not select requested humanoid clip '{expectedClipName}' " +
                            $"(selected='{motionChoice.ClipName}', warnings=[{string.Join(", ", motionWarnings)}]).");
                    }

                    if (motionChoice.FrameCount > 1)
                    {
                        float probeTime = Math.Min(0.8f, motionChoice.DurationSeconds * 0.25f);
                        DeformedSurface surfaceStart = BakeFrameSampler.EvaluateAt(verifyRoot, motionChoice, 0.0f);
                        DeformedSurface surfaceProbe = BakeFrameSampler.EvaluateAt(verifyRoot, motionChoice, probeTime);
                        float maxDisplacement = ComputeMaxVertexDisplacement(surfaceStart, surfaceProbe);
                        Console.WriteLine(
                            $"[CookUnityPrefab] Verified surface deformation: vertices={surfaceStart.VertexCount}, " +
                            $"maxDisplacement(0.00s -> {probeTime:F2}s)={maxDisplacement:F4}m.");
                    }
                }
                finally
                {
                    BakeMotionSelector.ReleaseTemporaryPlayers(motionChoice);
                }
            }

            stopwatch.Stop();
            FileInfo pakFileInfo = new(resolvedOutputPakPath);
            Console.WriteLine(
                $"[CookUnityPrefab] Ready: '{resolvedOutputPakPath}' ({pakFileInfo.Length / (1024.0 * 1024.0):F2} MiB, " +
                $"asset='{assetPath}', bounds={bounds.Min}..{bounds.Max}, elapsed={stopwatch.Elapsed.TotalSeconds:F1}s).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unity prefab cook failed: {ex}");
            return 1;
        }
    }

    private static void PruneInactiveModelComponents(
        SceneNode rootNode,
        out int activeCount,
        out int removedCount)
    {
        List<ModelComponent> inactiveModels = [];
        int active = 0;
        rootNode.IterateComponents<ModelComponent>(modelComponent =>
        {
            if (BakeMotionSelector.IsEnabled(modelComponent, rootNode))
                active++;
            else
                inactiveModels.Add(modelComponent);
        }, iterateChildHierarchy: true);

        foreach (ModelComponent inactive in inactiveModels)
        {
            inactive.SceneNode?.DetachComponent(inactive);
            inactive.Destroy();
        }

        activeCount = active;
        removedCount = inactiveModels.Count;
    }

    private static int HydrateActiveModelTexturesAndMeshes(SceneNode rootNode, uint maxTextureSize)
    {
        IRuntimeImageCodec codec = RuntimeImageCodecs.Require();
        HashSet<XRTexture2D> processedTextures = new(ReferenceEqualityComparer.Instance);
        int hydratedCount = 0;

        rootNode.IterateComponents<ModelComponent>(modelComponent =>
        {
            if (!BakeMotionSelector.IsEnabled(modelComponent, rootNode))
                return;

            if (modelComponent.Model is { } model)
            {
                foreach (var subMesh in model.Meshes)
                {
                    if (subMesh is null)
                        continue;

                    foreach (var lod in subMesh.LODs)
                    {
                        XRMaterial? material = lod?.Material;
                        if (material is null)
                            continue;

                        foreach (XRTexture? texture in material.Textures)
                        {
                            if (texture is not XRTexture2D texture2D || !processedTextures.Add(texture2D))
                                continue;

                            if (TryHydrateTextureFromSource(texture2D, codec, maxTextureSize))
                                hydratedCount++;
                        }

                        material.PrepareUberVariantImmediately();
                    }
                }
            }

            modelComponent.RebuildRuntimeMeshes();
        }, iterateChildHierarchy: true);

        return hydratedCount;
    }

    private static bool TryHydrateTextureFromSource(
        XRTexture2D texture,
        IRuntimeImageCodec codec,
        uint maxTextureSize)
    {
        string? sourcePath = ResolveReadableTexturePath(texture);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            ConfigureSynchronousSampling(texture);
            return false;
        }

        ETextureColorSpace importedColorSpace = texture.ImportedColorSpace;
        ETextureImportUsage importedUsage = texture.ImportedUsage;
        bool normalFlipGreen = texture.ImportedNormalMapFlipGreen;
        bool alphaAsTransparency = texture.AlphaAsTransparency;
        ETexWrapMode uWrap = texture.UWrap;
        ETexWrapMode vWrap = texture.VWrap;
        string? samplerName = texture.SamplerName;

        byte[] encodedBytes = File.ReadAllBytes(sourcePath);
        using RuntimeImage decoded = codec.Decode(encodedBytes, sourcePath);
        uint largestDimension = Math.Max(decoded.Width, decoded.Height);
        using RuntimeImage? resized = maxTextureSize > 0 && largestDimension > maxTextureSize
            ? ResizeToLimit(codec, decoded, maxTextureSize)
            : null;
        RuntimeImage targetImage = resized ?? decoded;

        Mipmap2D baseMip = new(targetImage);
        texture.Mipmaps = [baseMip];
        texture.SizedInternalFormat = ResolveSizedInternalFormat(baseMip.InternalFormat, importedColorSpace);
        texture.SamplerName = samplerName;
        texture.ImportedColorSpace = importedColorSpace;
        texture.ImportedUsage = importedUsage;
        texture.ImportedNormalMapFlipGreen = normalFlipGreen;
        texture.AlphaAsTransparency = alphaAsTransparency;
        texture.UWrap = uWrap;
        texture.VWrap = vWrap;
        ConfigureSynchronousSampling(texture);
        return true;
    }

    private static void ConfigureSynchronousSampling(XRTexture2D texture)
    {
        texture.AutoGenerateMipmaps = false;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.LargestMipmapLevel = 0;
        texture.SmallestAllowedMipmapLevel = 0;
        texture.Resizable = false;
        texture.PreferSynchronousGpuUpload = true;
    }

    private static RuntimeImage ResizeToLimit(IRuntimeImageCodec codec, RuntimeImage source, uint maxDimension)
    {
        uint largest = Math.Max(1u, Math.Max(source.Width, source.Height));
        double scale = maxDimension / (double)largest;
        uint targetWidth = Math.Max(1u, (uint)Math.Round(source.Width * scale));
        uint targetHeight = Math.Max(1u, (uint)Math.Round(source.Height * scale));
        return codec.Resize(source, targetWidth, targetHeight, RuntimeImageResizeMode.Standard);
    }

    private static ESizedInternalFormat ResolveSizedInternalFormat(
        EPixelInternalFormat internalFormat,
        ETextureColorSpace colorSpace)
    {
        bool isSrgb = colorSpace == ETextureColorSpace.Srgb;
        return internalFormat switch
        {
            EPixelInternalFormat.Rgb or EPixelInternalFormat.Rgb8 or EPixelInternalFormat.Srgb or EPixelInternalFormat.Srgb8
                => isSrgb ? ESizedInternalFormat.Srgb8 : ESizedInternalFormat.Rgb8,
            _ => isSrgb ? ESizedInternalFormat.Srgb8Alpha8 : ESizedInternalFormat.Rgba8,
        };
    }

    private static string? ResolveReadableTexturePath(XRTexture2D texture)
    {
        if (!string.IsNullOrWhiteSpace(texture.OriginalPath)
            && File.Exists(texture.OriginalPath)
            && !texture.OriginalPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(texture.OriginalPath);
        }

        if (!string.IsNullOrWhiteSpace(texture.FilePath)
            && File.Exists(texture.FilePath)
            && !texture.FilePath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(texture.FilePath);
        }

        return null;
    }

    private static void WriteCookedPrefabPak(XRPrefabSource prefab, string outputPakPath, string assetPath)
    {
        string normalizedAssetPath = assetPath.Replace('\\', '/').TrimStart('/');
        string stagingDirectory = Path.Combine(
            Path.GetDirectoryName(outputPakPath) ?? Path.GetTempPath(),
            $".staging-{Path.GetFileNameWithoutExtension(outputPakPath)}-{Guid.NewGuid():N}");

        try
        {
            string stagedAssetFile = Path.Combine(stagingDirectory, normalizedAssetPath.Replace('/', Path.DirectorySeparatorChar));
            string? stagedParent = Path.GetDirectoryName(stagedAssetFile);
            if (!string.IsNullOrWhiteSpace(stagedParent))
                Directory.CreateDirectory(stagedParent);

            string typeReference = CookedAssetTypeReference.Encode(typeof(XRPrefabSource));
            byte[] envelope;
            if (PublishedCookedAssetRegistry.TrySerialize(prefab, out byte[]? runtimePayload) && runtimePayload is not null)
            {
                envelope = CookedAssetEnvelope.Serialize(
                    new CookedAssetBlob(typeReference, CookedAssetFormat.RuntimeBinaryV1, runtimePayload));
            }
            else
            {
                byte[] binaryPayload = CookedBinarySerializer.Serialize(prefab);
                envelope = CookedAssetEnvelope.Serialize(
                    new CookedAssetBlob(typeReference, CookedAssetFormat.BinaryV2, binaryPayload));
            }

            File.WriteAllBytes(stagedAssetFile, envelope);

            string? outputDirectory = Path.GetDirectoryName(outputPakPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);
            if (File.Exists(outputPakPath))
                File.Delete(outputPakPath);

            AssetPacker.Pack(stagingDirectory, outputPakPath);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static string AttachHumanoidAnimationClip(
        SceneNode rootNode,
        SerializedPrefabImportManifest? manifest,
        string animFilePath)
    {
        string resolvedAnimPath = Path.GetFullPath(animFilePath);
        if (!File.Exists(resolvedAnimPath))
            throw new FileNotFoundException($"The humanoid animation clip '{resolvedAnimPath}' does not exist.", resolvedAnimPath);

        SceneNode avatarRoot = FindHumanoidTargetNode(rootNode);
        RemoveExistingAnimationClipComponents(rootNode, out int removedGenericClips);

        (string? fbxPath, string fbxSha256) = ResolvePrimaryModelDependency(manifest);
        Dictionary<EHumanoidAvatarBoneRole, SceneNode?> explicitMappings = fbxPath is not null
            ? ResolveMetaHumanoidBoneMappings(avatarRoot, fbxPath + ".meta")
            : [];

        HumanoidComponent humanoid;
        using (HumanoidComponent.BeginDeferredSceneNodeInitialization())
        {
            humanoid = avatarRoot.GetComponent<HumanoidComponent>()
                ?? avatarRoot.AddComponent<HumanoidComponent>("Humanoid")
                ?? throw new InvalidOperationException($"Failed to add HumanoidComponent to '{avatarRoot.Name}'.");
        }

        if (!string.IsNullOrWhiteSpace(fbxSha256))
            humanoid.SetSourceModelContentSha256(fbxSha256);

        humanoid.InitializeSceneNodeBindings();
        if (explicitMappings.Count > 0)
            humanoid.SetAvatarBoneMappings(explicitMappings, lockEditorCorrections: true);

        if (!humanoid.ConfirmAvatarDefinition(out string confirmDiagnostic))
            throw new InvalidOperationException($"Humanoid avatar definition confirmation failed: {confirmDiagnostic}");

        humanoid.PosePreviewMode = EHumanoidPosePreviewMode.AnimatedPose;

        AnimationClip clip = AnimYamlImporter.Import(resolvedAnimPath);
        if (clip.UsesCyclicSourcePlayback)
            clip.Looped = true;

        AnimationClipComponent animComponent = avatarRoot.AddComponent<AnimationClipComponent>("HumanoidClip")
            ?? throw new InvalidOperationException($"Failed to add AnimationClipComponent to '{avatarRoot.Name}'.");
        animComponent.StartOnActivate = false;
        animComponent.RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ExtractOnly;
        animComponent.Animation = clip;

        if (!animComponent.TryValidatePlaybackCapabilities(out string playbackDiagnostic))
            throw new InvalidOperationException($"Humanoid clip '{clip.Name}' cannot play on '{avatarRoot.Name}': {playbackDiagnostic}");

        Console.WriteLine(
            $"[CookUnityPrefab] Attached humanoid clip '{clip.Name}' ({clip.LengthInSeconds:F2}s, looped={clip.Looped}, " +
            $"mappedRoles={explicitMappings.Count}, removedGenericClips={removedGenericClips}) to '{avatarRoot.Name}'.");
        return clip.Name ?? Path.GetFileNameWithoutExtension(resolvedAnimPath);
    }

    private static SceneNode FindHumanoidTargetNode(SceneNode rootNode)
    {
        SceneNode? matched = null;
        rootNode.IterateComponents<HumanoidAuthoredTPoseComponent>(component =>
        {
            if (matched is null && component.SceneNode is not null)
                matched = component.SceneNode;
        }, iterateChildHierarchy: true);
        return matched ?? rootNode;
    }

    private static void RemoveExistingAnimationClipComponents(SceneNode rootNode, out int removedCount)
    {
        List<AnimationClipComponent> existing = [];
        rootNode.IterateComponents<AnimationClipComponent>(existing.Add, iterateChildHierarchy: true);
        foreach (AnimationClipComponent component in existing)
        {
            component.SceneNode?.DetachComponent(component);
            component.Destroy();
        }

        removedCount = existing.Count;
    }

    private static (string? FbxPath, string Sha256) ResolvePrimaryModelDependency(SerializedPrefabImportManifest? manifest)
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

    private static Dictionary<EHumanoidAvatarBoneRole, SceneNode?> ResolveMetaHumanoidBoneMappings(
        SceneNode avatarRoot,
        string metaPath)
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

    private static string ComputeFileSha256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool TryParseArgs(
        string[] args,
        out string? unityProjectPath,
        out string? prefabPath,
        out string? outputPakPath,
        out string assetPath,
        out uint maxTextureSize,
        out string? animFilePath,
        out string? error)
    {
        unityProjectPath = null;
        prefabPath = null;
        outputPakPath = null;
        assetPath = "Avatars/avatar.asset";
        maxTextureSize = DefaultMaxTextureSize;
        animFilePath = null;
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, CommandFlag, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(arg, "--verify-only", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(arg, "--unity-project", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "Missing path after --unity-project.";
                    return false;
                }

                unityProjectPath = args[++i];
                continue;
            }

            if (string.Equals(arg, "--prefab", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "Missing path after --prefab.";
                    return false;
                }

                prefabPath = args[++i];
                continue;
            }

            if (string.Equals(arg, "--output-pak", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "Missing path after --output-pak.";
                    return false;
                }

                outputPakPath = args[++i];
                continue;
            }

            if (string.Equals(arg, "--asset", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "Missing path after --asset.";
                    return false;
                }

                assetPath = args[++i];
                continue;
            }

            if (string.Equals(arg, "--max-texture-size", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !uint.TryParse(args[++i], CultureInfo.InvariantCulture, out maxTextureSize) || maxTextureSize == 0)
                {
                    error = "Invalid value after --max-texture-size.";
                    return false;
                }

                continue;
            }

            if (string.Equals(arg, "--anim", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "Missing path after --anim.";
                    return false;
                }

                animFilePath = args[++i];
                continue;
            }
        }

        if (string.IsNullOrWhiteSpace(unityProjectPath))
        {
            error = "Required argument --unity-project was not provided.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            error = "Required argument --prefab was not provided.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputPakPath))
        {
            error = "Required argument --output-pak was not provided.";
            return false;
        }

        return true;
    }

    private static float ComputeMaxVertexDisplacement(DeformedSurface first, DeformedSurface second)
    {
        int count = Math.Min(first.Positions.Length, second.Positions.Length) / 3;
        float maxSquared = 0.0f;
        for (int i = 0; i < count; i++)
        {
            int offset = i * 3;
            float dx = second.Positions[offset] - first.Positions[offset];
            float dy = second.Positions[offset + 1] - first.Positions[offset + 1];
            float dz = second.Positions[offset + 2] - first.Positions[offset + 2];
            float distSquared = (dx * dx) + (dy * dy) + (dz * dz);
            if (distSquared > maxSquared)
                maxSquared = distSquared;
        }

        return MathF.Sqrt(maxSquared);
    }
}

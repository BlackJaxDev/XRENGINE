using System.Diagnostics;
using System.Globalization;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Core;
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
using XREngine.Scene.Transforms;

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

    /// <summary>
    /// The flag that lets the import continue when required visual assets are missing from the Unity project.
    /// The importer then keeps the available content, uses fallback materials, and reports each missing asset.
    /// Without this flag, a missing material or texture stops the cook.
    /// </summary>
    private const string AllowPartialImportFlag = "--allow-partial-import";

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
                out string? sceneRootName,
                out string? outputPakPath,
                out string assetPath,
                out uint maxTextureSize,
                out string? animFilePath,
                out string? error))
        {
            Console.Error.WriteLine($"Unity prefab cook failed: {error}");
            return 2;
        }

        CookStageTimings timings = new();
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
                bool allowPartialImport = args.Contains(AllowPartialImportFlag, StringComparer.OrdinalIgnoreCase);
                SerializedPrefabConversionResult conversion;
                using (timings.Measure("import-prefab"))
                {
                    conversion = SerializedSceneImporter.ImportPrefabWithManifest(
                        resolvedPrefabPath,
                        outputDestination: null,
                        explicitProjectOrAssetsRoot: resolvedProjectPath,
                        CancellationToken.None,
                        progress: null,
                        allowPartialImport: allowPartialImport,
                        sceneRootName: sceneRootName);
                }

                SceneNode rootNode = conversion.RootNode
                    ?? throw new InvalidDataException($"Unity prefab import produced no root node for '{resolvedPrefabPath}'.");

                Console.WriteLine(
                    $"[CookUnityPrefab] Imported root '{rootNode.Name}' (tier={conversion.Manifest?.CompletionTier}, " +
                    $"diagnostics={conversion.Manifest?.Diagnostics.Count ?? 0}, meshletsCooked={conversion.MeshletCookingCompleted}, " +
                    $"partialImport={allowPartialImport}).");
                if (allowPartialImport)
                    PrintPartialImportDiagnostics(conversion.Manifest);

                int activeModelCount;
                int removedInactiveModelCount;
                using (timings.Measure("prune-inactive-models"))
                    PruneInactiveModelComponents(rootNode, out activeModelCount, out removedInactiveModelCount);

                int hydratedTextureCount = HydrateActiveModelTexturesAndMeshes(rootNode, maxTextureSize, timings);
                Console.WriteLine(
                    $"[CookUnityPrefab] Prepared {activeModelCount} active ModelComponent(s), removed {removedInactiveModelCount} " +
                    $"inactive ModelComponent(s), and hydrated {hydratedTextureCount} texture(s) (maxSize={maxTextureSize}).");

                if (!string.IsNullOrWhiteSpace(animFilePath))
                {
                    using (timings.Measure("attach-humanoid-clip"))
                        expectedClipName = AttachHumanoidAnimationClip(rootNode, conversion.Manifest, animFilePath);
                }

                XRPrefabSource prefab = new()
                {
                    Name = !string.IsNullOrWhiteSpace(sceneRootName)
                        ? sceneRootName
                        : Path.GetFileNameWithoutExtension(resolvedPrefabPath),
                    RootNode = rootNode,
                };

                (int cookMaterials, int cookNativeUber) = CountActiveMaterialSemantics(rootNode);
                Console.WriteLine(
                    $"[CookUnityPrefab] Material semantics before write: materials={cookMaterials}, nativeUberV1={cookNativeUber}.");

                WriteCookedPrefabPak(prefab, resolvedOutputPakPath, assetPath, timings);
            }

            CookedBakeInput verifyInput;
            using (timings.Measure("verify-load-pak"))
                verifyInput = CookedBakeInputLoader.Load(resolvedOutputPakPath, assetPath, GaussianBakeKind.Avatar);
            if (verifyInput.PrefabRoot is { } semanticRoot)
            {
                (int loadedMaterials, int loadedNativeUber) = CountActiveMaterialSemantics(semanticRoot);
                Console.WriteLine(
                    $"[CookUnityPrefab] Material semantics after load: materials={loadedMaterials}, nativeUberV1={loadedNativeUber}.");
            }
            AABB bounds;
            bool hasBounds;
            using (timings.Measure("verify-bounds"))
                hasBounds = BakeWorldFactory.TryComputeContentBounds(verifyInput, out bounds);
            if (!hasBounds)
                throw new InvalidOperationException($"Cooked prefab '{assetPath}' in '{resolvedOutputPakPath}' has no valid content bounds.");

            if (verifyInput.PrefabRoot is { } verifyRoot)
            {
                List<string> motionWarnings = [];
                BakeMotionChoice motionChoice;
                using (timings.Measure("verify-motion"))
                {
                    motionChoice = BakeMotionSelector.ChooseForPrefab(
                        verifyRoot,
                        expectedClipName,
                        GaussianBakeBudget.For(GaussianBakeKind.Avatar),
                        motionWarnings);
                }
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
                        verifyRoot.IterateComponents<HumanoidComponent>(loadedHumanoid =>
                            Console.WriteLine(
                                $"[CookUnityPrefab] Loaded humanoid '{loadedHumanoid.SceneNode?.Name}' bind positions: {SerializedHumanoidAvatarSetup.DescribeSymmetryRoles(loadedHumanoid)}"),
                            iterateChildHierarchy: true);
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
                        DeformedSurface surfaceStart;
                        DeformedSurface surfaceProbe;
                        using (timings.Measure("verify-deformation"))
                        {
                            surfaceStart = BakeFrameSampler.EvaluateAt(verifyRoot, motionChoice, 0.0f);
                            surfaceProbe = BakeFrameSampler.EvaluateAt(verifyRoot, motionChoice, probeTime);
                        }
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
        finally
        {
            Console.WriteLine(timings.FormatSummary("[CookUnityPrefab]"));
        }
    }

    /// <summary>
    /// Prints the warning and error diagnostics of a partial import, so the log names each missing asset and each
    /// fallback material instead of hiding them in the manifest.
    /// </summary>
    private static void PrintPartialImportDiagnostics(SerializedPrefabImportManifest? manifest)
    {
        if (manifest is null)
            return;

        int printed = 0;
        foreach (SourceImportDiagnostic diagnostic in manifest.Diagnostics)
        {
            if (diagnostic.Severity == SourceImportDiagnosticSeverity.Info)
                continue;

            Console.WriteLine($"[CookUnityPrefab] Partial import {diagnostic.Severity}: {diagnostic}");
            printed++;
        }

        Console.WriteLine($"[CookUnityPrefab] Partial import reported {printed} warning or error diagnostic(s).");
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

    /// <summary>
    /// Counts the distinct materials on active model submeshes and how many of them carry
    /// <see cref="EngineMaterialSemanticIdentity.NativeUberV1"/>. The cook prints the counts before
    /// the pak write and after the verify load, so a lost semantic shows which side dropped it.
    /// </summary>
    private static (int Total, int NativeUber) CountActiveMaterialSemantics(SceneNode rootNode)
    {
        HashSet<XRMaterial> materials = new(ReferenceEqualityComparer.Instance);
        int nativeUber = 0;
        rootNode.IterateComponents<ModelComponent>(modelComponent =>
        {
            if (!BakeMotionSelector.IsEnabled(modelComponent, rootNode) || modelComponent.Model is not { } model)
                return;

            foreach (var subMesh in model.Meshes)
            {
                if (subMesh is null)
                    continue;

                foreach (var lod in subMesh.LODs)
                {
                    if (lod?.Material is not { } material || !materials.Add(material))
                        continue;

                    if (material.EngineSemantic == EngineMaterialSemanticIdentity.NativeUberV1)
                        nativeUber++;
                }
            }
        }, iterateChildHierarchy: true);

        return (materials.Count, nativeUber);
    }

    /// <summary>
    /// Decodes and resizes the source textures of every active material, initializes the authored Uber state of each
    /// material, and rebuilds the runtime meshes of every active model. The method adds the time of each part to
    /// <paramref name="timings"/> as <c>hydrate-textures</c>, <c>init-uber-materials</c>, and <c>rebuild-meshes</c>.
    /// </summary>
    private static int HydrateActiveModelTexturesAndMeshes(SceneNode rootNode, uint maxTextureSize, CookStageTimings timings)
    {
        IRuntimeImageCodec codec = RuntimeImageCodecs.Require();
        HashSet<XRTexture2D> processedTextures = new(ReferenceEqualityComparer.Instance);
        HashSet<XRMaterial> processedMaterials = new(ReferenceEqualityComparer.Instance);
        int hydratedCount = 0;

        void HydrateMaterial(XRMaterial? material)
        {
            if (material is null || !processedMaterials.Add(material))
                return;

            using (timings.Measure("hydrate-textures"))
            {
                foreach (XRTexture? texture in material.Textures)
                {
                    if (texture is not XRTexture2D texture2D || !processedTextures.Add(texture2D))
                        continue;

                    if (TryHydrateTextureFromSource(texture2D, codec, maxTextureSize))
                        hydratedCount++;
                }
            }

            // Initialize authored Uber state while keeping canonical UberShader.frag in the cooked pak
            // so deserialization preserves EngineSemantic = NativeUberV1 without variant source bloat.
            using (timings.Measure("init-uber-materials"))
                material.EnsureUberStateInitialized();
        }

        rootNode.IterateComponents<ModelComponent>(modelComponent =>
        {
            if (!BakeMotionSelector.IsEnabled(modelComponent, rootNode))
                return;

            foreach (var renderableMesh in modelComponent.Meshes)
                HydrateMaterial(renderableMesh?.MaterialOverride);

            if (modelComponent.Model is { } model)
            {
                foreach (var subMesh in model.Meshes)
                {
                    if (subMesh is null)
                        continue;

                    foreach (var lod in subMesh.LODs)
                        HydrateMaterial(lod?.Material);
                }
            }

            using (timings.Measure("rebuild-meshes"))
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
        bool generateMipmaps = texture.AutoGenerateMipmaps;
        ETexMinFilter minFilter = texture.MinFilter;
        ETexMagFilter magFilter = texture.MagFilter;

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
        ConfigureSynchronousSampling(texture, generateMipmaps, minFilter, magFilter);
        return true;
    }

    /// <summary>
    /// Sets the upload and sampling state of a cooked texture. The cook stores only mip 0, so a texture that the
    /// source import marked for mipmaps generates its mip chain on the GPU after upload. Without that chain a
    /// minified high-frequency texture, such as a face, hair, or fishnet texture, aliases into bright and dark
    /// pixel noise. Textures that the source import did not mark for mipmaps sample mip 0 with linear filtering.
    /// </summary>
    private static void ConfigureSynchronousSampling(
        XRTexture2D texture,
        bool generateMipmaps = false,
        ETexMinFilter minFilter = ETexMinFilter.Linear,
        ETexMagFilter magFilter = ETexMagFilter.Linear)
    {
        texture.AutoGenerateMipmaps = generateMipmaps;
        texture.MinFilter = generateMipmaps ? minFilter : ETexMinFilter.Linear;
        texture.MagFilter = magFilter;
        texture.LargestMipmapLevel = 0;
        texture.SmallestAllowedMipmapLevel = generateMipmaps ? 1000 : 0;
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

    private static void WriteCookedPrefabPak(XRPrefabSource prefab, string outputPakPath, string assetPath, CookStageTimings timings)
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
            using (timings.Measure("serialize-prefab"))
            {
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
            }

            using (timings.Measure("pack-archive"))
            {
                File.WriteAllBytes(stagedAssetFile, envelope);

                string? outputDirectory = Path.GetDirectoryName(outputPakPath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                    Directory.CreateDirectory(outputDirectory);
                if (File.Exists(outputPakPath))
                    File.Delete(outputPakPath);

                AssetPacker.Pack(stagingDirectory, outputPakPath);
            }
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

        SceneNode avatarRoot = SerializedHumanoidAvatarSetup.FindHumanoidTargetNode(rootNode);
        RemoveExistingAnimationClipComponents(rootNode, out int removedGenericClips);

        (string? fbxPath, string fbxSha256) = SerializedHumanoidAvatarSetup.FindPrimaryModelDependency(manifest);
        SerializedHumanoidAvatarSetup.Configure(avatarRoot, fbxPath, fbxSha256, out int mappedRoleCount);

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
            $"mappedRoles={mappedRoleCount}, removedGenericClips={removedGenericClips}) to '{avatarRoot.Name}'.");
        return clip.Name ?? Path.GetFileNameWithoutExtension(resolvedAnimPath);
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

    private static bool TryParseArgs(
        string[] args,
        out string? unityProjectPath,
        out string? prefabPath,
        out string? sceneRootName,
        out string? outputPakPath,
        out string assetPath,
        out uint maxTextureSize,
        out string? animFilePath,
        out string? error)
    {
        unityProjectPath = null;
        prefabPath = null;
        sceneRootName = null;
        outputPakPath = null;
        string? explicitAssetPath = null;
        assetPath = string.Empty;
        maxTextureSize = DefaultMaxTextureSize;
        animFilePath = null;
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, CommandFlag, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(arg, "--verify-only", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, AllowPartialImportFlag, StringComparison.OrdinalIgnoreCase))
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

            if (string.Equals(arg, "--scene-root", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "Missing node name after --scene-root.";
                    return false;
                }

                sceneRootName = args[++i];
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

                explicitAssetPath = args[++i];
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

        // Without --asset, the archive entry takes the scene root name or the prefab file name, so two cooks write different entries.
        string entryName = string.IsNullOrWhiteSpace(sceneRootName)
            ? Path.GetFileNameWithoutExtension(prefabPath)
            : string.Join("_", sceneRootName.Trim().Split(Path.GetInvalidFileNameChars()));
        assetPath = explicitAssetPath ?? $"Avatars/{entryName}.asset";

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

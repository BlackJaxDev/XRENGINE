using System.Diagnostics;
using System.Globalization;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Imaging;
using XREngine.Rendering;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Prefabs;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Imports a Unity <c>.prefab</c> file into an <see cref="XRPrefabSource"/> with
/// <see cref="SerializedSceneImporter.ImportPrefabWithManifest(string, string?, string?, XREngine.Rendering.Models.FbxImportBackend)"/>,
/// hydrates active material textures, and cooks the prefab into a <c>.pak</c> archive for
/// <see cref="BakeSplatPreviewCommand"/>.
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

            if (! args.Contains("--verify-only", StringComparer.OrdinalIgnoreCase))
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

    private static bool TryParseArgs(
        string[] args,
        out string? unityProjectPath,
        out string? prefabPath,
        out string? outputPakPath,
        out string assetPath,
        out uint maxTextureSize,
        out string? error)
    {
        unityProjectPath = null;
        prefabPath = null;
        outputPakPath = null;
        assetPath = "Avatars/avatar.asset";
        maxTextureSize = DefaultMaxTextureSize;
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, CommandFlag, StringComparison.OrdinalIgnoreCase))
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
}

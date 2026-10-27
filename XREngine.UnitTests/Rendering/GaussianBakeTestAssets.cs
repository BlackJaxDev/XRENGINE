using Shouldly;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Fbx;
using XREngine.Imaging;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Runtime.Imaging.Magick;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Imports, cooks, and packs the content that the gaussian bake tests and fixtures use.
/// Cooking follows the published-build path: the registered runtime codec first, then the
/// generic reference-graph payload when no codec is registered for the type.
/// </summary>
internal static class GaussianBakeTestAssets
{
    /// <summary>
    /// The largest side, in pixels, of a texture that <see cref="ImportPrefabWithEngineMaterials"/> decodes into a prefab.
    /// The Corvette has 58 textures of up to 8K, and their full pixels make a cooked payload of more than 2 GB.
    /// </summary>
    public const uint MaxFixtureTextureSize = 256;

    /// <summary>
    /// Gets the absolute path of the skinned, morphed, and animated glTF test model. The test uses
    /// the <c>.gltf</c> file of this corpus entry. The native glTF importer does not parse the
    /// <c>.glb</c> copy, and the Assimp fallback for that copy attaches no animation clip component.
    /// </summary>
    public static string SkinnedAvatarModelPath
        => Path.Combine(GltfImportTestUtilities.ResolveWorkspaceRoot(), "XREngine.UnitTests", "TestData", "Gltf", "skinned-morph-animated.gltf");

    /// <summary>Gets the absolute path of the static Corvette prop model.</summary>
    public static string CorvetteModelPath
        => Path.Combine(GltfImportTestUtilities.ResolveWorkspaceRoot(), "Build", "CommonAssets", "Models", "Corvette", "Corvette_PBR.fbx");

    /// <summary>
    /// Imports a model file into a prefab with the default model import options.
    /// Materials and textures are test stubs that keep only names and texture paths.
    /// </summary>
    public static XRPrefabSource ImportPrefab(string modelPath)
    {
        using ModelAssetImporter importer = GltfImportTestUtilities.CreateImporter(modelPath, GltfImportBackend.Auto);
        return Import(importer, modelPath);
    }

    /// <summary>
    /// Imports a model file into a prefab with the default lit engine materials that the editor import makes.
    /// The shaders come from the engine shader assets, and the texture pixels are decoded into the prefab,
    /// so a cooked archive renders without the source files. A texture that is larger than
    /// <see cref="MaxFixtureTextureSize"/> on one side is made smaller, so that the cooked payload stays below its 2 GB limit.
    /// </summary>
    /// <param name="modelPath">The model file to import.</param>
    /// <param name="doubleSided">
    /// When true, every material draws both faces. Use this for planar test models that a bake sees from all sides.
    /// </param>
    public static XRPrefabSource ImportPrefabWithEngineMaterials(string modelPath, bool doubleSided)
    {
        IRuntimeImageCodec? previousCodec = RuntimeImageCodecs.Current;
        RuntimeImageCodecs.Current = new MagickRuntimeImageCodec();
        try
        {
            using IDisposable shaderServices = EngineRuntimeShaderServices.Install();
            using ModelAssetImporter importer = new(modelPath, onCompleted: null, materialFactory: null)
            {
                ImportOptions = new ModelImportOptions
                {
                    GltfBackend = GltfImportBackend.Auto,
                    GenerateMeshRenderersAsync = false,
                    ProcessMeshesAsynchronously = false,
                    BatchSubmeshAddsDuringAsyncImport = true,
                },
                MakeTextureAction = LoadTextureWithPixels,
            };

            XRPrefabSource prefab = Import(importer, modelPath);
            if (doubleSided)
                DisableBackFaceCulling(prefab.RootNode.ShouldNotBeNull());
            return prefab;
        }
        finally
        {
            RuntimeImageCodecs.Current = previousCodec;
        }
    }

    private static XRPrefabSource Import(ModelAssetImporter importer, string modelPath)
    {
        ModelImportOptions options = importer.ImportOptions.ShouldNotBeNull();
        SceneNode root = importer.Import(
            options.ImportSteps,
            preservePivots: options.FbxPivotPolicy == FbxPivotImportPolicy.PreservePivotSemantics,
            removeAssimpFBXNodes: options.CollapseGeneratedFbxHelperNodes,
            scaleConversion: options.ScaleConversion,
            zUp: options.ZUp,
            multiThread: options.MultiThread,
            processMeshesAsynchronously: false).ShouldNotBeNull($"Import must succeed for '{modelPath}'.");

        return new XRPrefabSource
        {
            Name = Path.GetFileNameWithoutExtension(modelPath),
            RootNode = root,
        };
    }

    /// <summary>
    /// Cooks an asset into the envelope that the project builder writes. A registered runtime
    /// codec gives <see cref="CookedAssetFormat.RuntimeBinaryV1"/>; other types use
    /// <see cref="CookedAssetFormat.BinaryV2"/>.
    /// </summary>
    public static byte[] Cook(XRAsset asset)
    {
        string typeReference = CookedAssetTypeReference.Encode(asset.GetType());
        CookedAssetBlob blob = PublishedCookedAssetRegistry.TrySerialize(asset, out byte[] runtimePayload)
            ? new CookedAssetBlob(typeReference, CookedAssetFormat.RuntimeBinaryV1, runtimePayload)
            : new CookedAssetBlob(typeReference, CookedAssetFormat.BinaryV2, CookedBinarySerializer.Serialize(asset));
        return CookedAssetEnvelope.Serialize(blob);
    }

    /// <summary>Writes the entries to a staging directory and packs that directory into <paramref name="pakPath"/>.</summary>
    public static void WritePak(string pakPath, params (string AssetPath, byte[] Bytes)[] entries)
    {
        string workRoot = CreateWorkRoot();
        try
        {
            string staging = WriteStaging(workRoot, entries);
            AssetPacker.Pack(staging, pakPath);
        }
        finally
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }

    /// <summary>
    /// Packs the entries plus one entry named <c>../<paramref name="escapingFileName"/></c>.
    /// The file discovery service is replaced only while the archive is written.
    /// </summary>
    public static void WritePakWithEscapingEntry(
        string pakPath,
        (string AssetPath, byte[] Bytes)[] entries,
        string escapingFileName,
        byte[] escapingBytes)
    {
        string workRoot = CreateWorkRoot();
        IRuntimeFileDiscovery? previous = RuntimeFileDiscoveryServices.Current;
        try
        {
            string staging = WriteStaging(workRoot, entries);
            string outsidePath = Path.Combine(workRoot, escapingFileName);
            File.WriteAllBytes(outsidePath, escapingBytes);

            RuntimeFileDiscoveryServices.Current = new AppendedFileDiscovery(
                RuntimeFileDiscoveryServices.Required,
                staging,
                Path.Combine(staging, "..", escapingFileName));
            AssetPacker.Pack(staging, pakPath);
        }
        finally
        {
            RuntimeFileDiscoveryServices.Current = previous;
            Directory.Delete(workRoot, recursive: true);
        }
    }

    /// <summary>Returns the first sub-mesh of the first model component, in depth-first order.</summary>
    public static SubMesh? FirstSubMesh(SceneNode node)
    {
        foreach (ModelComponent component in node.GetComponents<ModelComponent>())
        {
            if (component.Model is null)
                continue;

            foreach (SubMesh subMesh in component.Model.Meshes)
            {
                if (subMesh.LODs.Min?.Mesh is not null)
                    return subMesh;
            }
        }

        foreach (TransformBase child in node.Transform.Children)
        {
            if (child.SceneNode is SceneNode childNode && FirstSubMesh(childNode) is SubMesh found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Counts the animation clip components in the hierarchy. When <paramref name="requireAnimation"/>
    /// is true, only components that hold a clip are counted.
    /// </summary>
    public static int CountClipComponents(SceneNode node, bool requireAnimation)
    {
        int count = 0;
        foreach (AnimationClipComponent clip in node.GetComponents<AnimationClipComponent>())
        {
            if (!requireAnimation || clip.Animation is not null)
                count++;
        }

        foreach (TransformBase child in node.Transform.Children)
        {
            if (child.SceneNode is SceneNode childNode)
                count += CountClipComponents(childNode, requireAnimation);
        }

        return count;
    }

    /// <summary>
    /// Decodes a texture file into a texture that holds its pixels. When the longer side is more than
    /// <see cref="MaxFixtureTextureSize"/>, the image is resampled so that the longer side is that size.
    /// </summary>
    private static XRTexture2D LoadTextureWithPixels(string path)
    {
        IRuntimeImageCodec codec = RuntimeImageCodecs.Require();
        using RuntimeImage decoded = codec.Decode(File.ReadAllBytes(path));
        using RuntimeImage? resized = ResizeForFixture(codec, decoded);
        return new XRTexture2D(resized ?? decoded)
        {
            Name = Path.GetFileNameWithoutExtension(path),
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.Linear,
            UWrap = ETexWrapMode.Repeat,
            VWrap = ETexWrapMode.Repeat,
            AutoGenerateMipmaps = false,
            Resizable = false,
        };
    }

    /// <summary>
    /// Returns a copy of the image with the longer side at <see cref="MaxFixtureTextureSize"/>, or null when the image
    /// is not larger than that size.
    /// </summary>
    private static RuntimeImage? ResizeForFixture(IRuntimeImageCodec codec, RuntimeImage image)
    {
        uint longer = Math.Max(image.Width, image.Height);
        if (longer <= MaxFixtureTextureSize)
            return null;

        double scale = (double)MaxFixtureTextureSize / longer;
        uint width = (uint)Math.Max(1L, (long)Math.Round(image.Width * scale, MidpointRounding.AwayFromZero));
        uint height = (uint)Math.Max(1L, (long)Math.Round(image.Height * scale, MidpointRounding.AwayFromZero));
        return codec.Resize(image, width, height, RuntimeImageResizeMode.Lanczos);
    }

    private static void DisableBackFaceCulling(SceneNode node)
    {
        foreach (ModelComponent component in node.GetComponents<ModelComponent>())
        {
            if (component.Model is null)
                continue;

            foreach (SubMesh subMesh in component.Model.Meshes)
                foreach (SubMeshLOD lod in subMesh.LODs)
                    if (lod.Material is XRMaterial material)
                        material.RenderOptions.CullMode = ECullMode.None;
        }

        foreach (TransformBase child in node.Transform.Children)
        {
            if (child.SceneNode is SceneNode childNode)
                DisableBackFaceCulling(childNode);
        }
    }

    private static string CreateWorkRoot()
    {
        string workRoot = Path.Combine(Path.GetTempPath(), "xre-gaussian-bake-pak", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        return workRoot;
    }

    private static string WriteStaging(string workRoot, (string AssetPath, byte[] Bytes)[] entries)
    {
        string staging = Path.Combine(workRoot, "staging");
        foreach ((string assetPath, byte[] bytes) in entries)
        {
            string filePath = Path.Combine(staging, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllBytes(filePath, bytes);
        }

        return staging;
    }
}

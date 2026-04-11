using Assimp;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Gltf;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Shaders.Generator;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

internal static class GltfImportTestUtilities
{
    public static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the workspace root for the glTF corpus tests.");
    }

    public static GltfCorpusManifest LoadManifest()
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        string manifestPath = Path.Combine(workspaceRoot, GltfPhase0Decisions.CorpusManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return GltfCorpusManifest.Load(manifestPath);
    }

    public static string ResolveCorpusAssetPath(GltfCorpusEntry entry)
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        return Path.Combine(workspaceRoot, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string ResolveExpectedSummaryPath(GltfCorpusEntry entry)
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        string manifestPath = Path.Combine(workspaceRoot, GltfPhase0Decisions.CorpusManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string manifestDirectory = Path.GetDirectoryName(manifestPath).ShouldNotBeNull();
        return Path.Combine(manifestDirectory, entry.ExpectedSummaryPath!.Replace('/', Path.DirectorySeparatorChar));
    }

    public static GltfGoldenSummary LoadExpectedSummary(GltfCorpusEntry entry)
    {
        string summaryPath = ResolveExpectedSummaryPath(entry);
        using FileStream stream = File.OpenRead(summaryPath);
        GltfGoldenSummary? summary = System.Text.Json.JsonSerializer.Deserialize<GltfGoldenSummary>(stream, GltfCorpusJson.SerializerOptions);
        return summary ?? throw new InvalidDataException($"Failed to deserialize glTF golden summary '{summaryPath}'.");
    }

    public static GltfGoldenSummary ImportAndSummarize(GltfCorpusEntry entry, GltfImportBackend backend, Action<ModelImporter>? configureImporter = null)
    {
        string assetPath = ResolveCorpusAssetPath(entry);
        using GltfAssetDocument document = GltfAssetDocument.Open(assetPath);
        ImportedSceneSummary summary = ImportAndSummarize(assetPath, backend, configureImporter);
        IReadOnlyList<string> usedExtensions = [.. document.Root.ExtensionsUsed.OrderBy(static extension => extension, StringComparer.Ordinal)];
        long fileSizeBytes = new FileInfo(assetPath).Length;

        return new GltfGoldenSummary(
            AssetId: entry.Id,
            ImportSucceeded: true,
            Container: entry.Container,
            FileSizeBytes: fileSizeBytes,
            NodeCount: summary.NodeCount,
            MeshCount: summary.MeshCount,
            MaterialCount: summary.MaterialCount,
            AnimationCount: summary.AnimationCount,
            SkinCount: summary.SkinCount,
            BoneCount: summary.BoneCount,
            MorphTargetCount: summary.MorphTargetCount,
            TotalVertices: summary.TotalVertices,
            TotalTriangles: summary.TotalTriangles,
            MaxHierarchyDepth: summary.MaxHierarchyDepth,
            TextureCount: summary.TextureCount,
            UsedExtensions: usedExtensions,
            Notes: entry.Notes);
    }

    public static ImportedSceneSummary ImportAndSummarize(string assetPath, GltfImportBackend backend, Action<ModelImporter>? configureImporter = null)
    {
        using ModelImporter importer = CreateImporter(assetPath, backend);
        configureImporter?.Invoke(importer);

        SceneNode? rootNode = importer.Import(PostProcessSteps.None, cancellationToken: default, onProgress: null);
        rootNode.ShouldNotBeNull($"Import should succeed for '{assetPath}' using backend {backend}.");
        return SummarizeImportedScene(rootNode!);
    }

    public static ModelImporter CreateImporter(string assetPath, GltfImportBackend backend)
    {
        Dictionary<string, XRTexture2D> textureCache = new(StringComparer.OrdinalIgnoreCase);

        XRMaterial CreateMaterialCore(XRTexture[] textureList, string name)
        {
            XRMaterial material = new()
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Material" : name,
            };

            while (material.Textures.Count < textureList.Length)
                material.Textures.Add(null);

            for (int index = 0; index < textureList.Length; index++)
                material.Textures[index] = textureList[index];

            return material;
        }

        XRTexture2D CreateTextureStub(string path)
        {
            if (textureCache.TryGetValue(path, out XRTexture2D? cachedTexture))
                return cachedTexture;

            XRTexture2D texture = new()
            {
                FilePath = path,
                Name = Path.GetFileNameWithoutExtension(path),
                AutoGenerateMipmaps = false,
                Resizable = false,
            };
            textureCache[path] = texture;
            return texture;
        }

        XRMaterial CreateMaterialFromSlots(string modelFilePath, string name, List<TextureSlot> textures, TextureFlags flags, ShadingMode mode, Dictionary<string, List<MaterialProperty>> properties)
        {
            XRTexture[] textureList = new XRTexture[textures.Count];
            for (int index = 0; index < textures.Count; index++)
                textureList[index] = CreateTextureStub(textures[index].FilePath);
            return CreateMaterialCore(textureList, name);
        }

        return new ModelImporter(assetPath, onCompleted: null, materialFactory: CreateMaterialFromSlots)
        {
            ImportOptions = new ModelImportOptions
            {
                GltfBackend = backend,
                GenerateMeshRenderersAsync = false,
                ProcessMeshesAsynchronously = false,
                BatchSubmeshAddsDuringAsyncImport = true,
            },
            MakeTextureAction = CreateTextureStub,
            MakeMaterialAction = (textureList, textures, name) => CreateMaterialCore(textureList, name),
        };
    }

    public static ImportedSceneSummary SummarizeImportedScene(SceneNode rootNode)
    {
        HashSet<XRMaterial> materials = [];
        HashSet<XRTexture> textures = [];
        HashSet<TransformBase> bones = [];
        HashSet<string> blendshapeNames = new(StringComparer.Ordinal);
        long totalVertices = 0;
        long totalTriangles = 0;
        int nodeCount = 0;
        int meshCount = 0;
        int animationCount = 0;
        int skinCount = 0;
        int maxHierarchyDepth = 0;

        Walk(rootNode, 0);
        return new ImportedSceneSummary(nodeCount, meshCount, materials.Count, animationCount, skinCount, bones.Count, blendshapeNames.Count, totalVertices, totalTriangles, maxHierarchyDepth, textures.Count);

        void Walk(SceneNode node, int depth)
        {
            nodeCount++;
            maxHierarchyDepth = Math.Max(maxHierarchyDepth, depth);

            foreach (AnimationClipComponent clip in node.GetComponents<AnimationClipComponent>())
            {
                if (clip.Animation is not null)
                    animationCount++;
            }

            foreach (ModelComponent component in node.GetComponents<ModelComponent>())
            {
                if (component.Model is null)
                    continue;

                foreach (SubMesh subMesh in component.Model.Meshes)
                {
                    XRMaterial? material = subMesh.LODs.Min?.Material;
                    if (material is not null)
                    {
                        materials.Add(material);
                        foreach (XRTexture? texture in material.Textures)
                        {
                            if (texture is not null)
                                textures.Add(texture);
                        }
                    }

                    XRMesh? mesh = subMesh.LODs.Min?.Mesh;
                    if (mesh is null)
                        continue;

                    meshCount++;
                    totalVertices += mesh.Vertices.LongLength;

                    int[]? indices = mesh.GetIndices();
                    if (indices is not null)
                        totalTriangles += indices.LongLength / 3;
                    else
                        totalTriangles += mesh.Vertices.LongLength / 3;

                    if (mesh.HasSkinning)
                        skinCount++;

                    foreach (string blendshapeName in mesh.BlendshapeNames)
                    {
                        if (!string.IsNullOrWhiteSpace(blendshapeName))
                            blendshapeNames.Add(blendshapeName);
                    }

                    foreach (Vertex vertex in mesh.Vertices)
                    {
                        if (vertex.Weights is null)
                            continue;

                        foreach (TransformBase bone in vertex.Weights.Keys)
                            bones.Add(bone);
                    }
                }
            }

            foreach (Transform childTransform in node.Transform.Children)
            {
                if (childTransform.SceneNode is SceneNode childNode)
                    Walk(childNode, depth + 1);
            }
        }
    }

    public sealed record ImportedSceneSummary(
        int NodeCount,
        int MeshCount,
        int MaterialCount,
        int AnimationCount,
        int SkinCount,
        int BoneCount,
        int MorphTargetCount,
        long TotalVertices,
        long TotalTriangles,
        int MaxHierarchyDepth,
        int TextureCount);

    public sealed class TestRuntimeShaderServices : IRuntimeShaderServices
    {
        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => new T();

        public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => CreateShaderAsset<T>(relativePath);

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => Task.FromResult(CreateShaderAsset<T>(relativePath));

        public void LogWarning(string message)
        {
        }

        private static T CreateShaderAsset<T>(string relativePath) where T : XRAsset, new()
        {
            if (typeof(T) == typeof(XRShader))
            {
                TextFile source = TextFile.FromText("void main() {}\n");
                source.FilePath = relativePath;

                XRShader shader = new(EShaderType.Fragment, source)
                {
                    FilePath = relativePath,
                };

                return (T)(XRAsset)shader;
            }

            return new T();
        }
    }
}
using System.Diagnostics;
using Assimp;
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

namespace XREngine.Benchmarks;

public static class GltfPhase0BaselineHarness
{
    public static int Run(string[] args)
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        string manifestPath = Path.Combine(workspaceRoot, GltfPhase0Decisions.CorpusManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string manifestDirectory = Path.GetDirectoryName(manifestPath) ?? throw new InvalidOperationException("Could not resolve the glTF manifest directory.");
        string performanceOutputPath = GetOption(args, "--perf-out")
            ?? Path.Combine(workspaceRoot, "Build", "Reports", "gltf-phase0-performance.json");
        int iterations = GetIntOption(args, "--iterations", 6);

        GltfCorpusManifest manifest = GltfCorpusManifest.Load(manifestPath);
        Console.WriteLine($"glTF phase 0 baseline report for {manifest.Entries.Count} manifest entries");

        IRuntimeShaderServices? previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new TestRuntimeShaderServices();

        try
        {
            foreach (GltfCorpusEntry entry in manifest.Entries)
            {
                if (entry.Availability != GltfCorpusAvailability.CheckedIn || !entry.ExpectedImportSuccess || string.IsNullOrWhiteSpace(entry.RelativePath) || string.IsNullOrWhiteSpace(entry.ExpectedSummaryPath))
                    continue;

                string assetPath = Path.Combine(workspaceRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                string summaryPath = Path.Combine(manifestDirectory, entry.ExpectedSummaryPath.Replace('/', Path.DirectorySeparatorChar));

                Stopwatch stopwatch = Stopwatch.StartNew();
                GltfGoldenSummary summary = CreateSummary(entry, assetPath);
                stopwatch.Stop();

                File.WriteAllText(summaryPath, System.Text.Json.JsonSerializer.Serialize(summary, GltfCorpusJson.SerializerOptions));

                Console.WriteLine($"- {entry.Id}: container={summary.Container}, nodes={summary.NodeCount}, meshes={summary.MeshCount}, materials={summary.MaterialCount}, animations={summary.AnimationCount}, skins={summary.SkinCount}, bones={summary.BoneCount}, morphs={summary.MorphTargetCount}, vertices={summary.TotalVertices}, triangles={summary.TotalTriangles}, depth={summary.MaxHierarchyDepth}, textures={summary.TextureCount}, elapsedMs={stopwatch.ElapsedMilliseconds}");
            }

            GltfPhase0PerformanceReport performanceReport = BuildPerformanceReport(manifest, workspaceRoot, iterations);
            string? outputDirectory = Path.GetDirectoryName(performanceOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(performanceOutputPath, System.Text.Json.JsonSerializer.Serialize(performanceReport, GltfCorpusJson.SerializerOptions));

            Console.WriteLine($"Performance report written to {performanceOutputPath}");
            foreach (GltfPhase0PerformanceResult result in performanceReport.Workloads)
            {
                Console.WriteLine($"- perf {result.AssetId}: nativeAvgMs={result.NativeAverageMilliseconds:F3}, assimpAvgMs={result.AssimpAverageMilliseconds:F3}, nativeAlloc={result.NativeAllocatedBytesPerIteration}, assimpAlloc={result.AssimpAllocatedBytesPerIteration}, nativeSpeedup={result.NativeSpeedupVsAssimp:F3}");
            }

            Console.WriteLine($"- parallel native: sequentialMs={performanceReport.Parallel.SequentialMilliseconds:F3}, parallelMs={performanceReport.Parallel.ParallelMilliseconds:F3}, speedup={performanceReport.Parallel.Speedup:F3}");
            Console.WriteLine($"Recommended baseline command: {GltfPhase0Decisions.BaselineHarnessCommand}");
            return 0;
        }
        finally
        {
            RuntimeShaderServices.Current = previousShaderServices;
        }
    }

    private static GltfGoldenSummary CreateSummary(GltfCorpusEntry entry, string assetPath)
    {
        FileInfo fileInfo = new(assetPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"glTF corpus file '{assetPath}' does not exist.", assetPath);

        using GltfAssetDocument document = GltfAssetDocument.Open(assetPath);
        ImportedSceneSummary summary = ImportAndSummarize(assetPath, GltfImportBackend.Auto);
        IReadOnlyList<string> usedExtensions = [.. document.Root.ExtensionsUsed.OrderBy(static extension => extension, StringComparer.Ordinal)];

        return new GltfGoldenSummary(
            AssetId: entry.Id,
            ImportSucceeded: true,
            Container: entry.Container,
            FileSizeBytes: fileInfo.Length,
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

    private static GltfPhase0PerformanceReport BuildPerformanceReport(GltfCorpusManifest manifest, string workspaceRoot, int iterations)
    {
        GltfCorpusEntry[] entries = manifest.Entries
            .Where(static entry => entry.Availability == GltfCorpusAvailability.CheckedIn && entry.ExpectedImportSuccess && entry.IncludeInPerformanceBaseline && !string.IsNullOrWhiteSpace(entry.RelativePath))
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

        List<GltfPhase0PerformanceResult> workloads = new(entries.Length);
        foreach (GltfCorpusEntry entry in entries)
        {
            string assetPath = Path.Combine(workspaceRoot, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
            workloads.Add(MeasurePerformance(entry.Id, assetPath, iterations));
        }

        GltfPhase0ParallelResult parallel = MeasureParallel(entries, workspaceRoot, iterations);
        return new GltfPhase0PerformanceReport(DateTime.UtcNow, iterations, workloads, parallel);
    }

    private static GltfPhase0PerformanceResult MeasurePerformance(string assetId, string assetPath, int iterations)
    {
        WarmUp(assetPath);

        BackendMeasurement nativeMeasurement = MeasureBackend(assetPath, GltfImportBackend.Native, iterations);
        BackendMeasurement assimpMeasurement = MeasureBackend(assetPath, GltfImportBackend.AssimpLegacy, iterations);
        double speedup = nativeMeasurement.AverageMilliseconds > 0.0
            ? assimpMeasurement.AverageMilliseconds / nativeMeasurement.AverageMilliseconds
            : 0.0;

        return new GltfPhase0PerformanceResult(
            AssetId: assetId,
            NativeAverageMilliseconds: nativeMeasurement.AverageMilliseconds,
            AssimpAverageMilliseconds: assimpMeasurement.AverageMilliseconds,
            NativeAllocatedBytesPerIteration: nativeMeasurement.AllocatedBytesPerIteration,
            AssimpAllocatedBytesPerIteration: assimpMeasurement.AllocatedBytesPerIteration,
            NativePeakWorkingSetBytes: nativeMeasurement.PeakWorkingSetBytes,
            AssimpPeakWorkingSetBytes: assimpMeasurement.PeakWorkingSetBytes,
            NativeSpeedupVsAssimp: speedup);
    }

    private static void WarmUp(string assetPath)
    {
        ImportAndSummarize(assetPath, GltfImportBackend.Native);
        ImportAndSummarize(assetPath, GltfImportBackend.AssimpLegacy);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static BackendMeasurement MeasureBackend(string assetPath, GltfImportBackend backend, int iterations)
    {
        Process process = Process.GetCurrentProcess();
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        long peakWorkingSet = process.WorkingSet64;
        Stopwatch stopwatch = Stopwatch.StartNew();

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            ImportAndSummarize(assetPath, backend);
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }

        stopwatch.Stop();
        long allocatedAfter = GC.GetTotalAllocatedBytes(true);
        return new BackendMeasurement(
            AverageMilliseconds: stopwatch.Elapsed.TotalMilliseconds / iterations,
            AllocatedBytesPerIteration: (allocatedAfter - allocatedBefore) / iterations,
            PeakWorkingSetBytes: peakWorkingSet);
    }

    private static GltfPhase0ParallelResult MeasureParallel(GltfCorpusEntry[] entries, string workspaceRoot, int iterations)
    {
        if (entries.Length == 0)
            return new GltfPhase0ParallelResult(0.0, 0.0, 0.0);

        string[] assetPaths = [.. entries.Select(entry => Path.Combine(workspaceRoot, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar)))];

        Stopwatch sequentialStopwatch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            foreach (string assetPath in assetPaths)
                ImportAndSummarize(assetPath, GltfImportBackend.Native);
        }
        sequentialStopwatch.Stop();

        Stopwatch parallelStopwatch = Stopwatch.StartNew();
        Parallel.For(
            0,
            iterations,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            },
            _ =>
            {
                foreach (string assetPath in assetPaths)
                    ImportAndSummarize(assetPath, GltfImportBackend.Native);
            });
        parallelStopwatch.Stop();

        double sequentialMilliseconds = sequentialStopwatch.Elapsed.TotalMilliseconds;
        double parallelMilliseconds = parallelStopwatch.Elapsed.TotalMilliseconds;
        double speedup = parallelMilliseconds > 0.0 ? sequentialMilliseconds / parallelMilliseconds : 0.0;
        return new GltfPhase0ParallelResult(sequentialMilliseconds, parallelMilliseconds, speedup);
    }

    private static ImportedSceneSummary ImportAndSummarize(string assetPath, GltfImportBackend backend)
    {
        using ModelImporter importer = CreateImporter(assetPath, backend);
        SceneNode? rootNode = importer.Import(PostProcessSteps.None, cancellationToken: default, onProgress: null);
        if (rootNode is null)
            throw new InvalidDataException($"ModelImporter did not return a root node for '{assetPath}' using backend {backend}.");

        return SummarizeImportedScene(rootNode);
    }

    private static ModelImporter CreateImporter(string assetPath, GltfImportBackend backend)
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

        ModelImporter importer = new(assetPath, onCompleted: null, materialFactory: CreateMaterialFromSlots)
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

        return importer;
    }

    private static ImportedSceneSummary SummarizeImportedScene(SceneNode rootNode)
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

    private static string ResolveWorkspaceRoot()
        => TryFindWorkspaceRoot(Directory.GetCurrentDirectory())
        ?? TryFindWorkspaceRoot(AppContext.BaseDirectory)
        ?? throw new DirectoryNotFoundException("Could not locate the workspace root from the current directory or benchmark base directory.");

    private static string? TryFindWorkspaceRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);
        if (!directory.Exists)
            return null;

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static string? GetOption(string[] args, string optionName)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(optionName, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }

    private static int GetIntOption(string[] args, string optionName, int defaultValue)
    {
        string? text = GetOption(args, optionName);
        return int.TryParse(text, out int parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private sealed record ImportedSceneSummary(
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

    private sealed record BackendMeasurement(
        double AverageMilliseconds,
        long AllocatedBytesPerIteration,
        long PeakWorkingSetBytes);

    private sealed class TestRuntimeShaderServices : IRuntimeShaderServices
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

public sealed record GltfPhase0PerformanceResult(
    string AssetId,
    double NativeAverageMilliseconds,
    double AssimpAverageMilliseconds,
    long NativeAllocatedBytesPerIteration,
    long AssimpAllocatedBytesPerIteration,
    long NativePeakWorkingSetBytes,
    long AssimpPeakWorkingSetBytes,
    double NativeSpeedupVsAssimp);

public sealed record GltfPhase0ParallelResult(
    double SequentialMilliseconds,
    double ParallelMilliseconds,
    double Speedup);

public sealed record GltfPhase0PerformanceReport(
    DateTime GeneratedUtc,
    int Iterations,
    IReadOnlyList<GltfPhase0PerformanceResult> Workloads,
    GltfPhase0ParallelResult Parallel);
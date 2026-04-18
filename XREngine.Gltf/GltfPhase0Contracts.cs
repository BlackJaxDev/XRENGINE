namespace XREngine.Gltf;

public enum GltfSupportLevel
{
    Deferred,
    Partial,
    Supported,
    UnsupportedWithDiagnostic,
}

public enum GltfFeatureArea
{
    SceneHierarchy,
    Transforms,
    Meshes,
    Materials,
    Samplers,
    ExternalBuffers,
    EmbeddedBinaryChunks,
    DataUris,
    BufferViewImages,
    SparseAccessors,
    Skinning,
    MorphTargets,
    AnimationClips,
    ExtrasAndUnknownExtensions,
    CompatibilityFallback,
}

public enum GltfCorpusAvailability
{
    CheckedIn,
    SyntheticMalformed,
    Planned,
}

public enum GltfCorpusScenario
{
    StaticScene,
    ExternalBuffers,
    ExternalImages,
    EmbeddedBinaryChunk,
    DataUris,
    BufferViewImages,
    SparseAccessors,
    SkinnedCharacter,
    MorphTargets,
    Animation,
    LargeScene,
    Malformed,
    MultipleUvSets,
    MultipleColorSets,
    DefaultSceneSelection,
    ExtrasAndUnknownExtensions,
    CompatibilityFallback,
}

public enum GltfBenchmarkMetric
{
    WallTimeMilliseconds,
    AllocatedBytes,
    PeakWorkingSetBytes,
    ParallelImportSpeedup,
}

public enum GltfAssetContainerKind
{
    Gltf,
    Glb,
}

public sealed record GltfFeatureSupport(
    GltfFeatureArea Feature,
    GltfSupportLevel Support,
    string Notes);

public sealed record GltfExtensionSupport(
    string ExtensionName,
    GltfSupportLevel Support,
    string Notes);

public sealed record GltfPipelineBoundary(
    string NativeProject,
    string ManagedProject,
    string EngineIntegrationProject,
    IReadOnlyList<string> NativeResponsibilities,
    IReadOnlyList<string> ManagedResponsibilities,
    IReadOnlyList<string> EngineResponsibilities);

public sealed record GltfResourceOwnershipPolicy(
    bool NativeLoadsExternalBuffers,
    bool ManagedLoadsImages,
    bool AllowsRemoteUris,
    string Notes);

public sealed record GltfValidationPolicy(
    bool EnableFastGltfValidateInDevelopmentBuilds,
    bool KeepCustomMemoryPoolEnabled,
    string Notes);

public static class GltfPhase0Decisions
{
    public const string CorpusManifestRelativePath = "XREngine.UnitTests/TestData/Gltf/gltf-corpus.manifest.json";
    public const string BaselineHarnessCommand = "dotnet run --project XREngine.Benchmarks -- --gltf-phase0-report";
    public const string RuntimeStagingRelativePath = "XREngine.Gltf/runtimes/win-x64/native";
    public const string DependencyMethod = "Vendored fastgltf v0.9.0 and simdjson v3.12.3 source snapshot under Build/Native/FastGltfBridge/vendor.";

    public static IReadOnlyList<GltfFeatureSupport> FeatureMatrix { get; } =
    [
        new(GltfFeatureArea.SceneHierarchy, GltfSupportLevel.Supported, "Imports the default scene into the existing SceneNode hierarchy."),
        new(GltfFeatureArea.Transforms, GltfSupportLevel.Supported, "Supports TRS-authored nodes and baked 4x4 node matrices."),
        new(GltfFeatureArea.Meshes, GltfSupportLevel.Supported, "Supports positions, normals, tangents, indices, and multiple primitives per mesh."),
        new(GltfFeatureArea.Materials, GltfSupportLevel.Supported, "Maps metallic-roughness, emissive, alpha mode, alpha cutoff, double-sidedness, and unlit materials into current material workflows."),
        new(GltfFeatureArea.Samplers, GltfSupportLevel.Supported, "Maps glTF sampler filter and wrap state onto imported XRTexture2D assets and material texture slots."),
        new(GltfFeatureArea.ExternalBuffers, GltfSupportLevel.Supported, "The native bridge loads local external buffers directly through fastgltf."),
        new(GltfFeatureArea.EmbeddedBinaryChunks, GltfSupportLevel.Supported, "Managed JSON parsing validates GLB chunk bounds and the native bridge reads embedded BIN payloads."),
        new(GltfFeatureArea.DataUris, GltfSupportLevel.Supported, "Managed image and JSON loaders decode data URIs deterministically and fail closed on malformed payloads."),
        new(GltfFeatureArea.BufferViewImages, GltfSupportLevel.Supported, "Images backed by buffer views follow a single buffer-view-to-byte[] ownership path."),
        new(GltfFeatureArea.SparseAccessors, GltfSupportLevel.Supported, "Accessor copies rely on fastgltf accessor tools, including sparse and normalized conversions."),
        new(GltfFeatureArea.Skinning, GltfSupportLevel.Supported, "Supports joint lists, inverse bind matrices, and normalized joint weights."),
        new(GltfFeatureArea.MorphTargets, GltfSupportLevel.Supported, "Supports morph target deltas, target names, default weights, and animated weights."),
        new(GltfFeatureArea.AnimationClips, GltfSupportLevel.Supported, "Supports translation, rotation, scale, and weight animation channels with linear, step, and cubic spline sampling."),
        new(GltfFeatureArea.ExtrasAndUnknownExtensions, GltfSupportLevel.Supported, "Managed JSON POCOs retain extras and unknown extension payloads as JsonElement dictionaries for future features."),
        new(GltfFeatureArea.CompatibilityFallback, GltfSupportLevel.Supported, "Auto uses the native path first and falls back to Assimp before scene publication when native import fails; Assimp remains the explicit escape hatch. Legacy YAML may still use the older AssimpLegacy spelling."),
    ];

    public static IReadOnlyList<GltfExtensionSupport> ExtensionMatrix { get; } =
    [
        new("KHR_materials_unlit", GltfSupportLevel.Supported, "Maps unlit glTF materials onto the engine's unlit forward material path."),
        new("KHR_mesh_quantization", GltfSupportLevel.Supported, "Handled via fastgltf accessor conversion during batched native copies."),
        new("EXT_meshopt_compression", GltfSupportLevel.Supported, "Handled by fastgltf when loading and copying accessor-backed data."),
        new("EXT_texture_webp", GltfSupportLevel.Supported, "Managed image decode keeps EXT_texture_webp textures on the existing texture loading path."),
        new("KHR_texture_transform", GltfSupportLevel.Partial, "texCoord overrides are honored. Offset, scale, and rotation are rejected with a diagnostic so results are never silently wrong."),
        new("KHR_texture_basisu", GltfSupportLevel.UnsupportedWithDiagnostic, "The native path rejects basisu/KTX2 textures with a diagnostic and points users to Assimp for compatibility."),
        new("KHR_draco_mesh_compression", GltfSupportLevel.UnsupportedWithDiagnostic, "The native path rejects Draco-compressed primitives with a diagnostic and points users to Assimp for compatibility."),
    ];

    public static IReadOnlyList<GltfCorpusScenario> RequiredCorpusCoverage { get; } =
    [
        GltfCorpusScenario.StaticScene,
        GltfCorpusScenario.ExternalBuffers,
        GltfCorpusScenario.DataUris,
        GltfCorpusScenario.BufferViewImages,
        GltfCorpusScenario.SparseAccessors,
        GltfCorpusScenario.SkinnedCharacter,
        GltfCorpusScenario.MorphTargets,
        GltfCorpusScenario.Animation,
        GltfCorpusScenario.LargeScene,
        GltfCorpusScenario.Malformed,
        GltfCorpusScenario.DefaultSceneSelection,
        GltfCorpusScenario.ExtrasAndUnknownExtensions,
    ];

    public static IReadOnlyList<GltfBenchmarkMetric> BenchmarkMetrics { get; } =
    [
        GltfBenchmarkMetric.WallTimeMilliseconds,
        GltfBenchmarkMetric.AllocatedBytes,
        GltfBenchmarkMetric.PeakWorkingSetBytes,
        GltfBenchmarkMetric.ParallelImportSpeedup,
    ];

    public static GltfPipelineBoundary PipelineBoundary { get; } = new(
        NativeProject: "Build/Native/FastGltfBridge",
        ManagedProject: "XREngine.Gltf",
        EngineIntegrationProject: "XRENGINE",
        NativeResponsibilities:
        [
            "Open and validate .gltf and .glb containers with fastgltf.",
            "Load local external buffers and expose coarse batched accessor and buffer-view copy APIs through a narrow C ABI.",
            "Keep parser ownership native and versionable without leaking C++ object graphs into managed code.",
        ],
        ManagedResponsibilities:
        [
            "Read glTF JSON and GLB JSON chunks with deterministic bounds validation.",
            "Retain extras and extension payloads in an engine-neutral document model.",
            "Own native-handle lifetime and batched accessor copy helpers.",
            "Share corpus manifest and golden-summary contracts across tests and benchmarks.",
        ],
        EngineResponsibilities:
        [
            "Route .gltf and .glb through the existing import policy surface.",
            "Map the managed glTF document into SceneNode, XRMesh, XRMaterial, XRTexture2D, skinning, and animation assets.",
            "Preserve async mesh processing, publication order, and remap workflows.",
            "Own compatibility fallback and user-facing diagnostics.",
        ]);

    public static GltfResourceOwnershipPolicy ResourceOwnership { get; } = new(
        NativeLoadsExternalBuffers: true,
        ManagedLoadsImages: true,
        AllowsRemoteUris: false,
        Notes: "The native bridge owns container parsing and local external-buffer loading. Managed code owns image bytes, data URI decode, and local-path validation so image decode stays on the existing engine texture path.");

    public static GltfValidationPolicy ValidationPolicy { get; } = new(
        EnableFastGltfValidateInDevelopmentBuilds: false,
        KeepCustomMemoryPoolEnabled: true,
        Notes: "Keep fastgltf's default custom memory pool enabled. The bridge does not opt into stricter fastgltf validation yet; malformed and unsupported cases are covered by deterministic corpus tests and importer diagnostics.");
}
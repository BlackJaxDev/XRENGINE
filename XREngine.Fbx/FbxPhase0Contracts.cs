namespace XREngine.Fbx;

public enum FbxTransportEncoding
{
    Unknown,
    Binary,
    Ascii,
}

public enum FbxSupportLevel
{
    Deferred,
    BestEffort,
    DebugOnly,
    TargetedV1,
}

public enum FbxFeatureArea
{
    StaticMeshes,
    NodeHierarchy,
    Materials,
    ExternalTextureReferences,
    EmbeddedTextures,
    Skeletons,
    Skinning,
    Blendshapes,
    AnimationCurves,
    AnimationStacks,
    Constraints,
    ExoticDeformers,
    LayeredMaterials,
}

public enum FbxCorpusAvailability
{
    CheckedIn,
    SyntheticMalformed,
    Planned,
}

public enum FbxCorpusScenario
{
    BinaryTokenizer,
    StaticScene,
    Materials,
    ExternalTextures,
    EmbeddedTextures,
    SkinnedCharacter,
    Blendshapes,
    Animation,
    LargeFile,
    Malformed,
    TransformSemantics,
}

public enum FbxCompressionBackend
{
    DotNetZLib,
}

public enum FbxBenchmarkMetric
{
    WallTimeMilliseconds,
    MegabytesPerSecond,
    AllocatedBytes,
    PeakWorkingSetBytes,
    ParallelFileSpeedup,
}

public sealed record FbxVersionSupport(
    string VersionLabel,
    FbxTransportEncoding Encoding,
    FbxSupportLevel ImportSupport,
    FbxSupportLevel ExportSupport,
    string Notes);

public sealed record FbxFeatureSupport(
    FbxFeatureArea Feature,
    FbxSupportLevel ImportSupport,
    FbxSupportLevel ExportSupport,
    string Notes);

public sealed record FbxPipelineBoundary(
    string CoreProject,
    string EngineIntegrationProject,
    IReadOnlyList<string> CoreResponsibilities,
    IReadOnlyList<string> EngineIntegrationResponsibilities);

public sealed record FbxCompressionPolicy(
    FbxCompressionBackend Backend,
    bool RequiresNewDependency,
    string Notes);

public static class FbxPhase0Decisions
{
    public const string CorpusManifestRelativePath = "XREngine.UnitTests/TestData/Fbx/fbx-corpus.manifest.json";
    public const string BaselineHarnessCommand = "dotnet run --project XREngine.Benchmarks -- --fbx-phase0-report";

    public static IReadOnlyList<FbxVersionSupport> VersionMatrix { get; } =
    [
        new("7400", FbxTransportEncoding.Binary, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Primary binary interchange target for the native parser and writer."),
        new("7500", FbxTransportEncoding.Binary, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Required to cover 64-bit node record fields in the native path."),
        new("Pre-7400 binary", FbxTransportEncoding.Binary, FbxSupportLevel.BestEffort, FbxSupportLevel.Deferred, "Useful validation input, but not a blocking v1 export target."),
        new("ASCII 7.x", FbxTransportEncoding.Ascii, FbxSupportLevel.BestEffort, FbxSupportLevel.Deferred, "Keep as debug and interoperability coverage, not the primary performance path."),
    ];

    public static IReadOnlyList<FbxFeatureSupport> FeatureMatrix { get; } =
    [
        new(FbxFeatureArea.StaticMeshes, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Required v1 path."),
        new(FbxFeatureArea.NodeHierarchy, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Required v1 path."),
        new(FbxFeatureArea.Materials, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Required to replace current model import flow."),
        new(FbxFeatureArea.ExternalTextureReferences, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Must preserve current remap workflows."),
        new(FbxFeatureArea.EmbeddedTextures, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Required in scope even if corpus coverage is still being expanded."),
        new(FbxFeatureArea.Skeletons, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Required for skinned-model parity."),
        new(FbxFeatureArea.Skinning, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Required for skinned-model parity."),
        new(FbxFeatureArea.Blendshapes, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Explicit v1 scope item."),
        new(FbxFeatureArea.AnimationCurves, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Explicit v1 scope item."),
        new(FbxFeatureArea.AnimationStacks, FbxSupportLevel.TargetedV1, FbxSupportLevel.TargetedV1, "Explicit v1 scope item."),
        new(FbxFeatureArea.Constraints, FbxSupportLevel.Deferred, FbxSupportLevel.Deferred, "Do not block the initial native path on rare constraint semantics."),
        new(FbxFeatureArea.ExoticDeformers, FbxSupportLevel.Deferred, FbxSupportLevel.Deferred, "Defer until the core mesh/skin/blendshape path is stable."),
        new(FbxFeatureArea.LayeredMaterials, FbxSupportLevel.Deferred, FbxSupportLevel.Deferred, "Defer until basic material import/export is hardened."),
    ];

    public static IReadOnlyList<FbxCorpusScenario> RequiredCorpusCoverage { get; } =
    [
        FbxCorpusScenario.BinaryTokenizer,
        FbxCorpusScenario.StaticScene,
        FbxCorpusScenario.EmbeddedTextures,
        FbxCorpusScenario.SkinnedCharacter,
        FbxCorpusScenario.Blendshapes,
        FbxCorpusScenario.Animation,
        FbxCorpusScenario.Malformed,
        FbxCorpusScenario.TransformSemantics,
    ];

    public static IReadOnlyList<FbxBenchmarkMetric> BenchmarkMetrics { get; } =
    [
        FbxBenchmarkMetric.WallTimeMilliseconds,
        FbxBenchmarkMetric.MegabytesPerSecond,
        FbxBenchmarkMetric.AllocatedBytes,
        FbxBenchmarkMetric.PeakWorkingSetBytes,
        FbxBenchmarkMetric.ParallelFileSpeedup,
    ];

    public static FbxPipelineBoundary PipelineBoundary { get; } = new(
        CoreProject: "XREngine.Fbx",
        EngineIntegrationProject: "XRENGINE",
        CoreResponsibilities:
        [
            "Binary and ASCII container readers and writers.",
            "Typed FBX semantic graph for Objects, Connections, Definitions, Takes, and global settings.",
            "Engine-neutral intermediate model for nodes, meshes, materials, textures, skinning, blendshapes, and animation.",
            "Corpus manifest and golden-summary contracts shared by tests and benchmarks.",
        ],
        EngineIntegrationResponsibilities:
        [
            "Dispatch .fbx imports from the existing third-party import workflow.",
            "Map intermediate FBX scene data into SceneNode, XRMesh, XRMaterial, XRTexture, and animation assets.",
            "Preserve async mesh processing, publication, remap persistence, and editor-facing workflows.",
            "Own engine-specific logging, cancellation, and job-system orchestration.",
        ]);

    public static FbxCompressionPolicy CompressionPolicy { get; } = new(
        FbxCompressionBackend.DotNetZLib,
        RequiresNewDependency: false,
        Notes: "Use the built-in .NET zlib/deflate stack first; only add a faster dependency if profiling proves it is a bottleneck and the license is clean.");
}
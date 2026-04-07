using Assimp;
using System.ComponentModel;
using XREngine.Data;
using XREngine.Fbx;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Models;

public enum EDiffuseAlphaMode
{
    Auto,
    Opaque,
    Masked,
    Blended,
}

public enum EOpacityMapMode
{
    Auto,
    Masked,
    Blended,
}

public enum FbxImportBackend
{
    Auto,
    Native,
    AssimpLegacy,
    Assimp = AssimpLegacy,
}

public sealed class ModelImportOptions : IXR3rdPartyImportOptions
{
    /// <summary>
    /// Backwards-compatibility: older cached YAML stored the combined flags under "PostProcessSteps".
    /// This setter-only property allows deserialization without re-serializing it.
    /// </summary>
    [Browsable(false)]
    [YamlMember(Alias = "PostProcessSteps")]
    public PostProcessSteps LegacyPostProcessSteps
    {
        set => _postProcessSteps = value;
    }

    private PostProcessSteps _postProcessSteps =
        PostProcessSteps.Triangulate |
        PostProcessSteps.JoinIdenticalVertices |
        PostProcessSteps.GenerateNormals |
        PostProcessSteps.CalculateTangentSpace |
        PostProcessSteps.OptimizeGraph |
        PostProcessSteps.OptimizeMeshes |
        PostProcessSteps.SortByPrimitiveType |
        PostProcessSteps.ImproveCacheLocality |
        PostProcessSteps.GenerateBoundingBoxes |
        PostProcessSteps.FlipUVs;

    [Browsable(false)]
    [YamlIgnore]
    public PostProcessSteps PostProcessSteps => _postProcessSteps;

    private bool GetFlag(PostProcessSteps flag) => (_postProcessSteps & flag) == flag;

    private void SetFlag(PostProcessSteps flag, bool enabled)
    {
        if (enabled)
            _postProcessSteps |= flag;
        else
            _postProcessSteps &= ~flag;
    }

    public bool Triangulate
    {
        get => GetFlag(PostProcessSteps.Triangulate);
        set => SetFlag(PostProcessSteps.Triangulate, value);
    }

    public bool GenerateNormals
    {
        get => GetFlag(PostProcessSteps.GenerateNormals);
        set => SetFlag(PostProcessSteps.GenerateNormals, value);
    }

    public bool CalculateTangentSpace
    {
        get => GetFlag(PostProcessSteps.CalculateTangentSpace);
        set => SetFlag(PostProcessSteps.CalculateTangentSpace, value);
    }

    public bool JoinIdenticalVertices
    {
        get => GetFlag(PostProcessSteps.JoinIdenticalVertices);
        set => SetFlag(PostProcessSteps.JoinIdenticalVertices, value);
    }

    public bool OptimizeGraph
    {
        get => GetFlag(PostProcessSteps.OptimizeGraph);
        set => SetFlag(PostProcessSteps.OptimizeGraph, value);
    }

    public bool OptimizeMeshes
    {
        get => GetFlag(PostProcessSteps.OptimizeMeshes);
        set => SetFlag(PostProcessSteps.OptimizeMeshes, value);
    }

    public bool SortByPrimitiveType
    {
        get => GetFlag(PostProcessSteps.SortByPrimitiveType);
        set => SetFlag(PostProcessSteps.SortByPrimitiveType, value);
    }

    public bool ImproveCacheLocality
    {
        get => GetFlag(PostProcessSteps.ImproveCacheLocality);
        set => SetFlag(PostProcessSteps.ImproveCacheLocality, value);
    }

    public bool GenerateBoundingBoxes
    {
        get => GetFlag(PostProcessSteps.GenerateBoundingBoxes);
        set => SetFlag(PostProcessSteps.GenerateBoundingBoxes, value);
    }

    /// <summary>
    /// Selects how .fbx files are imported. Auto uses the native importer by default,
    /// while AssimpLegacy preserves the older compatibility path.
    /// </summary>
    public FbxImportBackend FbxBackend { get; set; } = FbxImportBackend.Auto;

    /// <summary>
    /// Controls whether FBX pivots stay explicit in the imported transform semantics
    /// or are baked into the local transform.
    /// </summary>
    public FbxPivotImportPolicy FbxPivotPolicy { get; set; } = FbxPivotImportPolicy.PreservePivotSemantics;

    /// <summary>
    /// When using the legacy Assimp FBX backend, collapse generated helper nodes
    /// back into the authored hierarchy when possible.
    /// </summary>
    public bool CollapseGeneratedFbxHelperNodes { get; set; } = true;

    /// <summary>
    /// Uniform scale conversion applied during import.
    /// </summary>
    public float ScaleConversion { get; set; } = 1.0f;

    /// <summary>
    /// If true, treat the source file as Z-up (common in some DCCs).
    /// </summary>
    public bool ZUp { get; set; } = false;

    /// <summary>
    /// Controls how diffuse/base-color alpha should be interpreted during import.
    /// </summary>
    public EDiffuseAlphaMode DiffuseAlphaMode { get; set; } = EDiffuseAlphaMode.Auto;

    /// <summary>
    /// Controls how explicit opacity maps should be interpreted during import.
    /// </summary>
    public EOpacityMapMode OpacityMapMode { get; set; } = EOpacityMapMode.Auto;

    /// <summary>
    /// Enables Assimp multithreading when the Assimp backend is used.
    /// </summary>
    public bool MultiThread { get; set; } = true;

    /// <summary>
    /// Whether to process meshes asynchronously.
    /// Null means "inherit <see cref="Engine.Rendering.Settings.ProcessMeshImportsAsynchronously"/>".
    /// </summary>
    public bool? ProcessMeshesAsynchronously { get; set; } = null;

    /// <summary>
    /// When true, mesh renderers created from imported submeshes opt into asynchronous GPU-side generation.
    /// This only affects imported model renderers and leaves the global XRMeshRenderer default unchanged.
    /// </summary>
    public bool GenerateMeshRenderersAsync { get; set; } = true;

    /// <summary>
    /// When true, each imported submesh is assigned to its own <see cref="Components.Scene.Mesh.ModelComponent"/>
    /// instead of grouping all submeshes from the same source node into a single model component.
    /// </summary>
    public bool SplitSubmeshesIntoSeparateModelComponents { get; set; } = false;

    /// <summary>
    /// When async mesh import is enabled, controls whether imported submeshes are published
    /// to the scene in one batch at the end or streamed in as they become ready.
    /// </summary>
    public bool BatchSubmeshAddsDuringAsyncImport { get; set; } = true;

    /// <summary>
    /// Maps original imported texture file paths to finalized texture assets.
    /// </summary>
    public Dictionary<string, XRTexture2D?>? TextureRemap { get; set; }

    /// <summary>
    /// Maps imported material names to finalized material assets.
    /// </summary>
    public Dictionary<string, XRMaterial?>? MaterialRemap { get; set; }

    private Dictionary<string, string>? _legacyTexturePathRemap;
    private Dictionary<string, string>? _legacyMaterialNameRemap;

    /// <summary>
    /// Backwards-compatibility: older cached YAML stored texture remaps as replacement paths.
    /// Preserve those entries so reimport still works until the asset remaps are resaved.
    /// </summary>
    [Browsable(false)]
    [YamlMember(Alias = "TexturePathRemap")]
    public Dictionary<string, string>? LegacyTexturePathRemap
    {
        set => _legacyTexturePathRemap = value;
    }

    /// <summary>
    /// Backwards-compatibility: older cached YAML stored material remaps as replacement paths.
    /// Preserve those entries so reimport still works until the asset remaps are resaved.
    /// </summary>
    [Browsable(false)]
    [YamlMember(Alias = "MaterialNameRemap")]
    public Dictionary<string, string>? LegacyMaterialNameRemap
    {
        set => _legacyMaterialNameRemap = value;
    }

    /// <summary>
    /// Backwards-compatibility: older cached YAML stored the FBX pivot behavior under
    /// the Assimp-era "PreservePivots" boolean.
    /// </summary>
    [Browsable(false)]
    [YamlMember(Alias = "PreservePivots")]
    public bool LegacyPreservePivots
    {
        set => FbxPivotPolicy = value
            ? FbxPivotImportPolicy.PreservePivotSemantics
            : FbxPivotImportPolicy.BakeIntoLocalTransform;
    }

    /// <summary>
    /// Backwards-compatibility: older cached YAML stored legacy FBX helper-node cleanup under
    /// the Assimp-specific "RemoveAssimpFBXNodes" boolean.
    /// </summary>
    [Browsable(false)]
    [YamlMember(Alias = "RemoveAssimpFBXNodes")]
    public bool LegacyRemoveAssimpFbxNodes
    {
        set => CollapseGeneratedFbxHelperNodes = value;
    }

    [Browsable(false)]
    [YamlIgnore]
    public IReadOnlyDictionary<string, string>? LegacyTexturePathRemapValues => _legacyTexturePathRemap;

    [Browsable(false)]
    [YamlIgnore]
    public IReadOnlyDictionary<string, string>? LegacyMaterialNameRemapValues => _legacyMaterialNameRemap;
}

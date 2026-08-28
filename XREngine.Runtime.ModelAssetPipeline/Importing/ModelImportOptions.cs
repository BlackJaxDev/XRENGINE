using System;
using System.ComponentModel;
using XREngine.Data;
using XREngine.Core.Attributes;
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
    Assimp,

    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    AssimpLegacy = Assimp,
}

public enum GltfImportBackend
{
    Auto,
    Native,
    Assimp,

    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    AssimpLegacy = Assimp,
}

[XRTypeRedirect("XREngine.Rendering.Models.ModelImportOptions")]
public sealed class ModelImportOptions : IXR3rdPartyImportOptions
{
    private ModelCookSettings _cookSettings = new();

    /// <summary>
    /// Versioned import-time geometry cook policy included in model-cache identity.
    /// </summary>
    public ModelCookSettings CookSettings
    {
        get => _cookSettings;
        set => _cookSettings = value ?? new ModelCookSettings();
    }
    public Caching.ModelCookOverrideSnapshot CookOverrides { get; set; } = Caching.ModelCookOverrideSnapshot.Empty;

    /// <summary>
    /// Defers derived meshlet cooking to a caller that performs additional model
    /// normalization after the backend returns. Such callers must invoke
    /// <see cref="Rendering.Models.ModelImportMeshletCooker"/> before publication.
    /// </summary>
    public bool DeferMeshletCookingUntilPostNormalization { get; set; }

    /// <summary>
    /// Optional Unity project or Assets folder selected for external .prefab conversion.
    /// Model importers ignore this value.
    /// </summary>
    public string? SourceProjectRootOverride { get; set; }

    /// <summary>
    /// Backwards-compatibility: older cached YAML stored the combined flags under "PostProcessSteps".
    /// This setter-only property allows deserialization without re-serializing it.
    /// </summary>
    [Browsable(false)]
    [YamlMember(Alias = "PostProcessSteps")]
    public ModelImportSteps LegacyPostProcessSteps
    {
        set => _postProcessSteps = value;
    }

    private ModelImportSteps _postProcessSteps =
        ModelImportSteps.Triangulate |
        ModelImportSteps.JoinIdenticalVertices |
        ModelImportSteps.GenerateNormals |
        ModelImportSteps.CalculateTangentSpace |
        ModelImportSteps.OptimizeGraph |
        ModelImportSteps.OptimizeMeshes |
        ModelImportSteps.SortByPrimitiveType |
        ModelImportSteps.ImproveCacheLocality |
        ModelImportSteps.GenerateBoundingBoxes |
        ModelImportSteps.FlipUVs;

    [Browsable(false)]
    [YamlIgnore]
    public ModelImportSteps ImportSteps => _postProcessSteps;

    private bool GetFlag(ModelImportSteps flag) => (_postProcessSteps & flag) == flag;

    private void SetFlag(ModelImportSteps flag, bool enabled)
    {
        if (enabled)
            _postProcessSteps |= flag;
        else
            _postProcessSteps &= ~flag;
    }

    public bool Triangulate
    {
        get => GetFlag(ModelImportSteps.Triangulate);
        set => SetFlag(ModelImportSteps.Triangulate, value);
    }

    public bool GenerateNormals
    {
        get => GetFlag(ModelImportSteps.GenerateNormals);
        set => SetFlag(ModelImportSteps.GenerateNormals, value);
    }

    public bool CalculateTangentSpace
    {
        get => GetFlag(ModelImportSteps.CalculateTangentSpace);
        set => SetFlag(ModelImportSteps.CalculateTangentSpace, value);
    }

    public bool JoinIdenticalVertices
    {
        get => GetFlag(ModelImportSteps.JoinIdenticalVertices);
        set => SetFlag(ModelImportSteps.JoinIdenticalVertices, value);
    }

    public bool OptimizeGraph
    {
        get => GetFlag(ModelImportSteps.OptimizeGraph);
        set => SetFlag(ModelImportSteps.OptimizeGraph, value);
    }

    public bool OptimizeMeshes
    {
        get => GetFlag(ModelImportSteps.OptimizeMeshes);
        set => SetFlag(ModelImportSteps.OptimizeMeshes, value);
    }

    public bool SortByPrimitiveType
    {
        get => GetFlag(ModelImportSteps.SortByPrimitiveType);
        set => SetFlag(ModelImportSteps.SortByPrimitiveType, value);
    }

    public bool ImproveCacheLocality
    {
        get => GetFlag(ModelImportSteps.ImproveCacheLocality);
        set => SetFlag(ModelImportSteps.ImproveCacheLocality, value);
    }

    public bool GenerateBoundingBoxes
    {
        get => GetFlag(ModelImportSteps.GenerateBoundingBoxes);
        set => SetFlag(ModelImportSteps.GenerateBoundingBoxes, value);
    }

    /// <summary>
    /// Reflects imported geometry and hierarchy transforms across the Z axis.
    /// Use this when converting a +Z-forward right-handed source into XRENGINE's
    /// -Z-forward left-handed coordinate system.
    /// </summary>
    public bool MakeLeftHanded
    {
        get => GetFlag(ModelImportSteps.MakeLeftHanded);
        set => SetFlag(ModelImportSteps.MakeLeftHanded, value);
    }

    /// <summary>
    /// Reverses primitive winding. Handedness reflections should normally enable
    /// this together with <see cref="MakeLeftHanded"/> to preserve front faces.
    /// </summary>
    public bool FlipWindingOrder
    {
        get => GetFlag(ModelImportSteps.FlipWindingOrder);
        set => SetFlag(ModelImportSteps.FlipWindingOrder, value);
    }

    /// <summary>
    /// Selects how .fbx files are imported. Auto uses the native importer by default,
    /// while Assimp preserves the older compatibility path. Legacy YAML may still spell
    /// this value as AssimpLegacy.
    /// </summary>
    public FbxImportBackend FbxBackend { get; set; } = FbxImportBackend.Auto;

    /// <summary>
    /// Selects how .gltf and .glb files are imported. Auto uses the native importer by default,
    /// while Assimp preserves the older compatibility path. Legacy YAML may still spell
    /// this value as AssimpLegacy.
    /// </summary>
    public GltfImportBackend GltfBackend { get; set; } = GltfImportBackend.Auto;

    /// <summary>
    /// Controls whether FBX pivots stay explicit in the imported transform semantics
    /// or are baked into the local transform.
    /// </summary>
    public FbxPivotImportPolicy FbxPivotPolicy { get; set; } = FbxPivotImportPolicy.PreservePivotSemantics;

    /// <summary>
    /// When using the legacy Assimp FBX backend, collapse generated helper nodes
    /// back into the authored hierarchy when possible.
    /// </summary>
    [DefaultValue(true)]
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
    [DefaultValue(true)]
    public bool MultiThread { get; set; } = true;

    /// <summary>
    /// Maximum worker parallelism used by the native FBX mesh build stage.
    /// Zero means auto: use a conservative editor-friendly cap that avoids saturating the UI/render thread.
    /// </summary>
    public int NativeFbxMeshBuildMaxDegreeOfParallelism { get; set; } = 0;

    /// <summary>
    /// Whether to process meshes asynchronously.
    /// Null means "inherit <see cref="RuntimeEngine.Rendering.Settings.ProcessMeshImportsAsynchronously"/>".
    /// </summary>
    public bool? ProcessMeshesAsynchronously { get; set; } = null;

    /// <summary>
    /// When true, mesh renderers created from imported submeshes opt into asynchronous GPU-side generation.
    /// This only affects imported model renderers and leaves the global XRMeshRenderer default unchanged.
    /// </summary>
    [DefaultValue(true)]
    public bool GenerateMeshRenderersAsync { get; set; } = true;

    /// <summary>
    /// When true, each imported submesh is assigned to its own <see cref="Components.Scene.Mesh.ModelComponent"/>
    /// instead of grouping all submeshes from the same source node into a single model component.
    /// </summary>
    public bool SplitSubmeshesIntoSeparateModelComponents { get; set; } = false;

    /// <summary>
    /// When true, split imported submeshes are placed on individual child scene nodes
    /// instead of attaching all generated model components to the source node.
    /// Implies <see cref="SplitSubmeshesIntoSeparateModelComponents"/>.
    /// </summary>
    public bool GenerateSceneNodesPerSubmesh { get; set; } = false;

    /// <summary>
    /// When true, imported triangle submeshes are analyzed for disconnected geometric islands
    /// and each island is emitted as a separate submesh with the original material.
    /// </summary>
    public bool SeparateMeshIslands { get; set; } = false;

    /// <summary>
    /// When greater than zero, imported triangle submeshes are recursively partitioned
    /// into spatially coherent draw units containing no more than this many triangles.
    /// Use this for CPU query occlusion when source meshes span large scene regions.
    /// </summary>
    public int SpatialPartitionMaxTriangles { get; set; } = 0;

    /// <summary>
    /// When async mesh import is enabled, controls whether imported submeshes are published
    /// to the scene in one batch at the end or streamed in as they become ready.
    /// </summary>
    [DefaultValue(true)]
    public bool BatchSubmeshAddsDuringAsyncImport { get; set; } = true;

    /// <summary>
    /// Runtime-only progress callback used by editor/import jobs. This is intentionally
    /// not serialized into import settings.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public Action<float>? ProgressCallback { get; set; }

    /// <summary>
    /// Maps original imported texture file paths to finalized texture assets.
    /// </summary>
    public Dictionary<string, XRTexture2D?>? TextureRemap { get; set; }

    /// <summary>
    /// Optional additional directories that the importer will search recursively by file name
    /// when an authored texture path cannot be resolved relative to the model.
    /// </summary>
    public string[] TextureLoadDirSearchPaths { get; set; } = [];

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

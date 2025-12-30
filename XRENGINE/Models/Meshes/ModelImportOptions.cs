using Assimp;
using System.ComponentModel;
using XREngine.Data;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Models;

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
    /// FBX-specific: preserve pivot transforms.
    /// </summary>
    public bool PreservePivots { get; set; } = true;

    /// <summary>
    /// FBX-specific: collapse Assimp-generated helper nodes.
    /// </summary>
    public bool RemoveAssimpFBXNodes { get; set; } = true;

    /// <summary>
    /// Uniform scale conversion applied by Assimp.
    /// </summary>
    public float ScaleConversion { get; set; } = 1.0f;

    /// <summary>
    /// If true, treat the source file as Z-up (common in some DCCs).
    /// </summary>
    public bool ZUp { get; set; } = false;

    /// <summary>
    /// Enables Assimp's multithreading option.
    /// </summary>
    public bool MultiThread { get; set; } = true;

    /// <summary>
    /// Whether to process meshes asynchronously via the engine setting.
    /// For asset import/reimport, you typically want this false to keep import deterministic.
    /// </summary>
    public bool ProcessMeshesAsynchronously { get; set; } = false;

    /// <summary>
    /// Maps original texture file paths to new paths.
    /// </summary>
    public Dictionary<string, string>? TexturePathRemap { get; set; }

    /// <summary>
    /// Maps original material names to paths of new materials.
    /// </summary>
    public Dictionary<string, string>? MaterialNameRemap { get; set; }
}

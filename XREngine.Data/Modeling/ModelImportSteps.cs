namespace XREngine.Rendering.Models;

/// <summary>
/// Format-neutral model preprocessing operations. Importer owners map these values
/// to their selected producer without exposing producer-specific flags to hosts.
/// Numeric values intentionally preserve the legacy serialized flag layout.
/// </summary>
[Flags]
public enum ModelImportSteps : uint
{
    None = 0,
    CalculateTangentSpace = 1u << 0,
    JoinIdenticalVertices = 1u << 1,
    MakeLeftHanded = 1u << 2,
    Triangulate = 1u << 3,
    RemoveComponent = 1u << 4,
    GenerateNormals = 1u << 5,
    GenerateSmoothNormals = 1u << 6,
    SplitLargeMeshes = 1u << 7,
    PreTransformVertices = 1u << 8,
    LimitBoneWeights = 1u << 9,
    ValidateDataStructure = 1u << 10,
    ImproveCacheLocality = 1u << 11,
    RemoveRedundantMaterials = 1u << 12,
    FixInFacingNormals = 1u << 13,
    SortByPrimitiveType = 1u << 15,
    FindDegenerates = 1u << 16,
    FindInvalidData = 1u << 17,
    GenerateUVCoords = 1u << 18,
    TransformUVCoords = 1u << 19,
    FindInstances = 1u << 20,
    OptimizeMeshes = 1u << 21,
    OptimizeGraph = 1u << 22,
    FlipUVs = 1u << 23,
    FlipWindingOrder = 1u << 24,
    SplitByBoneCount = 1u << 25,
    Debone = 1u << 26,
    GlobalScale = 1u << 27,
    EmbedTextures = 1u << 28,
    ForceGenerateNormals = 1u << 29,
    DropNormals = 1u << 30,
    GenerateBoundingBoxes = 1u << 31,
}

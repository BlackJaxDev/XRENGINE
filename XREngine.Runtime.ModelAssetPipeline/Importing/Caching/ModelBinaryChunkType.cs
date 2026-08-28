namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Stable chunk IDs for model binary cache schema v1.
/// </summary>
internal enum ModelBinaryChunkType : uint
{
    Dependencies = 1,
    Manifest = 2,
    PrefabGraph = 3,
    ComponentDirectory = 4,
    ComponentPayloads = 5,
    Models = 6,
    SubMeshes = 7,
    MeshDirectory = 8,
    MeshCoreStreams = 9,
    Skinning = 10,
    Skeletons = 11,
    MorphTargets = 12,
    LodTables = 13,
    Meshlets = 14,
    Materials = 15,
    TextureReferences = 16,
    AnimationReferences = 17,
    ImportedEntityTable = 18,
    ColliderHints = 19,
    Diagnostics = 20,
}

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// V1 payload versions reserved for each model-container chunk contract.
/// </summary>
public static class ModelBinaryChunkVersions
{
    public const uint Dependencies = 1;
    public const uint Manifest = 1;
    public const uint PrefabGraph = 1;
    public const uint ComponentDirectory = 1;
    public const uint ComponentPayloads = 1;
    public const uint Models = 1;
    public const uint SubMeshes = 1;
    public const uint MeshDirectory = 1;
    public const uint MeshCoreStreams = 1;
    public const uint Skinning = 1;
    public const uint Skeletons = 1;
    public const uint MorphTargets = 1;
    public const uint LodTables = 1;
    public const uint Meshlets = 1;
    public const uint Materials = 1;
    public const uint TextureReferences = 1;
    public const uint AnimationReferences = 1;
    public const uint ImportedEntityTable = 1;
    public const uint ColliderHints = 1;
    public const uint Diagnostics = 1;
}

using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>Directory-owned reference used to collect meshlet payloads deterministically.</summary>
internal readonly record struct ModelBinaryMeshletSourceReference(
    string ModelIdentity,
    uint SubMeshIndex,
    uint LodIndex,
    MeshletPayload Payload);

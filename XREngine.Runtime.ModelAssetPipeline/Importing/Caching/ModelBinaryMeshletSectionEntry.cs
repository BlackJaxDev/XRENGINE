using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>One model-container meshlet payload and its stable mesh identity.</summary>
internal sealed record ModelBinaryMeshletSectionEntry(ModelBinaryMeshletSectionKey Key, MeshletPayload Payload);

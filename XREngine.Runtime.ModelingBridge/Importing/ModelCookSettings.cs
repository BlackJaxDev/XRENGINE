using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Caching;

namespace XREngine.Rendering.Models;

/// <summary>
/// Versioned model-level defaults for import-time LOD and meshlet cooking.
/// </summary>
public sealed class ModelCookSettings
{
    public uint PolicyVersion { get; set; } = ModelBinaryCacheVersions.CookPolicy;
    public MeshletGenerationSettings Meshlets { get; set; } = new();
    public MeshLodGenerationSettings Lods { get; set; } = new();
    public ModelCookRepairPolicy RepairPolicy { get; set; } = ModelCookRepairPolicy.RepairOptionalDerivedData;
}

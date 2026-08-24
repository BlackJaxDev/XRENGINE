using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Caching;

namespace XREngine.Rendering.Models;

/// <summary>
/// Versioned model-level defaults for import-time LOD and meshlet cooking.
/// </summary>
public sealed class ModelCookSettings
{
    public uint PolicyVersion { get; set; } = ModelBinaryCacheVersions.CookPolicy;
    /// <summary>
    /// Imported triangle meshes are cooked for the portable mesh-shader profile by
    /// default. This is deliberately a model-import policy rather than changing the
    /// default for procedural/runtime-created meshes.
    /// </summary>
    public MeshletGenerationSettings Meshlets { get; set; } = new() { Enabled = true };
    public MeshLodGenerationSettings Lods { get; set; } = new();
    public ModelCookRepairPolicy RepairPolicy { get; set; } = ModelCookRepairPolicy.RepairOptionalDerivedData;
}

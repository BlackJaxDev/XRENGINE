using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Records the inputs of one successful committed-binding check of a material.
/// While every recorded value is the same, the check would give the same result,
/// so the scene publisher skips it. The witness holds the material and layout
/// objects, the material versions, the texture-source version that bound
/// textures advance (<see cref="XRMaterialBase.TextureSourceVersion"/>), a hash
/// of the per-texture content counters, the resource-publisher commit
/// generation, the material database generations, because a publication
/// can commit a material row again without any source change, and a hash of the
/// committed bindings and surfaces that the check compared, because one material
/// can be checked against more than one committed row.
/// </summary>
internal readonly record struct AdvancedMaterialSourceWitness(
    XRMaterial? Material,
    MaterialBindingLayout Layout,
    ulong BindingLayoutVersion,
    ulong BindingValueVersion,
    ulong BindingResourceVersion,
    long ShaderStateRevision,
    long UberStateRevision,
    ulong TextureSourceVersion,
    ulong TextureCounterHash,
    ulong ResourceCommitGeneration,
    AdvancedMaterialDatabaseGenerations MaterialDatabaseGenerations,
    ulong CommittedInputHash);

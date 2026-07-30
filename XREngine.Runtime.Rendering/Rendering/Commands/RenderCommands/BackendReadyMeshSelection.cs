using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable pure-data selection captured for a mesh command on the
/// collect-visible producer. Backend handles remain render-thread-owned.
/// </summary>
public readonly record struct BackendReadyMeshSelection(
    int RenderPass,
    uint StableQueryKey,
    IRenderCommandMesh Command,
    XRMeshRenderer? Mesh,
    XRMaterial? Material,
    RenderingParameters? RenderOptions,
    uint Instances,
    bool ForceCpuRendering,
    bool ExcludeFromGpuIndirect,
    ulong MaterialBindingLayoutVersion,
    long MaterialShaderStateRevision,
    long MaterialUberStateRevision,
    ulong DependencySignature);

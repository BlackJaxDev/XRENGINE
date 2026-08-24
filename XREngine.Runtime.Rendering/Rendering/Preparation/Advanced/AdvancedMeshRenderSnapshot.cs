using System.Numerics;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Immutable render-buffer view of a managed mesh command used during live
/// advanced scene extraction.
/// </summary>
public readonly record struct AdvancedMeshRenderSnapshot(
    XRMeshRenderer? Renderer,
    Matrix4x4 CurrentWorld,
    Matrix4x4 PreviousWorld,
    uint Instances,
    bool WorldMatrixIsModelMatrix,
    bool ForceCpuRendering,
    XRMaterial? MaterialOverride,
    RenderingParameters? RenderOptionsOverride);

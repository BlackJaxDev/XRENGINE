using System.Numerics;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Mesh inputs captured at a swap boundary for legacy GPU scene conversion.
/// The command remains the owner and dictionary key; callback mutations apply
/// at the next boundary instead of mixing two states in one publication.
/// </summary>
internal readonly record struct GpuSceneMeshCommandSnapshot(
    XRMeshRenderer? Renderer,
    Matrix4x4 WorldMatrix,
    bool WorldMatrixIsModelMatrix,
    XRMaterial? MaterialOverride,
    uint Instances,
    int RenderPass,
    bool ForceCpuRendering,
    uint EditorHighlightBits,
    uint StableQueryKey,
    GpuSceneOwnerSnapshot Owner)
{
    internal static GpuSceneMeshCommandSnapshot CaptureLive(RenderInfo renderInfo,
        IRenderCommandMesh command)
        => new(command.Mesh, command.WorldMatrix, command.WorldMatrixIsModelMatrix,
            command.MaterialOverride, command.Instances, command.RenderPass,
            command.ForceCpuRendering, command.EditorHighlightBits, command.StableQueryKey,
            GpuSceneOwnerSnapshot.CaptureLive(renderInfo));

    internal Matrix4x4 ModelMatrix => WorldMatrixIsModelMatrix ? WorldMatrix : Matrix4x4.Identity;
}

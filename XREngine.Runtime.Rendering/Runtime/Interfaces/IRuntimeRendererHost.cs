using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Marker interface for host renderer implementations such as OpenGL or Vulkan renderers.
/// </summary>
public interface IRuntimeRendererHost
{
    /// <summary>
    /// True after the backend detected a terminal graphics-device loss.
    /// </summary>
    bool IsDeviceLost { get; }

    /// <summary>
    /// Returns whether this renderer can consume a GPU-written indirect draw-count buffer directly.
    /// </summary>
    bool SupportsIndirectCountDraw();

    /// <summary>
    /// Returns the task/mesh shader dialect visible to this renderer.
    /// </summary>
    EMeshShaderDialect MeshShaderDialect { get; }

    /// <summary>
    /// Returns whether this renderer can issue diagnostic CPU-count task/mesh dispatch.
    /// </summary>
    bool SupportsDirectMeshTaskDispatch();

    /// <summary>
    /// Returns whether this renderer can submit production task/mesh work from GPU-written counts.
    /// </summary>
    bool SupportsIndirectCountMeshTaskDispatch();

    /// <summary>
    /// Returns whether production task/mesh shader sources are available for this renderer's dialect.
    /// </summary>
    bool SupportsProductionMeshletShaders();

    /// <summary>
    /// Submits mesh-task work from GPU-written indirect arguments and a GPU-written indirect-command count.
    /// </summary>
    bool TryDrawMeshTasksIndirectCount(
        XRDataBuffer indirectBuffer,
        XRDataBuffer countBuffer,
        uint maxDrawCount,
        uint stride,
        out string failureReason,
        nuint byteOffset = 0,
        nuint countByteOffset = 0);

    /// <summary>
    /// Describes why <see cref="SupportsMeshletDispatch"/> is false.
    /// </summary>
    string MeshletDispatchUnsupportedReason { get; }

    /// <summary>
    /// Returns whether this renderer can submit the production zero-readback meshlet/task-mesh path.
    /// </summary>
    bool SupportsMeshletDispatch();

    /// <summary>
    /// Gets the descriptor backend RVC should share with the renderer-owned material/resource table.
    /// </summary>
    ERvcDescriptorBackend RvcDescriptorBackend => ERvcDescriptorBackend.None;

    /// <summary>
    /// Returns whether the renderer has a material/resource table RVC can consume without duplicating descriptors.
    /// </summary>
    bool SupportsRvcMaterialResourceTable => false;

    /// <summary>
    /// Returns whether RVC can allocate and use per-view depth, visibility-id, velocity, shadelet, and resolve targets.
    /// </summary>
    bool SupportsRvcVisibilityTargets => false;

    /// <summary>
    /// Returns whether RVC can build visibility from the direct static-mesh draw path.
    /// </summary>
    bool SupportsRvcStaticMeshVisibilitySource => SupportsRvcVisibilityTargets;

    /// <summary>
    /// Returns whether RVC can consume compute skinning output without CPU readback.
    /// </summary>
    bool SupportsRvcSkinnedComputeVisibilitySource => SupportsRvcVisibilityTargets;

    /// <summary>
    /// Returns whether RVC can consume GPU-written indirect draw sources without CPU readback.
    /// </summary>
    bool SupportsRvcZeroReadbackIndirectVisibilitySource => SupportsIndirectCountDraw();

    /// <summary>
    /// Returns whether RVC can use meshlet or mesh-shader visibility source expansion.
    /// </summary>
    bool SupportsRvcMeshletVisibilitySource => SupportsMeshletDispatch();

    /// <summary>
    /// Returns whether the renderer can upload and stencil OpenXR hidden-area visibility meshes.
    /// </summary>
    bool SupportsRvcOpenXrVisibilityMaskStencil => SupportsRvcVisibilityTargets;

    /// <summary>
    /// Gets the Vulkan production features surfaced to the RVC planner.
    /// </summary>
    ERvcVulkanProductionFeature RvcVulkanProductionFeatures => ERvcVulkanProductionFeature.None;
}

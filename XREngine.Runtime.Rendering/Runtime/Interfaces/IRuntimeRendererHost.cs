using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Marker interface for host renderer implementations such as OpenGL or Vulkan renderers.
/// </summary>
public interface IRuntimeRendererHost
{
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
}

namespace XREngine.Rendering.Compute;

/// <summary>
/// Decides whether the compute skinning and compute blendshape paths may run.
/// The vertex program, the renderer buffers, and the skinning prepass must
/// make the same decision for a mesh, or the draw reads compute outputs that
/// the prepass never writes. <c>XRE_VULKAN_COMPUTE_DEFORMATION=0</c> keeps
/// Vulkan on the vertex-shader path.
/// </summary>
public static class MeshDeformationBackendPolicy
{
    private static readonly bool s_vulkanComputeDeformationDisabled =
        Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanComputeDeformation) == "0";

    /// <summary>Gets whether the active backend may run compute mesh deformation.</summary>
    public static bool AllowsComputeDeformation
        => !RuntimeEngine.Rendering.State.IsVulkan || !s_vulkanComputeDeformationDisabled;

    /// <summary>
    /// Returns true when <paramref name="mesh"/> has the inputs that the compute
    /// blendshape prepass reads. A mesh without blendshapes always qualifies. A
    /// mesh that has only the dense per-vertex blendshape layout must keep the
    /// vertex-shader path for skinning too, because blendshapes apply before skinning.
    /// </summary>
    public static bool MeshSupportsComputeDeformation(XRMesh? mesh)
    {
        if (mesh is null)
            return false;
        if (mesh.BlendshapeCount == 0)
            return true;

        XRMeshBlendshapeBufferState state = mesh.GetBlendshapeBufferStateSnapshot();
        return state.SparseShapeRanges is not null &&
            state.SparseRecords is not null &&
            state.QuantizedDeltas is not null &&
            state.QuantizationMetadata is not null;
    }

    /// <summary>
    /// Returns true when the active backend allows compute deformation and
    /// <paramref name="mesh"/> has the inputs for it.
    /// </summary>
    public static bool AllowsComputeDeformationFor(XRMesh? mesh)
        => AllowsComputeDeformation && MeshSupportsComputeDeformation(mesh);
}

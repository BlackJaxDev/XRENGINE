using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact immutable topology and prepared vertex source consumed by one
/// visibility payload. Canonical geometry never changes to represent
/// deformation; the prepared source carries that frame's output separately.
/// </summary>
internal readonly record struct VulkanVisibilityGeometryRecordClosure(
    AdvancedGpuHandle Geometry,
    AdvancedGeometryRecord CanonicalGeometry,
    VulkanFrameDataSlice CanonicalVertexSlice,
    VulkanFrameDataSlice IndexSlice,
    VulkanVisibilityPreparedVertexSource PreparedVertexSource,
    uint PreparedVertexBase,
    ulong SceneNativeGeneration)
{
    internal bool IsValid
        => Geometry.IsValid && SceneNativeGeneration != 0u &&
           CanonicalVertexSlice.IsValid && IndexSlice.IsValid &&
           PreparedVertexSource.IsValid &&
           CanonicalGeometry.CurrentVertexData.IsValid &&
           CanonicalGeometry.IndexData.IsValid &&
           CanonicalGeometry.CurrentVertexData.ElementStride == 64u &&
           CanonicalGeometry.IndexData.ElementStride == sizeof(uint) &&
           CanonicalGeometry.VertexCount != 0u &&
           CanonicalGeometry.IndexCount != 0u &&
           CanonicalGeometry.Source is EAdvancedGeometrySource.Static or
               EAdvancedGeometrySource.MeshletLocal &&
           RangeFits(CanonicalVertexSlice, CanonicalGeometry.CurrentVertexData) &&
           RangeFits(IndexSlice, CanonicalGeometry.IndexData) &&
           PreparedRangeFits();

    internal bool TryValidate(
        VulkanResourceRuntime resources,
        in VulkanAdvancedScenePublicationState scene,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(resources);
        reason = "Ready";
        if (!IsValid)
        {
            reason = "the canonical visibility geometry closure is incomplete";
            return false;
        }
        if (!scene.IsValid || scene.NativeGeneration != SceneNativeGeneration ||
            scene.StaticVertices != CanonicalVertexSlice ||
            scene.Indices != IndexSlice)
        {
            reason = "the canonical geometry publication changed after visibility-plan sealing";
            return false;
        }
        if (CanonicalGeometry.CurrentVertexData.ElementOffset !=
                CanonicalGeometry.VertexBase ||
            CanonicalGeometry.CurrentVertexData.ElementCount !=
                CanonicalGeometry.VertexCount ||
            CanonicalGeometry.IndexData.ElementOffset !=
                CanonicalGeometry.IndexBase ||
            CanonicalGeometry.IndexData.ElementCount !=
                CanonicalGeometry.IndexCount)
        {
            reason = "the canonical geometry record range changed after visibility-plan sealing";
            return false;
        }
        if (!PreparedVertexSource.TryValidate(resources, out reason))
            return false;
        if (!PreparedVertexSource.UsesNativeRange &&
            (PreparedVertexSource.CanonicalSlice != CanonicalVertexSlice ||
             PreparedVertexBase != CanonicalGeometry.VertexBase))
        {
            reason = "a static visibility draw no longer addresses its canonical vertex range";
            return false;
        }

        return true;
    }

    private bool PreparedRangeFits()
    {
        ulong byteOffset = (ulong)PreparedVertexBase *
            PreparedVertexSource.ElementStride;
        ulong byteLength = (ulong)CanonicalGeometry.VertexCount *
            PreparedVertexSource.ElementStride;
        return byteOffset <= PreparedVertexSource.Length &&
            byteLength <= PreparedVertexSource.Length - byteOffset;
    }

    private static bool RangeFits(
        VulkanFrameDataSlice slice,
        AdvancedBufferReference reference)
        => reference.ByteOffset <= slice.Length &&
           reference.ByteLength <= slice.Length - reference.ByteOffset;
}

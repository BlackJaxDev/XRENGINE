using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact canonical geometry publication consumed by one visibility payload.
/// Both slices belong to the retained advanced-scene frame slot, so recording
/// can reject a later slot rotation without consulting the legacy GPU scene.
/// </summary>
internal readonly record struct VulkanVisibilityGeometryRecordClosure(
    AdvancedGpuHandle Geometry,
    EAdvancedGeometrySource Source,
    VulkanFrameDataSlice VertexSlice,
    VulkanFrameDataSlice IndexSlice,
    AdvancedBufferReference VertexReference,
    AdvancedBufferReference IndexReference,
    uint VertexBase,
    uint VertexCount,
    uint IndexBase,
    uint IndexCount,
    ulong VertexLayoutId,
    ulong SceneNativeGeneration)
{
    internal bool IsValid
        => Geometry.IsValid && SceneNativeGeneration != 0u &&
           VertexSlice.IsValid && IndexSlice.IsValid &&
           VertexReference.IsValid && IndexReference.IsValid &&
           VertexReference.ElementStride == 64u &&
           IndexReference.ElementStride == sizeof(uint) &&
           VertexCount != 0u && IndexCount != 0u &&
           Source is EAdvancedGeometrySource.Static or
               EAdvancedGeometrySource.MeshletLocal or
               EAdvancedGeometrySource.PreSkinnedCurrentAndPrevious &&
           RangeFits(VertexSlice, VertexReference) &&
           RangeFits(IndexSlice, IndexReference);

    internal bool TryValidate(
        in VulkanAdvancedScenePublicationState scene,
        out string reason)
    {
        reason = "Ready";
        if (!IsValid)
        {
            reason = "the canonical visibility geometry closure is incomplete";
            return false;
        }
        VulkanFrameDataSlice expectedVertices = Source ==
            EAdvancedGeometrySource.PreSkinnedCurrentAndPrevious
                ? scene.PreSkinnedCurrent
                : scene.StaticVertices;
        if (!scene.IsValid || scene.NativeGeneration != SceneNativeGeneration ||
            expectedVertices != VertexSlice || scene.Indices != IndexSlice)
        {
            reason = "the canonical geometry publication changed after visibility-plan sealing";
            return false;
        }
        if (VertexReference.ElementOffset != VertexBase ||
            VertexReference.ElementCount != VertexCount ||
            IndexReference.ElementOffset != IndexBase ||
            IndexReference.ElementCount != IndexCount)
        {
            reason = "the canonical geometry record range changed after visibility-plan sealing";
            return false;
        }

        return true;
    }

    private static bool RangeFits(
        VulkanFrameDataSlice slice,
        AdvancedBufferReference reference)
        => reference.ByteOffset <= slice.Length &&
           reference.ByteLength <= slice.Length - reference.ByteOffset;
}

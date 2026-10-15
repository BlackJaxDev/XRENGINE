using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact immutable topology and vertex source consumed by one visibility
/// payload. Canonical geometry never changes to represent deformation.
/// </summary>
/// <remarks>
/// A deformed record does not hold the deformation output buffer. That buffer
/// changes with the deformed-arena frame slot, so the stream applies it as a
/// frame binding (<see cref="ResolveVertexSource"/>). The record holds only the
/// vertex base inside that buffer, which stays the same while the arena keeps
/// the owner's range. A record therefore stays valid across frames while its
/// inputs are unchanged.
/// </remarks>
internal readonly record struct VulkanVisibilityGeometryRecordClosure(
    AdvancedGpuHandle Geometry,
    AdvancedGeometryRecord CanonicalGeometry,
    VulkanFrameDataSlice CanonicalVertexSlice,
    VulkanFrameDataSlice IndexSlice,
    VulkanVisibilityPreparedVertexSource StaticVertexSource,
    uint PreparedVertexBase,
    bool UsesDeformedVertexSource)
{
    /// <summary>The element stride of the deformation output buffer.</summary>
    internal const uint DeformedVertexStride = 64u;

    internal bool IsValid
        => Geometry.IsValid &&
           CanonicalVertexSlice.IsValid && IndexSlice.IsValid &&
           (UsesDeformedVertexSource
               ? !StaticVertexSource.IsValid
               : StaticVertexSource.IsValid &&
                 !StaticVertexSource.UsesNativeRange &&
                 RangeFits(StaticVertexSource)) &&
           CanonicalGeometry.CurrentVertexData.IsValid &&
           CanonicalGeometry.IndexData.IsValid &&
           CanonicalGeometry.CurrentVertexData.ElementStride == 64u &&
           CanonicalGeometry.IndexData.ElementStride == sizeof(uint) &&
           CanonicalGeometry.VertexCount != 0u &&
           CanonicalGeometry.IndexCount != 0u &&
           CanonicalGeometry.Source is EAdvancedGeometrySource.Static or
               EAdvancedGeometrySource.MeshletLocal &&
           RangeFits(CanonicalVertexSlice, CanonicalGeometry.CurrentVertexData) &&
           RangeFits(IndexSlice, CanonicalGeometry.IndexData);

    /// <summary>
    /// Returns the vertex source that this record reads: the static canonical
    /// source, or the deformation output of the current frame.
    /// </summary>
    internal VulkanVisibilityPreparedVertexSource ResolveVertexSource(
        in VulkanVisibilityPreparedVertexSource deformedVertices)
        => UsesDeformedVertexSource ? deformedVertices : StaticVertexSource;

    /// <summary>
    /// Validates the record against the scene publication and the deformation
    /// output of the current frame.
    /// </summary>
    internal bool TryValidate(
        VulkanResourceRuntime resources,
        in VulkanAdvancedScenePublicationState scene,
        in VulkanVisibilityPreparedVertexSource deformedVertices,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(resources);
        reason = "Ready";
        if (!IsValid)
        {
            reason = "the canonical visibility geometry closure is incomplete";
            return false;
        }
        if (!scene.IsValid ||
            !scene.StaticVertices.Matches(CanonicalVertexSlice) ||
            !scene.Indices.Matches(IndexSlice))
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

        VulkanVisibilityPreparedVertexSource source = ResolveVertexSource(in deformedVertices);
        if (UsesDeformedVertexSource)
        {
            if (!source.UsesNativeRange ||
                source.ElementStride != DeformedVertexStride ||
                !RangeFits(in source))
            {
                reason = "the deformation output of this frame does not cover a deformed visibility draw";
                return false;
            }
        }
        else if (!source.CanonicalSlice.Matches(CanonicalVertexSlice) ||
            PreparedVertexBase != CanonicalGeometry.VertexBase)
        {
            reason = "a static visibility draw no longer addresses its canonical vertex range";
            return false;
        }

        return source.TryValidate(resources, out reason);
    }

    private bool RangeFits(in VulkanVisibilityPreparedVertexSource source)
    {
        ulong byteOffset = (ulong)PreparedVertexBase * source.ElementStride;
        ulong byteLength = (ulong)CanonicalGeometry.VertexCount * source.ElementStride;
        return byteOffset <= source.Length &&
            byteLength <= source.Length - byteOffset;
    }

    private static bool RangeFits(
        VulkanFrameDataSlice slice,
        AdvancedBufferReference reference)
        => reference.ByteOffset <= slice.Length &&
           reference.ByteLength <= slice.Length - reference.ByteOffset;
}

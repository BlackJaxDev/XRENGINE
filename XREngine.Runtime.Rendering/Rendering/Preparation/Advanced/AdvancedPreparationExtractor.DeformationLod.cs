using System.Numerics;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;

namespace XREngine.Rendering;

public sealed partial class AdvancedPreparationExtractor
{
    /// <summary>
    /// Draw flags of paths that read the deformed vertices of a skinned draw without the clamped
    /// cluster cut: the directional shadow raster (level-0 ranges), transparency, the
    /// depth-writing masked raster, the decal buffer pass, the editor highlight stencil draw,
    /// wireframe, and non-canonical raster state.
    /// </summary>
    private const GPUIndirectRenderFlags ExcludedDeformationLodFlags =
        GPUIndirectRenderFlags.CastShadow |
        GPUIndirectRenderFlags.Transparent |
        GPUIndirectRenderFlags.FragmentDepth |
        GPUIndirectRenderFlags.SurfaceDecal |
        GPUIndirectRenderFlags.EditorHighlightMask |
        GPUIndirectRenderFlags.Wireframe |
        GPUIndirectRenderFlags.NonCanonicalRasterState |
        GPUIndirectRenderFlags.CpuFallbackOnly;

    private readonly Vector3[] _deformationLodEyes = new Vector3[RenderFrameViewSet.MaxViewCount];
    private int _deformationLodEyeCount;
    private float _deformationLodPixelScale;
    private float _deformationLodNearDistance;

    /// <summary>
    /// Reads the eye positions, the largest pixel scale, and the smallest near distance of the
    /// initial view set of this build. Deformation level of detail chooses levels only from these
    /// views. Without a usable view set every skinned draw keeps level 0. A view that a later
    /// <see cref="AddVisibilityPlans"/> call adds still draws only deformed vertices, because the
    /// clamp in the task stage keeps every drawn cluster inside the deformed prefix. Such a view
    /// can show coarser clusters than its own distance needs.
    /// </summary>
    private void CaptureDeformationLodViews(RenderFrameViewSet? viewSet)
    {
        _deformationLodEyeCount = 0;
        if (viewSet is not RenderFrameViewSet views ||
            !AdvancedVirtualGeometryPolicy.DeformationLodEnabled ||
            !AdvancedDeformationLevelSelector.TryCaptureViews(
                in views,
                _deformationLodEyes,
                out int eyeCount,
                out _deformationLodPixelScale,
                out _deformationLodNearDistance))
            return;
        _deformationLodEyeCount = eyeCount;
    }

    /// <summary>
    /// Chooses the minimum cluster level of one skinned draw. A level above 0 makes the
    /// deformation job deform only the prefix of the cooked deformation order for that level, and
    /// the skinned cut never draws a finer cluster. The draw gets level 0 unless every consumer of
    /// its deformed vertices selects clusters through the clamped cut: the meshlet task stage of
    /// an opaque or masked draw with the full hierarchy range (see
    /// <see cref="ExcludedDeformationLodFlags"/>). Shadow casters keep level 0 until the
    /// directional shadow raster draws a level cover range.
    /// </summary>
    private uint ChooseDeformationLevel(
        AdvancedGpuScenePublicationSnapshot publication,
        in AdvancedDrawRecord draw,
        in AdvancedInstanceRecord instance,
        in AdvancedGeometryRecord geometry,
        in DrawMetadata command,
        XRMesh mesh,
        float clusterCutMargin,
        bool hasPhysicsBounds,
        bool physicsStateCaptured,
        in PhysicsChainGpuRendererOutputState physicsState)
    {
        if (_deformationLodEyeCount == 0 ||
            (command.Flags & (uint)ExcludedDeformationLodFlags) != 0u ||
            !geometry.IsResident ||
            !geometry.HasClusterHierarchy ||
            geometry.MeshletCount <= geometry.DrawMeshletCount ||
            !geometry.MeshletDescriptors.IsValid ||
            !geometry.MeshletVertexIndices.IsValid ||
            !geometry.MeshletTriangleWords.IsValid ||
            mesh.MeshletPayload is not { HasDeformationOrder: true } payload ||
            payload.DeformationVertexOrder.Length != mesh.VertexCount ||
            !publication.Transforms.TryGet(draw.CurrentTransform, out AdvancedTransformRecord transform))
            return 0u;

        // A GPU physics draw needs the committed proxy box that contains its patched sphere.
        bool physicsBoundsUsable = hasPhysicsBounds && physicsStateCaptured && physicsState.CpuSpatialBoundsValid;
        if (hasPhysicsBounds && !physicsBoundsUsable)
            return 0u;

        float maxScale = AdvancedDeformationLevelSelector.MaxAxisScale(in transform.World);
        float distance = AdvancedDeformationLevelSelector.DrawDistance(
            _deformationLodEyes.AsSpan(0, _deformationLodEyeCount),
            instance.BoundsSphere,
            physicsBoundsUsable,
            physicsBoundsUsable ? physicsState.CpuSpatialBounds.Min : default,
            physicsBoundsUsable ? physicsState.CpuSpatialBounds.Max : default,
            clusterCutMargin,
            maxScale,
            _deformationLodNearDistance);
        uint level = AdvancedDeformationLevelSelector.ChooseLevel(
            payload.LevelMaxErrors.AsSpan(),
            distance,
            maxScale,
            _deformationLodPixelScale,
            AdvancedVirtualGeometryPolicy.PixelThreshold);
        return level != 0u && payload.DeformationPrefixCounts[(int)level] != 0u ? level : 0u;
    }

    /// <summary>
    /// Returns the number of leading vertices of the cooked deformation order that the job of a
    /// draw with minimum level <paramref name="level"/> deforms, or the full vertex count when
    /// the draw deforms every vertex. The prefix applies only when the prepared mesh slice stores
    /// the order in its influence rows.
    /// </summary>
    private static uint ResolveDeformedVertexCount(
        XRMesh mesh,
        in AdvancedGpuDeformationMeshSlice meshSlice,
        uint level,
        out bool ordered)
    {
        ordered = false;
        uint vertexCount = checked((uint)mesh.VertexCount);
        if (level == 0u || !meshSlice.HasDeformationOrder ||
            mesh.MeshletPayload is not { HasDeformationOrder: true } payload ||
            level >= (uint)payload.DeformationPrefixCounts.Length)
            return vertexCount;
        uint prefix = payload.DeformationPrefixCounts[(int)level];
        if (prefix == 0u || prefix >= vertexCount)
            return vertexCount;
        ordered = true;
        return prefix;
    }
}

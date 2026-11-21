using System.Numerics;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering;

public sealed partial class AdvancedPreparationExtractor
{
    /// <summary>
    /// Draw flags of paths that read the deformed vertices of a skinned draw without the clamped
    /// cluster cut and without the level cover range of the visibility payload: transparency,
    /// the depth-writing masked raster, the decal buffer pass, the editor highlight stencil draw,
    /// wireframe, and the CPU fallback. The directional shadow raster draws the payload index
    /// range, which is the level cover of a clamped draw, so shadow casters can use deformation
    /// level of detail. <see cref="ChooseDeformationLevel"/> checks the cull state separately.
    /// </summary>
    private const GPUIndirectRenderFlags ExcludedDeformationLodFlags =
        GPUIndirectRenderFlags.Transparent |
        GPUIndirectRenderFlags.FragmentDepth |
        GPUIndirectRenderFlags.SurfaceDecal |
        GPUIndirectRenderFlags.EditorHighlightMask |
        GPUIndirectRenderFlags.Wireframe |
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
    /// <see cref="ExcludedDeformationLodFlags"/>), or an indexed raster that draws the level cover
    /// of the chosen level. A level above 0 therefore needs the published level covers of the
    /// geometry (see <see cref="ResolveDrawIndexRange"/>).
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
        // The view check comes after the geometry checks, so that the diagnostics also show the
        // geometry state of a cluster cut session without deformation level of detail.
        uint excludedFlags = command.Flags & (uint)ExcludedDeformationLodFlags;
        if (excludedFlags != 0u)
            return NoteDeformationLevelGate(ClassifyExcludedDrawFlags(excludedFlags));
        // A double-sided draw sets NonCanonicalRasterState, but the visibility payload carries its
        // cull mode, and the raster draws it through the same cluster cut and index range as a
        // back-face culled draw. Front-face and all-face culling keep level 0.
        if ((command.Flags & (uint)GPUIndirectRenderFlags.NonCanonicalRasterState) != 0u &&
            (command.Flags & (uint)GPUIndirectRenderFlags.DoubleSided) == 0u)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.ExcludedCullState);
        if (!geometry.IsResident)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.GeometryNotResident);
        if (geometry.MeshletCount == 0u)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.NoMeshlets);
        if (!geometry.HasClusterHierarchy)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.NoClusterHierarchy);
        if (geometry.MeshletCount <= geometry.DrawMeshletCount)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.NoCoarserLevels);
        if (!geometry.MeshletDescriptors.IsValid ||
            !geometry.MeshletVertexIndices.IsValid ||
            !geometry.MeshletTriangleWords.IsValid)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.MeshletBuffersInvalid);
        if (mesh.MeshletPayload is not { HasDeformationOrder: true } payload)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.NoDeformationOrder);
        if (payload.DeformationVertexOrder.Length != mesh.VertexCount)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.DeformationOrderMismatch);
        if (_deformationLodEyeCount == 0)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.NoViews);
        if (!publication.Transforms.TryGet(draw.CurrentTransform, out AdvancedTransformRecord transform))
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.NoTransform);

        // A GPU physics draw needs the committed proxy box that contains its patched sphere.
        bool physicsBoundsUsable = hasPhysicsBounds && physicsStateCaptured && physicsState.CpuSpatialBoundsValid;
        if (hasPhysicsBounds && !physicsBoundsUsable)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.PhysicsBoundsUnusable);

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
        if (level == 0u)
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.SelectedLevelZero);
        if (payload.DeformationPrefixCounts[(int)level] == 0u ||
            !HasPublishedLevelCover(in geometry, payload, level))
            return NoteDeformationLevelGate(EAdvancedDeformationLevelGate.NoPrefixOrCover);
        NoteDeformationLevelGate(EAdvancedDeformationLevelGate.Chosen, level);
        return level;
    }

    /// <summary>
    /// Returns the gate outcome of the first excluded flag in <paramref name="excludedFlags"/>,
    /// in the order of <see cref="ExcludedDeformationLodFlags"/>.
    /// </summary>
    private static EAdvancedDeformationLevelGate ClassifyExcludedDrawFlags(uint excludedFlags)
    {
        if ((excludedFlags & (uint)GPUIndirectRenderFlags.Transparent) != 0u)
            return EAdvancedDeformationLevelGate.ExcludedTransparent;
        if ((excludedFlags & (uint)GPUIndirectRenderFlags.FragmentDepth) != 0u)
            return EAdvancedDeformationLevelGate.ExcludedFragmentDepth;
        if ((excludedFlags & (uint)GPUIndirectRenderFlags.SurfaceDecal) != 0u)
            return EAdvancedDeformationLevelGate.ExcludedSurfaceDecal;
        if ((excludedFlags & (uint)GPUIndirectRenderFlags.EditorHighlightMask) != 0u)
            return EAdvancedDeformationLevelGate.ExcludedEditorHighlight;
        if ((excludedFlags & (uint)GPUIndirectRenderFlags.Wireframe) != 0u)
            return EAdvancedDeformationLevelGate.ExcludedWireframe;
        return EAdvancedDeformationLevelGate.ExcludedCpuFallback;
    }

    /// <summary>
    /// True when the geometry record holds the level covers of the payload after its canonical
    /// indices, and the cover of <paramref name="level"/> is not empty.
    /// </summary>
    private static bool HasPublishedLevelCover(in AdvancedGeometryRecord geometry, MeshletPayload payload, uint level)
        => level < (uint)payload.LevelCoverIndexCount.Length &&
           payload.LevelCoverIndexCount[(int)level] != 0u &&
           geometry.LevelCoverIndexCount == (uint)payload.LevelCoverIndices.Length;

    /// <summary>
    /// Returns the index range of the visibility payload of a draw. A skinned draw with
    /// minimum level <paramref name="deformationLevel"/> above 0 draws the level cover of that
    /// level, because its deformation job deformed only the vertices of the cover. Every
    /// indexed raster of the draw (the directional shadow raster and the traditional raster)
    /// reads this range. The cluster cut ignores it and selects clusters from the meshlet range.
    /// Other draws use the canonical index list.
    /// </summary>
    private static void ResolveDrawIndexRange(
        in AdvancedGeometryRecord geometry,
        XRMesh? mesh,
        uint deformationLevel,
        out uint firstIndex,
        out uint indexCount)
    {
        firstIndex = geometry.IndexData.ElementOffset;
        indexCount = geometry.IndexCount;
        if (deformationLevel == 0u ||
            mesh?.MeshletPayload is not { } payload ||
            !HasPublishedLevelCover(in geometry, payload, deformationLevel))
            return;
        firstIndex = geometry.LevelCoverIndexBase + payload.LevelCoverFirstIndex[(int)deformationLevel];
        indexCount = payload.LevelCoverIndexCount[(int)deformationLevel];
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

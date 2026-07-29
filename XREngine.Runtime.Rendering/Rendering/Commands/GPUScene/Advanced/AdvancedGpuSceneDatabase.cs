namespace XREngine.Rendering.Commands;

/// <summary>
/// Shared canonical GPU-scene database. It owns backend-neutral records while
/// render pipelines own output topology, temporal images, and command recording.
/// </summary>
public sealed class AdvancedGpuSceneDatabase
{
    public AdvancedGpuSceneDatabase(in AdvancedGpuSceneCapacityProfile capacities)
    {
        Draws = new AdvancedGpuRecordTable<AdvancedDrawRecord>(capacities.DrawRecords);
        Instances = new AdvancedGpuRecordTable<AdvancedInstanceRecord>(capacities.InstanceRecords);
        Transforms = new AdvancedGpuRecordTable<AdvancedTransformRecord>(capacities.TransformRecords);
        Deformations = new AdvancedGpuRecordTable<AdvancedDeformationRecord>(capacities.DeformationRecords);
        RenderStates = new AdvancedGpuRecordTable<AdvancedRenderStateRecord>(capacities.RenderStateRecords);
        EditorIdentities = new AdvancedGpuRecordTable<AdvancedEditorIdentityRecord>(capacities.EditorIdentityRecords);
        Geometry = new AdvancedGeometryDatabase(
            capacities.GeometryRecords,
            capacities.StaticVertexBytes,
            capacities.IndexBytes,
            capacities.PreSkinnedCurrentBytes,
            capacities.PreSkinnedPreviousBytes,
            capacities.MeshletBytes);
    }

    public AdvancedGpuRecordTable<AdvancedDrawRecord> Draws { get; }

    public AdvancedGpuRecordTable<AdvancedInstanceRecord> Instances { get; }

    public AdvancedGpuRecordTable<AdvancedTransformRecord> Transforms { get; }

    public AdvancedGpuRecordTable<AdvancedDeformationRecord> Deformations { get; }

    public AdvancedGpuRecordTable<AdvancedRenderStateRecord> RenderStates { get; }

    public AdvancedGpuRecordTable<AdvancedEditorIdentityRecord> EditorIdentities { get; }

    public AdvancedGeometryDatabase Geometry { get; }

    public bool TryResolveDraw(
        AdvancedGpuHandle drawHandle,
        out AdvancedResolvedDrawRecords resolved)
    {
        resolved = default;
        if (!Draws.TryGet(drawHandle, out AdvancedDrawRecord draw) ||
            !draw.Material.IsValid ||
            !Instances.TryGet(draw.Instance, out AdvancedInstanceRecord instance) ||
            !Geometry.TryGet(draw.Geometry, out AdvancedGeometryRecord geometry) ||
            !Transforms.TryGet(draw.CurrentTransform, out AdvancedTransformRecord currentTransform) ||
            !Transforms.TryGet(draw.PreviousTransform, out AdvancedTransformRecord previousTransform) ||
            !RenderStates.TryGet(draw.RenderState, out AdvancedRenderStateRecord renderState) ||
            !EditorIdentities.TryGet(draw.EditorIdentity, out AdvancedEditorIdentityRecord editorIdentity))
        {
            return false;
        }

        AdvancedDeformationRecord deformation = default;
        uint hasDeformation = 0u;
        if (draw.Deformation.IsValid)
        {
            if (!Deformations.TryGet(draw.Deformation, out deformation))
                return false;
            hasDeformation = 1u;
        }

        resolved = new AdvancedResolvedDrawRecords
        {
            Draw = draw,
            Instance = instance,
            Geometry = geometry,
            CurrentTransform = currentTransform,
            PreviousTransform = previousTransform,
            Deformation = deformation,
            RenderState = renderState,
            EditorIdentity = editorIdentity,
            Material = draw.Material,
            HasDeformation = hasDeformation,
        };
        return true;
    }

    public bool TryResolveVisibilityDraw(
        AdvancedGpuHandle drawHandle,
        out AdvancedResolvedDrawRecords resolved)
    {
        if (!TryResolveDraw(drawHandle, out resolved))
            return false;
        if (resolved.Geometry.IsResident)
            return true;
        if (!Geometry.TryResolveVisibilityGeometry(resolved.Draw.Geometry, out AdvancedGeometryRecord fallback))
            return false;

        resolved.Geometry = fallback;
        return true;
    }

    public bool TryCreateDrawDependencySnapshot(
        AdvancedGpuHandle drawHandle,
        out AdvancedDrawDependencySnapshot snapshot)
    {
        snapshot = default;
        if (!TryResolveDraw(drawHandle, out AdvancedResolvedDrawRecords resolved) ||
            !Draws.TryGetDenseIndex(drawHandle, out uint drawDenseIndex) ||
            !Instances.TryGetDenseIndex(resolved.Draw.Instance, out uint instanceDenseIndex) ||
            !Geometry.Records.TryGetDenseIndex(resolved.Draw.Geometry, out uint geometryDenseIndex) ||
            !RenderStates.TryGetDenseIndex(resolved.Draw.RenderState, out uint renderStateDenseIndex) ||
            !EditorIdentities.TryGetDenseIndex(resolved.Draw.EditorIdentity, out uint editorIdentityDenseIndex) ||
            !Transforms.TryGetDenseIndex(resolved.Draw.CurrentTransform, out uint currentTransformDenseIndex) ||
            !Transforms.TryGetDenseIndex(resolved.Draw.PreviousTransform, out uint previousTransformDenseIndex))
        {
            return false;
        }

        uint deformationDenseIndex = AdvancedGpuHandleRemap.InvalidDenseIndex;
        if (resolved.Draw.Deformation.IsValid &&
            !Deformations.TryGetDenseIndex(resolved.Draw.Deformation, out deformationDenseIndex))
        {
            return false;
        }

        snapshot = new AdvancedDrawDependencySnapshot
        {
            Draw = drawHandle,
            Instance = resolved.Draw.Instance,
            Geometry = resolved.Draw.Geometry,
            Material = resolved.Draw.Material,
            Deformation = resolved.Draw.Deformation,
            RenderState = resolved.Draw.RenderState,
            EditorIdentity = resolved.Draw.EditorIdentity,
            CurrentTransform = resolved.Draw.CurrentTransform,
            PreviousTransform = resolved.Draw.PreviousTransform,
            DrawDenseIndex = drawDenseIndex,
            InstanceDenseIndex = instanceDenseIndex,
            GeometryDenseIndex = geometryDenseIndex,
            DeformationDenseIndex = deformationDenseIndex,
            RenderStateDenseIndex = renderStateDenseIndex,
            EditorIdentityDenseIndex = editorIdentityDenseIndex,
            CurrentTransformDenseIndex = currentTransformDenseIndex,
            PreviousTransformDenseIndex = previousTransformDenseIndex,
            GeometryResidency = resolved.Geometry.Residency,
        };
        return true;
    }

    /// <summary>
    /// Begins one structural publication batch. Consumers must have copied the
    /// previous remap spans before clearing them.
    /// </summary>
    public void BeginStructuralUpdate()
    {
        Draws.ClearPublishedRemaps();
        Instances.ClearPublishedRemaps();
        Transforms.ClearPublishedRemaps();
        Deformations.ClearPublishedRemaps();
        RenderStates.ClearPublishedRemaps();
        EditorIdentities.ClearPublishedRemaps();
        Geometry.Records.ClearPublishedRemaps();
    }

    /// <summary>
    /// Compacts every physical table without allocations and leaves uploadable remap
    /// spans on each table. Returns -1 if a retained remap batch exhausted capacity.
    /// </summary>
    public int CompactAndPublishRemaps()
    {
        int total = 0;
        if (!AccumulateCompaction(Draws.Compact(), ref total) ||
            !AccumulateCompaction(Instances.Compact(), ref total) ||
            !AccumulateCompaction(Transforms.Compact(), ref total) ||
            !AccumulateCompaction(Deformations.Compact(), ref total) ||
            !AccumulateCompaction(RenderStates.Compact(), ref total) ||
            !AccumulateCompaction(EditorIdentities.Compact(), ref total) ||
            !AccumulateCompaction(Geometry.Records.Compact(), ref total))
        {
            return -1;
        }

        return total;
    }

    public void ApplyDrawRemaps(Span<uint> dependentDrawDenseIndices)
        => Draws.ApplyPublishedRemaps(dependentDrawDenseIndices);

    public void ApplyInstanceRemaps(Span<uint> dependentInstanceDenseIndices)
        => Instances.ApplyPublishedRemaps(dependentInstanceDenseIndices);

    public void ApplyGeometryRemaps(Span<uint> dependentGeometryDenseIndices)
        => Geometry.Records.ApplyPublishedRemaps(dependentGeometryDenseIndices);

    public void ApplyTransformRemaps(Span<uint> dependentTransformDenseIndices)
        => Transforms.ApplyPublishedRemaps(dependentTransformDenseIndices);

    public void ApplyDeformationRemaps(Span<uint> dependentDeformationDenseIndices)
        => Deformations.ApplyPublishedRemaps(dependentDeformationDenseIndices);

    public void ApplyRenderStateRemaps(Span<uint> dependentRenderStateDenseIndices)
        => RenderStates.ApplyPublishedRemaps(dependentRenderStateDenseIndices);

    public void ApplyEditorIdentityRemaps(Span<uint> dependentEditorIdentityDenseIndices)
        => EditorIdentities.ApplyPublishedRemaps(dependentEditorIdentityDenseIndices);

    public void GrowAtFrameBoundary(in AdvancedGpuSceneCapacityProfile capacities)
    {
        Draws.GrowAtBoundary(capacities.DrawRecords);
        Instances.GrowAtBoundary(capacities.InstanceRecords);
        Transforms.GrowAtBoundary(capacities.TransformRecords);
        Deformations.GrowAtBoundary(capacities.DeformationRecords);
        RenderStates.GrowAtBoundary(capacities.RenderStateRecords);
        EditorIdentities.GrowAtBoundary(capacities.EditorIdentityRecords);
        Geometry.GrowAtBoundary(
            capacities.GeometryRecords,
            capacities.StaticVertexBytes,
            capacities.IndexBytes,
            capacities.PreSkinnedCurrentBytes,
            capacities.PreSkinnedPreviousBytes,
            capacities.MeshletBytes);
    }

    private static bool AccumulateCompaction(int result, ref int total)
    {
        if (result < 0)
            return false;

        total = checked(total + result);
        return true;
    }
}

namespace XREngine.Rendering.Commands;

/// <summary>
/// Packed GPU upload image that resolves stable logical handles to compacted
/// physical rows for both scene and material databases.
/// </summary>
public sealed class AdvancedGpuSceneLookupTable
{
    private const int SegmentCount = 10;

    private AdvancedGpuHandleLookup[] _records;
    private readonly AdvancedGpuDirtyRange[] _publishedDirtyRanges =
        new AdvancedGpuDirtyRange[SegmentCount];
    private int _publishedDirtyRangeCount;

    public AdvancedGpuSceneLookupTable(
        AdvancedGpuSceneDatabase scene,
        AdvancedMaterialDatabase materials)
    {
        Layout = AdvancedGpuSceneLookupLayout.Create(scene, materials);
        _records = new AdvancedGpuHandleLookup[checked((int)Layout.TotalCount)];
        _records.AsSpan().Fill(AdvancedGpuHandleLookup.Invalid);
    }

    public AdvancedGpuSceneLookupLayout Layout { get; private set; }

    public ReadOnlySpan<AdvancedGpuHandleLookup> Records => _records;

    /// <summary>
    /// Global lookup-buffer ranges changed by the latest publication. Backend
    /// upload code can copy these ranges without rewriting unchanged segments.
    /// </summary>
    public ReadOnlySpan<AdvancedGpuDirtyRange> PublishedDirtyRanges
        => _publishedDirtyRanges.AsSpan(0, _publishedDirtyRangeCount);

    /// <summary>
    /// Rebuilds only at an explicit structural boundary after any table capacity grows.
    /// </summary>
    public void RebuildAtFrameBoundary(
        AdvancedGpuSceneDatabase scene,
        AdvancedMaterialDatabase materials)
    {
        AdvancedGpuSceneLookupLayout layout =
            AdvancedGpuSceneLookupLayout.Create(scene, materials);
        if (layout == Layout)
        {
            _publishedDirtyRangeCount = 0;
            return;
        }

        Layout = layout;
        _records = new AdvancedGpuHandleLookup[checked((int)layout.TotalCount)];
        _records.AsSpan().Fill(AdvancedGpuHandleLookup.Invalid);
        CopyAll(scene.Draws, layout.Draws);
        CopyAll(scene.Instances, layout.Instances);
        CopyAll(scene.Transforms, layout.Transforms);
        CopyAll(scene.Deformations, layout.Deformations);
        CopyAll(scene.RenderStates, layout.RenderStates);
        CopyAll(scene.EditorIdentities, layout.EditorIdentities);
        CopyAll(scene.Geometry.Records, layout.Geometry);
        CopyAll(materials.Materials, layout.Materials);
        CopyAll(materials.Kernels, layout.ShadingKernels);
        CopyAll(materials.Layouts, layout.MaterialLayouts);
        ClearSourceDirtyRanges(scene, materials);
        _publishedDirtyRanges[0] = new AdvancedGpuDirtyRange(0u, layout.TotalCount);
        _publishedDirtyRangeCount = layout.TotalCount == 0u ? 0 : 1;
    }

    /// <summary>
    /// Publishes changed lookup rows without allocating. This is the table uploaded
    /// before visibility consumers when structural records changed or compacted.
    /// </summary>
    public bool Publish(
        AdvancedGpuSceneDatabase scene,
        AdvancedMaterialDatabase materials)
    {
        if (AdvancedGpuSceneLookupLayout.Create(scene, materials) != Layout)
            return false;

        _publishedDirtyRangeCount = 0;
        if (!CopyDirty(scene.Draws, Layout.Draws) ||
            !CopyDirty(scene.Instances, Layout.Instances) ||
            !CopyDirty(scene.Transforms, Layout.Transforms) ||
            !CopyDirty(scene.Deformations, Layout.Deformations) ||
            !CopyDirty(scene.RenderStates, Layout.RenderStates) ||
            !CopyDirty(scene.EditorIdentities, Layout.EditorIdentities) ||
            !CopyDirty(scene.Geometry.Records, Layout.Geometry) ||
            !CopyDirty(materials.Materials, Layout.Materials) ||
            !CopyDirty(materials.Kernels, Layout.ShadingKernels) ||
            !CopyDirty(materials.Layouts, Layout.MaterialLayouts))
        {
            _publishedDirtyRangeCount = 0;
            return false;
        }

        ClearSourceDirtyRanges(scene, materials);
        return true;
    }

    public bool TryResolve(
        AdvancedGpuHandle handle,
        in AdvancedGpuLookupSegment segment,
        out uint denseIndex)
    {
        denseIndex = AdvancedGpuHandleRemap.InvalidDenseIndex;
        if (!handle.IsValid || handle.Index >= segment.Count)
            return false;

        AdvancedGpuHandleLookup lookup =
            _records[checked((int)(segment.Offset + handle.Index))];
        if (lookup.Generation != handle.Generation || !lookup.IsResident)
            return false;

        denseIndex = lookup.DenseIndex;
        return true;
    }

    private void CopyAll<T>(
        AdvancedGpuRecordTable<T> table,
        in AdvancedGpuLookupSegment segment)
        where T : unmanaged
    {
        Span<AdvancedGpuHandleLookup> destination =
            _records.AsSpan(checked((int)segment.Offset), checked((int)segment.Count));
        destination.Fill(AdvancedGpuHandleLookup.Invalid);
        if (!table.CopyLogicalLookups(destination, out _))
            throw new InvalidOperationException("Advanced lookup segment capacity is inconsistent with its source table.");
    }

    private bool CopyDirty<T>(
        AdvancedGpuRecordTable<T> table,
        in AdvancedGpuLookupSegment segment)
        where T : unmanaged
    {
        AdvancedGpuDirtyRange sourceRange = table.LogicalLookupDirtyRange;
        if (sourceRange.IsEmpty)
            return true;
        if (sourceRange.Start > segment.Count ||
            sourceRange.Count > segment.Count - sourceRange.Start)
        {
            return false;
        }

        Span<AdvancedGpuHandleLookup> destination = _records.AsSpan(
            checked((int)(segment.Offset + sourceRange.Start)),
            checked((int)sourceRange.Count));
        if (!table.CopyDirtyLogicalLookups(
                destination,
                out AdvancedGpuDirtyRange copiedRange) ||
            copiedRange != sourceRange)
        {
            return false;
        }

        AppendPublishedDirtyRange(new AdvancedGpuDirtyRange(
            segment.Offset + sourceRange.Start,
            sourceRange.Count));
        return true;
    }

    private void AppendPublishedDirtyRange(in AdvancedGpuDirtyRange range)
    {
        if (_publishedDirtyRangeCount > 0)
        {
            ref AdvancedGpuDirtyRange previous =
                ref _publishedDirtyRanges[_publishedDirtyRangeCount - 1];
            uint previousEnd = checked(previous.Start + previous.Count);
            if (previousEnd == range.Start)
            {
                previous = new AdvancedGpuDirtyRange(
                    previous.Start,
                    checked(previous.Count + range.Count));
                return;
            }
        }

        if (_publishedDirtyRangeCount >= _publishedDirtyRanges.Length)
            throw new InvalidOperationException("Advanced lookup dirty-range capacity is exhausted.");

        _publishedDirtyRanges[_publishedDirtyRangeCount++] = range;
    }

    private static void ClearSourceDirtyRanges(
        AdvancedGpuSceneDatabase scene,
        AdvancedMaterialDatabase materials)
    {
        scene.Draws.ClearLogicalLookupDirtyRange();
        scene.Instances.ClearLogicalLookupDirtyRange();
        scene.Transforms.ClearLogicalLookupDirtyRange();
        scene.Deformations.ClearLogicalLookupDirtyRange();
        scene.RenderStates.ClearLogicalLookupDirtyRange();
        scene.EditorIdentities.ClearLogicalLookupDirtyRange();
        scene.Geometry.Records.ClearLogicalLookupDirtyRange();
        materials.Materials.ClearLogicalLookupDirtyRange();
        materials.Kernels.ClearLogicalLookupDirtyRange();
        materials.Layouts.ClearLogicalLookupDirtyRange();
    }
}

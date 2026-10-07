namespace XREngine.Rendering.Commands;

/// <summary>
/// Fixed-capacity reverse dependency manifests captured at the canonical
/// publication boundary. The arrays are compact, generation-safe handle pairs;
/// consumers never scan mutable resident tables to fan out a dirty owner row.
/// </summary>
public sealed class AdvancedCanonicalReverseDependencyManifest
{
    private readonly AdvancedReverseDependencyEdge[] _materialToDraw;
    private readonly AdvancedReverseDependencyEdge[] _geometryToDraw;
    private readonly AdvancedReverseDependencyEdge[] _textureToMaterial;
    private readonly AdvancedReverseDependencyEdge[] _kernelToMaterial;
    private readonly AdvancedReverseDependencyEdge[] _layoutToMaterial;
    private int _broadFallbackCount;
    private int _materialToDrawCount;
    private int _geometryToDrawCount;
    private int _textureToMaterialCount;
    private int _kernelToMaterialCount;
    private int _layoutToMaterialCount;

    internal AdvancedCanonicalReverseDependencyManifest(
        in AdvancedSharedGpuSceneCapacityProfile capacities)
    {
        int drawCapacity = checked((int)capacities.Scene.DrawRecords);
        int materialCapacity = checked((int)capacities.MaterialRecords);
        _materialToDraw = new AdvancedReverseDependencyEdge[drawCapacity];
        _geometryToDraw = new AdvancedReverseDependencyEdge[drawCapacity];
        _textureToMaterial = new AdvancedReverseDependencyEdge[
            checked((int)capacities.MaterialTextureBindings)];
        _kernelToMaterial = new AdvancedReverseDependencyEdge[materialCapacity];
        _layoutToMaterial = new AdvancedReverseDependencyEdge[materialCapacity];
    }

    public ulong Sequence { get; private set; }
    public bool IsComplete { get; private set; }
    internal string? LastCaptureFailure { get; private set; }
    public uint BroadFallbackCount => unchecked((uint)Volatile.Read(ref _broadFallbackCount));

    public ReadOnlySpan<AdvancedReverseDependencyEdge> MaterialToDraw
        => _materialToDraw.AsSpan(0, _materialToDrawCount);
    public ReadOnlySpan<AdvancedReverseDependencyEdge> GeometryToDraw
        => _geometryToDraw.AsSpan(0, _geometryToDrawCount);
    public ReadOnlySpan<AdvancedReverseDependencyEdge> TextureToMaterial
        => _textureToMaterial.AsSpan(0, _textureToMaterialCount);
    public ReadOnlySpan<AdvancedReverseDependencyEdge> KernelToMaterial
        => _kernelToMaterial.AsSpan(0, _kernelToMaterialCount);
    public ReadOnlySpan<AdvancedReverseDependencyEdge> LayoutToMaterial
        => _layoutToMaterial.AsSpan(0, _layoutToMaterialCount);

    /// <summary>Copies exact dependents into caller-owned storage, reporting a typed broad fallback only when required.</summary>
    public AdvancedReverseDependencyLookup CopyDependents(
        EAdvancedReverseDependencyKind kind,
        AdvancedGpuHandle source,
        Span<AdvancedGpuHandle> destination)
    {
        if (!IsComplete || source.IsValid == false)
            return RecordFallback(EAdvancedReverseDependencyFallback.ManifestUnavailable);

        ReadOnlySpan<AdvancedReverseDependencyEdge> edges = GetEdges(kind);
        int count = 0;
        for (int index = 0; index < edges.Length; ++index)
        {
            if (edges[index].Source != source)
                continue;
            if (count >= destination.Length)
                return RecordFallback(EAdvancedReverseDependencyFallback.DestinationCapacityExceeded);
            destination[count++] = edges[index].Dependent;
        }

        return new AdvancedReverseDependencyLookup(count, EAdvancedReverseDependencyFallback.None);
    }

    internal bool TryCapture(
        ulong sequence,
        AdvancedGpuSceneDatabase scene,
        AdvancedMaterialDatabase materials)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(materials);
        Reset(sequence);

        ReadOnlySpan<AdvancedDrawRecord> drawRecords = scene.Draws.PhysicalRecords;
        ReadOnlySpan<AdvancedGpuHandle> drawHandles = scene.Draws.PhysicalHandles;
        ReadOnlySpan<byte> drawOccupancy = scene.Draws.PhysicalOccupancy;
        for (int index = 0; index < drawRecords.Length; ++index)
        {
            if (drawOccupancy[index] == 0)
                continue;
            AdvancedGpuHandle draw = drawHandles[index];
            // Physical rows remain occupied after tombstoning until every
            // publication consumer acknowledges them. They are not logical
            // dependencies of the next canonical publication.
            if (!draw.IsValid)
                return FailCapture($"Invalid draw handle at physical row {index}");
            if (!scene.Draws.IsCurrent(draw))
                continue;
            ref readonly AdvancedDrawRecord record = ref drawRecords[index];
            if (!record.Material.IsValid)
                return FailCapture($"Draw {draw.Index}:{draw.Generation} at physical row {index} has an invalid material handle {record.Material.Index}:{record.Material.Generation}");
            if (!materials.Materials.IsCurrent(record.Material))
                return FailCapture($"Draw {draw.Index}:{draw.Generation} at physical row {index} has a stale material handle {record.Material.Index}:{record.Material.Generation}");
            if (!record.Geometry.IsValid)
                return FailCapture($"Draw {draw.Index}:{draw.Generation} at physical row {index} has an invalid geometry handle {record.Geometry.Index}:{record.Geometry.Generation}");
            if (!scene.Geometry.Records.IsCurrent(record.Geometry))
                return FailCapture($"Draw {draw.Index}:{draw.Generation} at physical row {index} has a stale geometry handle {record.Geometry.Index}:{record.Geometry.Generation}");
            if (_materialToDrawCount >= _materialToDraw.Length)
                return FailCapture($"Material-to-draw edge capacity exceeded at draw physical row {index}, handle {draw.Index}:{draw.Generation}");
            if (_geometryToDrawCount >= _geometryToDraw.Length)
                return FailCapture($"Geometry-to-draw edge capacity exceeded at draw physical row {index}, handle {draw.Index}:{draw.Generation}");
            _materialToDraw[_materialToDrawCount++] = new(record.Material, draw);
            _geometryToDraw[_geometryToDrawCount++] = new(record.Geometry, draw);
        }

        ReadOnlySpan<AdvancedMaterialRecord> materialRecords = materials.Materials.PhysicalRecords;
        ReadOnlySpan<AdvancedGpuHandle> materialHandles = materials.Materials.PhysicalHandles;
        ReadOnlySpan<byte> materialOccupancy = materials.Materials.PhysicalOccupancy;
        for (int index = 0; index < materialRecords.Length; ++index)
        {
            if (materialOccupancy[index] == 0)
                continue;
            AdvancedGpuHandle material = materialHandles[index];
            if (!material.IsValid)
                return FailCapture($"Invalid material handle at physical row {index}");
            if (!materials.Materials.IsCurrent(material))
                continue;
            ref readonly AdvancedMaterialRecord record = ref materialRecords[index];
            if (!materials.TryGetLayoutHandle(material, out AdvancedGpuHandle layout))
                return FailCapture($"Material {material.Index}:{material.Generation} at physical row {index} has a {(layout.IsValid ? "stale" : "missing or invalid")} layout handle {layout.Index}:{layout.Generation}");
            AdvancedGpuHandle kernel = new(record.ShadingKernelId, record.ShadingKernelGeneration);
            if (!kernel.IsValid)
                return FailCapture($"Material {material.Index}:{material.Generation} at physical row {index} has an invalid kernel handle {kernel.Index}:{kernel.Generation}");
            if (!materials.Kernels.TryGet(kernel, out _))
                return FailCapture($"Material {material.Index}:{material.Generation} at physical row {index} has a stale kernel handle {kernel.Index}:{kernel.Generation}");
            if (_kernelToMaterialCount >= _kernelToMaterial.Length)
                return FailCapture($"Kernel-to-material edge capacity exceeded at material physical row {index}, handle {material.Index}:{material.Generation}");
            if (_layoutToMaterialCount >= _layoutToMaterial.Length)
                return FailCapture($"Layout-to-material edge capacity exceeded at material physical row {index}, handle {material.Index}:{material.Generation}");
            if (!materials.TryGetTextureBindings(record, out ReadOnlySpan<AdvancedMaterialTextureBinding> bindings))
                return FailCapture($"Material {material.Index}:{material.Generation} at physical row {index} has an invalid texture binding range {record.TextureReferenceOffset}+{record.TextureReferenceCount}/{materials.TextureBindings.Length}");

            _kernelToMaterial[_kernelToMaterialCount++] = new(kernel, material);
            _layoutToMaterial[_layoutToMaterialCount++] = new(layout, material);
            for (int bindingIndex = 0; bindingIndex < bindings.Length; ++bindingIndex)
            {
                AdvancedGpuHandle texture = bindings[bindingIndex].Texture.Handle;
                if (!texture.IsValid)
                    continue;
                if (_textureToMaterialCount >= _textureToMaterial.Length)
                    return FailCapture($"Texture-to-material edge capacity exceeded at material physical row {index}, binding {bindingIndex}, texture {texture.Index}:{texture.Generation}");
                _textureToMaterial[_textureToMaterialCount++] = new(texture, material);
            }
        }

        IsComplete = true;
        return true;
    }

    private ReadOnlySpan<AdvancedReverseDependencyEdge> GetEdges(EAdvancedReverseDependencyKind kind)
        => kind switch
        {
            EAdvancedReverseDependencyKind.MaterialToDraw => MaterialToDraw,
            EAdvancedReverseDependencyKind.GeometryToDraw => GeometryToDraw,
            EAdvancedReverseDependencyKind.TextureToMaterial => TextureToMaterial,
            EAdvancedReverseDependencyKind.KernelToMaterial => KernelToMaterial,
            EAdvancedReverseDependencyKind.LayoutToMaterial => LayoutToMaterial,
            _ => ReadOnlySpan<AdvancedReverseDependencyEdge>.Empty,
        };

    private void Reset(ulong sequence)
    {
        Sequence = sequence;
        IsComplete = false;
        LastCaptureFailure = null;
        _materialToDrawCount = 0;
        _geometryToDrawCount = 0;
        _textureToMaterialCount = 0;
        _kernelToMaterialCount = 0;
        _layoutToMaterialCount = 0;
    }

    private bool FailCapture(string reason)
    {
        LastCaptureFailure =
            $"{reason}; sequence={Sequence}, materialToDraw={_materialToDrawCount}/{_materialToDraw.Length}, " +
            $"geometryToDraw={_geometryToDrawCount}/{_geometryToDraw.Length}, " +
            $"textureToMaterial={_textureToMaterialCount}/{_textureToMaterial.Length}, " +
            $"kernelToMaterial={_kernelToMaterialCount}/{_kernelToMaterial.Length}, " +
            $"layoutToMaterial={_layoutToMaterialCount}/{_layoutToMaterial.Length}";
        Interlocked.Increment(ref _broadFallbackCount);
        return false;
    }

    private AdvancedReverseDependencyLookup RecordFallback(EAdvancedReverseDependencyFallback fallback)
    {
        Interlocked.Increment(ref _broadFallbackCount);
        return new AdvancedReverseDependencyLookup(0, fallback);
    }
}

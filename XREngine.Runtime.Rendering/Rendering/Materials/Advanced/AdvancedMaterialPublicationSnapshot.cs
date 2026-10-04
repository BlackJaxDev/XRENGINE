namespace XREngine.Rendering;

/// <summary>
/// Retained, database-owned material payload image. Its arrays are allocated at
/// setup/growth time; sealing only copies canonical bytes so later mutations can
/// never change a previously published material payload.
/// </summary>
public sealed class AdvancedMaterialPublicationSnapshot
{
    private readonly AdvancedGpuHandle[] _layoutHandles;
    private readonly AdvancedMaterialLayoutMember[] _layoutMembers;
    private readonly uint[] _constantWords;
    private readonly AdvancedMaterialTextureBinding[] _textureBindings;
    private readonly AdvancedEngineSurfaceRecord[] _engineSurfaces;
    private readonly AdvancedUberBaseSurfaceRecord[] _uberBaseSurfaces;
    private readonly AdvancedNativeVertexMaterial[] _nativeVertices;
    private int _layoutMemberCount;
    private int _constantWordCount;
    private int _textureBindingCount;

    internal AdvancedMaterialPublicationSnapshot(
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedMaterialRecord> materials,
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedShadingKernelRecord> kernels,
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedMaterialLayoutRecord> layouts,
        int layoutHandleCapacity,
        int layoutMemberCapacity,
        int constantWordCapacity,
        int textureBindingCapacity,
        int engineSurfaceCapacity)
    {
        Materials = materials ?? throw new ArgumentNullException(nameof(materials));
        Kernels = kernels ?? throw new ArgumentNullException(nameof(kernels));
        Layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
        _layoutHandles = new AdvancedGpuHandle[layoutHandleCapacity];
        _layoutMembers = new AdvancedMaterialLayoutMember[layoutMemberCapacity];
        _constantWords = new uint[constantWordCapacity];
        _textureBindings = new AdvancedMaterialTextureBinding[textureBindingCapacity];
        _engineSurfaces = new AdvancedEngineSurfaceRecord[engineSurfaceCapacity];
        _uberBaseSurfaces = new AdvancedUberBaseSurfaceRecord[engineSurfaceCapacity];
        _nativeVertices = new AdvancedNativeVertexMaterial[engineSurfaceCapacity];
    }

    public ulong Sequence { get; private set; }
    public AdvancedMaterialDatabaseGenerations Generations { get; private set; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedMaterialRecord> Materials { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedShadingKernelRecord> Kernels { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedMaterialLayoutRecord> Layouts { get; }
    public ReadOnlySpan<AdvancedGpuHandle> MaterialLayoutHandles => _layoutHandles;
    public ReadOnlySpan<AdvancedMaterialLayoutMember> LayoutMembers
        => _layoutMembers.AsSpan(0, _layoutMemberCount);
    public ReadOnlySpan<uint> ConstantWords
        => _constantWords.AsSpan(0, _constantWordCount);
    public ReadOnlySpan<AdvancedMaterialTextureBinding> TextureBindings
        => _textureBindings.AsSpan(0, _textureBindingCount);

    public ReadOnlySpan<AdvancedEngineSurfaceRecord> EngineSurfaces => _engineSurfaces;
    internal int EngineSurfaceCapacity => _engineSurfaces.Length;
    public ReadOnlySpan<AdvancedUberBaseSurfaceRecord> UberBaseSurfaces => _uberBaseSurfaces;
    internal int UberBaseSurfaceCapacity => _uberBaseSurfaces.Length;

    internal int LayoutHandleCapacity => _layoutHandles.Length;
    internal int LayoutMemberCapacity => _layoutMembers.Length;
    internal int ConstantWordCapacity => _constantWords.Length;
    internal int TextureBindingCapacity => _textureBindings.Length;

    /// <summary>Releases managed program closures only after the publication's final pin is retired.</summary>
    internal void ReleaseRetainedSources() => Array.Clear(_nativeVertices);

    internal bool TryCapture(
        ulong sequence,
        ReadOnlySpan<AdvancedGpuHandle> layoutHandles,
        ReadOnlySpan<AdvancedMaterialLayoutMember> layoutMembers,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings,
        ReadOnlySpan<AdvancedEngineSurfaceRecord> engineSurfaces,
        ReadOnlySpan<AdvancedUberBaseSurfaceRecord> uberBaseSurfaces,
        ReadOnlySpan<AdvancedNativeVertexMaterial> nativeVertices,
        in AdvancedGpuOwnerGenerations materialGeneration,
        in AdvancedGpuOwnerGenerations kernelGeneration,
        in AdvancedGpuOwnerGenerations layoutGeneration)
    {
        Sequence = 0u;
        if (sequence == 0u ||
            layoutHandles.Length > _layoutHandles.Length ||
            layoutMembers.Length > _layoutMembers.Length ||
            constantWords.Length > _constantWords.Length ||
            textureBindings.Length > _textureBindings.Length ||
            engineSurfaces.Length > _engineSurfaces.Length ||
            uberBaseSurfaces.Length > _uberBaseSurfaces.Length ||
            nativeVertices.Length > _nativeVertices.Length)
        {
            return false;
        }

        layoutHandles.CopyTo(_layoutHandles);
        layoutMembers.CopyTo(_layoutMembers);
        constantWords.CopyTo(_constantWords);
        textureBindings.CopyTo(_textureBindings);
        engineSurfaces.CopyTo(_engineSurfaces);
        _engineSurfaces.AsSpan(engineSurfaces.Length).Clear();
        uberBaseSurfaces.CopyTo(_uberBaseSurfaces);
        _uberBaseSurfaces.AsSpan(uberBaseSurfaces.Length).Clear();
        nativeVertices.CopyTo(_nativeVertices);
        _nativeVertices.AsSpan(nativeVertices.Length).Clear();
        _layoutMemberCount = layoutMembers.Length;
        _constantWordCount = constantWords.Length;
        _textureBindingCount = textureBindings.Length;
        Sequence = sequence;
        Generations = new AdvancedMaterialDatabaseGenerations(materialGeneration, kernelGeneration, layoutGeneration);
        return true;
    }

    public bool TryGetEngineSurface(in AdvancedMaterialRecord material, out AdvancedEngineSurfaceRecord surface)
    {
        if (material.StableRowId >= (uint)_engineSurfaces.Length ||
            !Materials.TryGetDenseIndex(new(material.StableRowId, material.Generation), out _))
        {
            surface = default;
            return false;
        }
        surface = _engineSurfaces[checked((int)material.StableRowId)];
        return surface.SchemaVersion == 0 || surface.Generation == material.Generation;
    }

    /// <summary>Returns an exact copied Uber companion for the retained material generation.</summary>
    public bool TryGetUberBaseSurface(in AdvancedMaterialRecord material, out AdvancedUberBaseSurfaceRecord surface)
    {
        if (material.StableRowId >= (uint)_uberBaseSurfaces.Length ||
            !Materials.TryGetDenseIndex(new(material.StableRowId, material.Generation), out _))
        {
            surface = default;
            return false;
        }
        surface = _uberBaseSurfaces[checked((int)material.StableRowId)];
        return surface.SchemaVersion == 0 || surface.Generation == material.Generation;
    }

    /// <summary>Reads copied inputs only from the same retained material generation.</summary>
    public bool TryGetNativeVertex(in AdvancedMaterialRecord material, out AdvancedNativeVertexMaterial vertex)
    {
        if (material.StableRowId >= (uint)_nativeVertices.Length ||
            !Materials.TryGetDenseIndex(new(material.StableRowId, material.Generation), out _))
        {
            vertex = default;
            return false;
        }
        vertex = _nativeVertices[checked((int)material.StableRowId)];
        return true;
    }

    public bool TryGetLayoutHandle(
        AdvancedGpuHandle materialHandle,
        out AdvancedGpuHandle layoutHandle)
    {
        if (!Materials.TryGetDenseIndex(materialHandle, out _) ||
            materialHandle.Index >= (uint)_layoutHandles.Length)
        {
            layoutHandle = AdvancedGpuHandle.Invalid;
            return false;
        }

        layoutHandle = _layoutHandles[checked((int)materialHandle.Index)];
        return layoutHandle.IsValid && Layouts.TryGetDenseIndex(layoutHandle, out _);
    }

    public bool TryGetTextureBindings(
        in AdvancedMaterialRecord material,
        out ReadOnlySpan<AdvancedMaterialTextureBinding> bindings)
    {
        ulong end = (ulong)material.TextureReferenceOffset +
            material.TextureReferenceCount;
        if (end > (ulong)_textureBindingCount)
        {
            bindings = default;
            return false;
        }

        bindings = _textureBindings.AsSpan(
            checked((int)material.TextureReferenceOffset),
            checked((int)material.TextureReferenceCount));
        return true;
    }

    public bool TryGetConstantWords(
        in AdvancedMaterialRecord material,
        out ReadOnlySpan<uint> words)
    {
        ulong end = (ulong)material.ConstantWordOffset +
            material.ConstantWordCount;
        if (end > (ulong)_constantWordCount)
        {
            words = default;
            return false;
        }

        words = _constantWords.AsSpan(
            checked((int)material.ConstantWordOffset),
            checked((int)material.ConstantWordCount));
        return true;
    }

    public bool TryGetLayoutMembers(
        in AdvancedMaterialLayoutRecord layout,
        out ReadOnlySpan<AdvancedMaterialLayoutMember> members)
    {
        ulong end = (ulong)layout.MemberOffset + layout.MemberCount;
        if (end > (ulong)_layoutMemberCount)
        {
            members = default;
            return false;
        }

        members = _layoutMembers.AsSpan(
            checked((int)layout.MemberOffset),
            checked((int)layout.MemberCount));
        return true;
    }
}

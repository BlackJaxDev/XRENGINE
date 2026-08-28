using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Fixed-capacity, generation-checked material, kernel, and layout database.
/// Growth is an explicit frame-boundary operation on the underlying tables.
/// </summary>
public sealed class AdvancedMaterialDatabase
{
    private readonly AdvancedGpuRecordTable<AdvancedMaterialRecord> _materials;
    private readonly AdvancedGpuRecordTable<AdvancedShadingKernelRecord> _kernels;
    private readonly AdvancedGpuRecordTable<AdvancedMaterialLayoutRecord> _layouts;
    private AdvancedMaterialLayoutMember[] _layoutMembers;
    private uint[] _constantWords;
    private AdvancedMaterialTextureBinding[] _textureBindings;
    private AdvancedGpuHandle[] _materialLayoutHandles;
    private readonly uint _maximumConstantWordsPerMaterial;
    private readonly uint _maximumTextureBindingsPerMaterial;
    private uint _layoutMemberCount;
    private uint _constantWordCount;
    private uint _textureBindingCount;
    private bool _materialDirty;
    private uint _materialDirtyFirst;
    private uint _materialDirtyEnd;
    private uint _constantDirtyFirst = uint.MaxValue;
    private uint _constantDirtyEnd;
    private uint _textureDirtyFirst = uint.MaxValue;
    private uint _textureDirtyEnd;
    private ulong _materialGeneration;
    private ulong _kernelGeneration;
    private ulong _layoutGeneration;

    public AdvancedMaterialDatabase(
        uint materialCapacity,
        uint kernelCapacity,
        uint layoutCapacity,
        uint layoutMemberCapacity,
        uint constantWordCapacity = 0u,
        uint textureBindingCapacity = 0u,
        uint maximumConstantWordsPerMaterial = 0u,
        uint maximumTextureBindingsPerMaterial = 0u)
    {
        _materials = new AdvancedGpuRecordTable<AdvancedMaterialRecord>(materialCapacity);
        _kernels = new AdvancedGpuRecordTable<AdvancedShadingKernelRecord>(kernelCapacity);
        _layouts = new AdvancedGpuRecordTable<AdvancedMaterialLayoutRecord>(layoutCapacity);
        _layoutMembers = new AdvancedMaterialLayoutMember[checked((int)layoutMemberCapacity)];
        _maximumConstantWordsPerMaterial = maximumConstantWordsPerMaterial == 0u
            ? Math.Max(1u, materialCapacity == 0u ? 1u : constantWordCapacity / materialCapacity)
            : maximumConstantWordsPerMaterial;
        _maximumTextureBindingsPerMaterial = maximumTextureBindingsPerMaterial == 0u
            ? Math.Max(1u, materialCapacity == 0u ? 1u : textureBindingCapacity / materialCapacity)
            : maximumTextureBindingsPerMaterial;
        uint fixedConstantCapacity = checked((materialCapacity + 1u) * _maximumConstantWordsPerMaterial);
        uint fixedTextureCapacity = checked((materialCapacity + 1u) * _maximumTextureBindingsPerMaterial);
        _constantWords = new uint[checked((int)Math.Max(constantWordCapacity, fixedConstantCapacity))];
        _textureBindings = new AdvancedMaterialTextureBinding[checked((int)Math.Max(textureBindingCapacity, fixedTextureCapacity))];
        _materialLayoutHandles = new AdvancedGpuHandle[checked((int)materialCapacity + 1)];
    }

    public AdvancedGpuRecordTable<AdvancedMaterialRecord> Materials => _materials;
    public AdvancedGpuRecordTable<AdvancedShadingKernelRecord> Kernels => _kernels;
    public AdvancedGpuRecordTable<AdvancedMaterialLayoutRecord> Layouts => _layouts;
    public ReadOnlySpan<AdvancedMaterialLayoutMember> LayoutMembers => _layoutMembers.AsSpan(0, checked((int)_layoutMemberCount));
    public ReadOnlySpan<uint> ConstantWords => _constantWords.AsSpan(0, checked((int)_constantWordCount));
    public ReadOnlySpan<AdvancedMaterialTextureBinding> TextureBindings => _textureBindings.AsSpan(0, checked((int)_textureBindingCount));
    public uint MaximumConstantWordsPerMaterial => _maximumConstantWordsPerMaterial;
    public uint MaximumTextureBindingsPerMaterial => _maximumTextureBindingsPerMaterial;
    public uint LayoutMemberCount => _layoutMemberCount;
    public uint LayoutMemberCapacity => checked((uint)_layoutMembers.Length);

    /// <summary>Allocates a ring-owned immutable publication image at a setup or growth boundary.</summary>
    public AdvancedMaterialPublicationSnapshot CreatePublicationSnapshot()
        => new(
            _materials.CreatePublicationSnapshot(includeRecordImage: true),
            _kernels.CreatePublicationSnapshot(includeRecordImage: true),
            _layouts.CreatePublicationSnapshot(includeRecordImage: true),
            _materialLayoutHandles.Length,
            _layoutMembers.Length,
            _constantWords.Length,
            _textureBindings.Length);

    internal bool CanSealPublication(AdvancedMaterialPublicationSnapshot snapshot)
        => snapshot.LayoutHandleCapacity >= _materialLayoutHandles.Length &&
           snapshot.LayoutMemberCapacity >= _layoutMembers.Length &&
           snapshot.ConstantWordCapacity >= _constantWords.Length &&
           snapshot.TextureBindingCapacity >= _textureBindings.Length;

    /// <summary>Copies canonical material payload state into a retained publication image without allocating.</summary>
    public bool TrySealPublication(ulong publicationSequence, AdvancedMaterialPublicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (publicationSequence == 0u)
            return false;

        return snapshot.TryCapture(
            publicationSequence,
            _materialLayoutHandles,
            LayoutMembers,
            ConstantWords,
            TextureBindings,
            _materials.Generations,
            _kernels.Generations,
            _layouts.Generations);
    }

    /// <summary>Resolves the stable layout handle used by a packed material row.</summary>
    public bool TryFindLayoutHandle(ulong layoutHash, out AdvancedGpuHandle handle)
    {
        ReadOnlySpan<AdvancedMaterialLayoutRecord> records = _layouts.PhysicalRecords;
        ReadOnlySpan<AdvancedGpuHandle> handles = _layouts.PhysicalHandles;
        ReadOnlySpan<byte> occupancy = _layouts.PhysicalOccupancy;
        for (int index = 0; index < records.Length; ++index)
        {
            if (occupancy[index] == 0 || records[index].LayoutHash != layoutHash)
                continue;

            handle = handles[index];
            return handle.IsValid;
        }

        handle = AdvancedGpuHandle.Invalid;
        return false;
    }

    /// <summary>Finds an existing layout only when every packed field agrees.</summary>
    public bool TryFindLayoutHandle(in AdvancedMaterialLayoutRecord source, ReadOnlySpan<AdvancedMaterialLayoutMember> members, out AdvancedGpuHandle handle)
    {
        ReadOnlySpan<AdvancedMaterialLayoutRecord> records = _layouts.PhysicalRecords;
        ReadOnlySpan<AdvancedGpuHandle> handles = _layouts.PhysicalHandles;
        ReadOnlySpan<byte> occupancy = _layouts.PhysicalOccupancy;
        for (int index = 0; index < records.Length; ++index)
        {
            if (occupancy[index] == 0 || !LayoutEquals(in records[index], in source) || !GetLayoutMembers(records[index]).SequenceEqual(members))
                continue;
            handle = handles[index];
            return handle.IsValid;
        }
        handle = AdvancedGpuHandle.Invalid;
        return false;
    }

    /// <summary>Finds an existing fully initialized kernel under its exact layout identity.</summary>
    public bool TryFindKernelHandle(AdvancedGpuHandle layoutHandle, in AdvancedShadingKernelRecord source, out AdvancedGpuHandle handle)
    {
        if (!_layouts.TryGet(layoutHandle, out AdvancedMaterialLayoutRecord layout))
        {
            handle = AdvancedGpuHandle.Invalid;
            return false;
        }
        ReadOnlySpan<AdvancedShadingKernelRecord> records = _kernels.PhysicalRecords;
        ReadOnlySpan<AdvancedGpuHandle> handles = _kernels.PhysicalHandles;
        ReadOnlySpan<byte> occupancy = _kernels.PhysicalOccupancy;
        for (int index = 0; index < records.Length; ++index)
        {
            if (occupancy[index] == 0 || records[index].MaterialLayoutHash != layout.LayoutHash || !KernelEquals(in records[index], in source))
                continue;
            handle = handles[index];
            return handle.IsValid;
        }
        handle = AdvancedGpuHandle.Invalid;
        return false;
    }

    /// <summary>
    /// Returns the immutable texture/sampler reference range owned by a packed
    /// material row. Invalid ranges fail closed instead of exposing arena data.
    /// </summary>
    public bool TryGetTextureBindings(
        in AdvancedMaterialRecord material,
        out ReadOnlySpan<AdvancedMaterialTextureBinding> bindings)
    {
        ulong end = (ulong)material.TextureReferenceOffset + material.TextureReferenceCount;
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

    /// <summary>Returns a material's fixed-stride constant payload, bounded by the row's authored count.</summary>
    public bool TryGetConstantWords(in AdvancedMaterialRecord material, out ReadOnlySpan<uint> words)
    {
        ulong end = (ulong)material.ConstantWordOffset + material.ConstantWordCount;
        if (end > (ulong)_constantWords.Length)
        {
            words = default;
            return false;
        }
        words = _constantWords.AsSpan(checked((int)material.ConstantWordOffset), checked((int)material.ConstantWordCount));
        return true;
    }

    /// <summary>Resolves the exact generation-safe layout identity retained by a material row.</summary>
    public bool TryGetLayoutHandle(
        AdvancedGpuHandle materialHandle,
        out AdvancedGpuHandle layoutHandle)
    {
        if (!_materials.IsCurrent(materialHandle) ||
            materialHandle.Index >= (uint)_materialLayoutHandles.Length)
        {
            layoutHandle = AdvancedGpuHandle.Invalid;
            return false;
        }

        layoutHandle = _materialLayoutHandles[checked((int)materialHandle.Index)];
        return layoutHandle.IsValid && _layouts.IsCurrent(layoutHandle);
    }
    /// <summary>Independent owner-domain versions captured with publication payloads.</summary>
    public AdvancedMaterialDatabaseGenerations Generations
        => new(_materials.Generations, _kernels.Generations, _layouts.Generations);

    public bool TryAddLayout(
        in AdvancedMaterialLayoutRecord source,
        ReadOnlySpan<AdvancedMaterialLayoutMember> members,
        out AdvancedGpuHandle handle)
    {
        handle = AdvancedGpuHandle.Invalid;
        if (!ValidateLayoutMembers(source, members))
            return false;
        if ((uint)members.Length > (uint)_layoutMembers.Length - _layoutMemberCount)
            return false;

        AdvancedMaterialLayoutRecord record = source;
        record.MemberOffset = _layoutMemberCount;
        record.MemberCount = checked((uint)members.Length);
        record.StableLayoutId = 0u;
        record.Generation = 0u;
        if (!_layouts.TryAdd(record, out handle))
            return false;

        record.StableLayoutId = handle.Index;
        record.Generation = handle.Generation;
        if (!_layouts.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted material layout could not be initialized.");

        members.CopyTo(_layoutMembers.AsSpan(checked((int)_layoutMemberCount)));
        _layoutMemberCount += checked((uint)members.Length);
        IncrementGeneration(ref _layoutGeneration);
        return true;
    }

    public bool TryAddKernel(
        AdvancedGpuHandle layoutHandle,
        in AdvancedShadingKernelRecord source,
        out AdvancedGpuHandle handle)
    {
        handle = AdvancedGpuHandle.Invalid;
        if (!_layouts.TryGet(layoutHandle, out AdvancedMaterialLayoutRecord layout))
            return false;

        AdvancedShadingKernelRecord record = source;
        record.StableKernelId = 0u;
        record.Generation = 0u;
        record.MaterialLayoutHash = layout.LayoutHash;
        record.RequiredAttributeMask |= layout.RequiredAttributeMask;
        if (!_kernels.TryAdd(record, out handle))
            return false;

        record.StableKernelId = handle.Index;
        record.Generation = handle.Generation;
        if (!_kernels.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted shading kernel could not be initialized.");

        IncrementGeneration(ref _kernelGeneration);
        return true;
    }

    public bool TryAddMaterial(
        AdvancedGpuHandle layoutHandle,
        AdvancedGpuHandle kernelHandle,
        in AdvancedMaterialRecord source,
        out AdvancedGpuHandle handle)
        => TryAddMaterial(
            layoutHandle,
            kernelHandle,
            source,
            ReadOnlySpan<AdvancedMaterialValueDescriptor>.Empty,
            ReadOnlySpan<uint>.Empty,
            ReadOnlySpan<AdvancedMaterialTextureBinding>.Empty,
            out handle);

    /// <summary>
    /// Adds a fully packed material row after validating every authored semantic
    /// against its declared layout. Payload storage is a fixed logical slot keyed
    /// by the generation-safe material handle.
    /// </summary>
    public bool TryAddMaterial(
        AdvancedGpuHandle layoutHandle,
        AdvancedGpuHandle kernelHandle,
        in AdvancedMaterialRecord source,
        ReadOnlySpan<AdvancedMaterialValueDescriptor> values,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings,
        out AdvancedGpuHandle handle)
    {
        handle = AdvancedGpuHandle.Invalid;
        if (_materials.Count >= _materials.Capacity ||
            !TryPrepareMaterial(
                layoutHandle,
                kernelHandle,
                source,
                values,
                constantWords,
                textureBindings,
                out AdvancedMaterialRecord record))
            return false;

        if (!_materials.TryAdd(record, out handle))
            return false;

        record.ConstantWordOffset = GetConstantOffset(handle.Index);
        record.ConstantWordCount = checked((uint)constantWords.Length);
        record.TextureReferenceOffset = GetTextureOffset(handle.Index);
        record.TextureReferenceCount = checked((uint)textureBindings.Length);
        record.StableRowId = handle.Index;
        record.Generation = handle.Generation;
        if (!_materials.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted material row could not be initialized.");

        _materialLayoutHandles[checked((int)handle.Index)] = layoutHandle;
        WriteMaterialPayload(handle.Index, constantWords, textureBindings);
        MarkMaterialDirty(handle);
        IncrementGeneration(ref _materialGeneration);
        return true;
    }

    /// <summary>
    /// Interns an exact layout and kernel schema before adding an exact material
    /// payload. Every input and fixed-capacity journal is preflighted before the
    /// first mutation, so a rejected request leaves the database unchanged.
    /// </summary>
    public bool TryAddMaterialWithInternedSchema(
        in AdvancedMaterialLayoutRecord layoutSource,
        ReadOnlySpan<AdvancedMaterialLayoutMember> layoutMembers,
        in AdvancedShadingKernelRecord kernelSource,
        in AdvancedMaterialRecord materialSource,
        ReadOnlySpan<AdvancedMaterialValueDescriptor> values,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings,
        out AdvancedMaterialVariantHandles handles,
        out EAdvancedMaterialVariantCreationFailure failure)
    {
        handles = default;
        failure = EAdvancedMaterialVariantCreationFailure.None;

        bool hasLayout = TryFindLayoutHandle(layoutSource, layoutMembers, out AdvancedGpuHandle layoutHandle);
        AdvancedMaterialLayoutRecord layout;
        if (hasLayout)
        {
            if (!_layouts.TryGet(layoutHandle, out layout))
                throw new InvalidOperationException("Interned material layout disappeared before compound creation.");
        }
        else
        {
            if (!ValidateLayoutMembers(layoutSource, layoutMembers))
            {
                failure = EAdvancedMaterialVariantCreationFailure.InvalidLayout;
                return false;
            }
            if ((uint)layoutMembers.Length > (uint)_layoutMembers.Length - _layoutMemberCount)
            {
                failure = EAdvancedMaterialVariantCreationFailure.LayoutMemberCapacity;
                return false;
            }
            if (!_layouts.CanApply(addCount: 1, replaceCount: 1, tombstoneCount: 0))
            {
                failure = EAdvancedMaterialVariantCreationFailure.LayoutPublicationCapacity;
                return false;
            }

            layout = layoutSource;
            layout.MemberOffset = _layoutMemberCount;
            layout.MemberCount = checked((uint)layoutMembers.Length);
            layout.StableLayoutId = 0u;
            layout.Generation = 0u;
        }

        AdvancedShadingKernelRecord kernel = PrepareKernelRecord(in layout, in kernelSource);
        AdvancedGpuHandle kernelHandle = AdvancedGpuHandle.Invalid;
        bool hasKernel = hasLayout && TryFindKernelHandle(layoutHandle, kernel, out kernelHandle);
        if (hasKernel)
        {
            if (!_kernels.TryGet(kernelHandle, out kernel))
                throw new InvalidOperationException("Interned shading kernel disappeared before compound creation.");
        }
        else if (!_kernels.CanApply(addCount: 1, replaceCount: 1, tombstoneCount: 0))
        {
            failure = EAdvancedMaterialVariantCreationFailure.KernelPublicationCapacity;
            return false;
        }

        if (!TryPrepareMaterial(
                in layout,
                in kernel,
                hasKernel ? kernelHandle : AdvancedGpuHandle.Invalid,
                hasLayout ? GetLayoutMembers(layout) : layoutMembers,
                materialSource,
                values,
                constantWords,
                textureBindings,
                out _))
        {
            failure = EAdvancedMaterialVariantCreationFailure.InvalidMaterial;
            return false;
        }

        if (!_materials.CanApply(addCount: 1, replaceCount: 1, tombstoneCount: 0))
        {
            failure = EAdvancedMaterialVariantCreationFailure.MaterialPublicationCapacity;
            return false;
        }

        if (!hasLayout)
        {
            if (!TryAddLayout(layoutSource, layoutMembers, out layoutHandle))
                throw new InvalidOperationException("Preflighted material layout insertion failed.");
        }
        if (!hasKernel)
        {
            if (!TryAddKernel(layoutHandle, kernelSource, out kernelHandle))
                throw new InvalidOperationException("Preflighted shading kernel insertion failed.");
        }
        if (!TryAddMaterial(
                layoutHandle,
                kernelHandle,
                materialSource,
                values,
                constantWords,
                textureBindings,
                out AdvancedGpuHandle materialHandle))
        {
            throw new InvalidOperationException("Preflighted material insertion failed.");
        }

        handles = new(layoutHandle, kernelHandle, materialHandle);
        return true;
    }

    public bool TryReplaceMaterial(
        AdvancedGpuHandle materialHandle,
        AdvancedGpuHandle layoutHandle,
        AdvancedGpuHandle kernelHandle,
        in AdvancedMaterialRecord source)
        => TryReplaceMaterial(
            materialHandle,
            layoutHandle,
            kernelHandle,
            source,
            ReadOnlySpan<AdvancedMaterialValueDescriptor>.Empty,
            ReadOnlySpan<uint>.Empty,
            ReadOnlySpan<AdvancedMaterialTextureBinding>.Empty);

    /// <summary>
    /// Replaces a material header and overwrites its bounded fixed-slot payload.
    /// </summary>
    public bool TryReplaceMaterial(
        AdvancedGpuHandle materialHandle,
        AdvancedGpuHandle layoutHandle,
        AdvancedGpuHandle kernelHandle,
        in AdvancedMaterialRecord source,
        ReadOnlySpan<AdvancedMaterialValueDescriptor> values,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings)
    {
        if (!_materials.TryGet(materialHandle, out AdvancedMaterialRecord previous))
            return false;
        if (!TryPrepareMaterial(
                layoutHandle,
                kernelHandle,
                source,
                values,
                constantWords,
                textureBindings,
                out AdvancedMaterialRecord record))
            return false;

        record.ConstantWordOffset = GetConstantOffset(materialHandle.Index);
        record.ConstantWordCount = checked((uint)constantWords.Length);
        record.TextureReferenceOffset = GetTextureOffset(materialHandle.Index);
        record.TextureReferenceCount = checked((uint)textureBindings.Length);
        record.StableRowId = materialHandle.Index;
        record.Generation = materialHandle.Generation;
        EAdvancedGpuMutationDomain mutationDomain = ResolveMaterialMutationDomain(
            in previous,
            in record,
            textureBindings);
        if (!_materials.TryReplace(materialHandle, record, mutationDomain))
            return false;

        _materialLayoutHandles[checked((int)materialHandle.Index)] = layoutHandle;
        WriteMaterialPayload(materialHandle.Index, constantWords, textureBindings);
        MarkMaterialDirty(materialHandle);
        IncrementGeneration(ref _materialGeneration);
        return true;
    }

    public bool RemoveMaterial(AdvancedGpuHandle materialHandle)
    {
        if (!_materials.TryGetDenseIndex(materialHandle, out uint denseIndex))
            return false;
        if (!_materials.TryTombstone(materialHandle))
            return false;

        _materialLayoutHandles[checked((int)materialHandle.Index)] =
            AdvancedGpuHandle.Invalid;
        MarkMaterialDirty(denseIndex);
        IncrementGeneration(ref _materialGeneration);
        return true;
    }

    public bool RemoveKernel(AdvancedGpuHandle kernelHandle)
    {
        if (!_kernels.TryTombstone(kernelHandle))
            return false;

        IncrementGeneration(ref _kernelGeneration);
        return true;
    }

    public bool RemoveLayout(AdvancedGpuHandle layoutHandle)
    {
        if (!_layouts.TryTombstone(layoutHandle))
            return false;

        IncrementGeneration(ref _layoutGeneration);
        return true;
    }

    public AdvancedMaterialValidationResult ValidateValues(
        AdvancedGpuHandle layoutHandle,
        ReadOnlySpan<AdvancedMaterialValueDescriptor> values)
    {
        if (!_layouts.TryGet(layoutHandle, out AdvancedMaterialLayoutRecord layout))
        {
            return AdvancedMaterialValidationResult.Invalid(
                EAdvancedMaterialValidationFailure.InvalidLayoutHandle,
                0u,
                0ul);
        }

        return ValidateValues(in layout, values);
    }

    private AdvancedMaterialValidationResult ValidateValues(
        in AdvancedMaterialLayoutRecord layout,
        ReadOnlySpan<AdvancedMaterialValueDescriptor> values)
        => ValidateValues(GetLayoutMembers(layout), values);

    private static AdvancedMaterialValidationResult ValidateValues(
        ReadOnlySpan<AdvancedMaterialLayoutMember> declared,
        ReadOnlySpan<AdvancedMaterialValueDescriptor> values)
    {
        for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
        {
            AdvancedMaterialValueDescriptor value = values[valueIndex];
            if (value.ElementCount == 0u ||
                (uint)value.Kind > (uint)EAdvancedMaterialValueKind.Sampler)
            {
                return AdvancedMaterialValidationResult.Invalid(
                    EAdvancedMaterialValidationFailure.ValueKindMismatch,
                    checked((uint)valueIndex),
                    value.SemanticHash);
            }

            for (int priorIndex = 0; priorIndex < valueIndex; priorIndex++)
            {
                if (values[priorIndex].SemanticHash == value.SemanticHash)
                {
                    return AdvancedMaterialValidationResult.Invalid(
                        EAdvancedMaterialValidationFailure.DuplicateValue,
                        checked((uint)valueIndex),
                        value.SemanticHash);
                }
            }

            int memberIndex = FindMember(declared, value.SemanticHash);
            if (memberIndex < 0)
            {
                return AdvancedMaterialValidationResult.Invalid(
                    EAdvancedMaterialValidationFailure.UndeclaredValue,
                    checked((uint)valueIndex),
                    value.SemanticHash);
            }

            AdvancedMaterialLayoutMember member = declared[memberIndex];
            if (member.Kind != value.Kind)
            {
                return AdvancedMaterialValidationResult.Invalid(
                    EAdvancedMaterialValidationFailure.ValueKindMismatch,
                    checked((uint)valueIndex),
                    value.SemanticHash);
            }

            if (value.ElementCount > member.ElementCount)
            {
                EAdvancedMaterialValidationFailure failure = value.Kind is
                    EAdvancedMaterialValueKind.Texture or EAdvancedMaterialValueKind.Sampler
                    ? EAdvancedMaterialValidationFailure.TextureRangeOverflow
                    : EAdvancedMaterialValidationFailure.ConstantRangeOverflow;
                return AdvancedMaterialValidationResult.Invalid(
                    failure,
                    checked((uint)valueIndex),
                    value.SemanticHash);
            }
        }

        return AdvancedMaterialValidationResult.Valid;
    }

    public bool TryConsumeMaterialDirtyRange(out AdvancedMaterialDirtyRange range)
    {
        if (!_materialDirty)
        {
            range = default;
            return false;
        }

        range = new(
            _materialDirtyFirst,
            _materialDirtyEnd - _materialDirtyFirst,
            _materialGeneration);
        _materialDirty = false;
        _materialDirtyFirst = 0u;
        _materialDirtyEnd = 0u;
        _materials.ClearDirtyRange();
        return true;
    }

    public bool TryConsumeConstantDirtyRange(out AdvancedMaterialDirtyRange range)
        => TryConsumeArenaDirtyRange(
            ref _constantDirtyFirst,
            ref _constantDirtyEnd,
            _materialGeneration,
            out range);

    public bool TryConsumeTextureBindingDirtyRange(out AdvancedMaterialDirtyRange range)
        => TryConsumeArenaDirtyRange(
            ref _textureDirtyFirst,
            ref _textureDirtyEnd,
            _materialGeneration,
            out range);

    public void GrowAtFrameBoundary(
        uint materialCapacity,
        uint kernelCapacity,
        uint layoutCapacity,
        uint layoutMemberCapacity,
        uint constantWordCapacity = 0u,
        uint textureBindingCapacity = 0u)
    {
        _materials.GrowAtBoundary(materialCapacity);
        _kernels.GrowAtBoundary(kernelCapacity);
        _layouts.GrowAtBoundary(layoutCapacity);
        if (materialCapacity >= (uint)_materialLayoutHandles.Length)
        {
            Array.Resize(
                ref _materialLayoutHandles,
                checked((int)materialCapacity + 1));
        }
        uint requiredConstantCapacity = checked((materialCapacity + 1u) * _maximumConstantWordsPerMaterial);
        uint requiredTextureCapacity = checked((materialCapacity + 1u) * _maximumTextureBindingsPerMaterial);
        constantWordCapacity = Math.Max(constantWordCapacity, requiredConstantCapacity);
        textureBindingCapacity = Math.Max(textureBindingCapacity, requiredTextureCapacity);
        if (layoutMemberCapacity <= (uint)_layoutMembers.Length)
        {
            GrowMaterialPayloadArenas(constantWordCapacity, textureBindingCapacity);
            return;
        }

        Array.Resize(ref _layoutMembers, checked((int)layoutMemberCapacity));
        GrowMaterialPayloadArenas(constantWordCapacity, textureBindingCapacity);
    }

    private bool TryPrepareMaterial(
        AdvancedGpuHandle layoutHandle,
        AdvancedGpuHandle kernelHandle,
        in AdvancedMaterialRecord source,
        ReadOnlySpan<AdvancedMaterialValueDescriptor> values,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings,
        out AdvancedMaterialRecord record)
    {
        if (!_layouts.TryGet(layoutHandle, out AdvancedMaterialLayoutRecord layout) ||
            !_kernels.TryGet(kernelHandle, out AdvancedShadingKernelRecord kernel))
        {
            record = default;
            return false;
        }

        return TryPrepareMaterial(
            in layout,
            in kernel,
            kernelHandle,
            GetLayoutMembers(layout),
            source,
            values,
            constantWords,
            textureBindings,
            out record);
    }

    private bool TryPrepareMaterial(
        in AdvancedMaterialLayoutRecord layout,
        in AdvancedShadingKernelRecord kernel,
        AdvancedGpuHandle kernelHandle,
        ReadOnlySpan<AdvancedMaterialLayoutMember> declaredMembers,
        in AdvancedMaterialRecord source,
        ReadOnlySpan<AdvancedMaterialValueDescriptor> values,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings,
        out AdvancedMaterialRecord record)
    {
        record = source;
        if (kernel.MaterialLayoutHash != layout.LayoutHash)
            return false;

        AdvancedMaterialValidationResult validation = ValidateValues(declaredMembers, values);
        if (!validation.IsValid ||
            constantWords.Length != layout.ConstantWordCount ||
            textureBindings.Length != layout.TextureReferenceCount ||
            (uint)constantWords.Length > _maximumConstantWordsPerMaterial ||
            (uint)textureBindings.Length > _maximumTextureBindingsPerMaterial)
        {
            return false;
        }

        if ((uint)source.CoverageMode > (uint)EAdvancedMaterialCoverageMode.Refractive)
            return false;

        uint coverageBit = 1u << checked((int)source.CoverageMode);
        if ((kernel.SupportedCoverageMask & coverageBit) == 0u)
            return false;
        if ((source.EligibilityFlags & ~kernel.SupportedEligibility) != 0)
            return false;
        if ((source.FeatureFlags & ~kernel.SupportedFeatures) != 0)
            return false;
        record.ShadingKernelId = kernelHandle.Index;
        record.ShadingKernelGeneration = kernelHandle.Generation;
        record.MaterialLayoutHash = layout.LayoutHash;
        record.RequiredAttributeMask |= layout.RequiredAttributeMask | kernel.RequiredAttributeMask;
        return true;
    }

    private EAdvancedGpuMutationDomain ResolveMaterialMutationDomain(
        in AdvancedMaterialRecord previous,
        in AdvancedMaterialRecord replacement,
        ReadOnlySpan<AdvancedMaterialTextureBinding> replacementBindings)
    {
        if (previous.ShadingKernelId != replacement.ShadingKernelId ||
            previous.ShadingKernelGeneration != replacement.ShadingKernelGeneration ||
            previous.MaterialLayoutHash != replacement.MaterialLayoutHash ||
            previous.RenderStateClass != replacement.RenderStateClass ||
            previous.CoverageMode != replacement.CoverageMode ||
            previous.RequiredAttributeMask != replacement.RequiredAttributeMask ||
            previous.FeatureFlags != replacement.FeatureFlags ||
            previous.EligibilityFlags != replacement.EligibilityFlags)
        {
            return EAdvancedGpuMutationDomain.LayoutTopology;
        }

        if (!TryGetTextureBindings(previous, out ReadOnlySpan<AdvancedMaterialTextureBinding> previousBindings) ||
            !TextureBindingsEqual(previousBindings, replacementBindings))
        {
            return EAdvancedGpuMutationDomain.ResourceBinding;
        }

        return EAdvancedGpuMutationDomain.Content;
    }

    private ReadOnlySpan<AdvancedMaterialLayoutMember> GetLayoutMembers(
        in AdvancedMaterialLayoutRecord layout)
        => _layoutMembers.AsSpan(
            checked((int)layout.MemberOffset),
            checked((int)layout.MemberCount));

    private static int FindMember(
        ReadOnlySpan<AdvancedMaterialLayoutMember> members,
        ulong semanticHash)
    {
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i].SemanticHash == semanticHash)
                return i;
        }

        return -1;
    }

    private static bool ValidateLayoutMembers(
        in AdvancedMaterialLayoutRecord layout,
        ReadOnlySpan<AdvancedMaterialLayoutMember> members)
    {
        if (layout.LayoutHash == 0ul)
            return false;

        for (int i = 0; i < members.Length; i++)
        {
            AdvancedMaterialLayoutMember member = members[i];
            if (member.SemanticHash == 0ul ||
                member.ElementCount == 0u ||
                (uint)member.Kind > (uint)EAdvancedMaterialValueKind.Sampler)
                return false;

            for (int prior = 0; prior < i; prior++)
            {
                if (members[prior].SemanticHash == member.SemanticHash)
                    return false;
            }

            ulong end = (ulong)member.ElementOffset + member.ElementCount;
            bool resource = member.Kind is EAdvancedMaterialValueKind.Texture or EAdvancedMaterialValueKind.Sampler;
            uint capacity = resource ? layout.TextureReferenceCount : layout.ConstantWordCount;
            if (end > capacity)
                return false;
        }

        return true;
    }

    private void WriteMaterialPayload(
        uint materialIndex,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings)
    {
        if (!constantWords.IsEmpty)
        {
            uint start = GetConstantOffset(materialIndex);
            constantWords.CopyTo(_constantWords.AsSpan(checked((int)start)));
            _constantWordCount = Math.Max(_constantWordCount, checked(start + _maximumConstantWordsPerMaterial));
            MarkArenaDirty(ref _constantDirtyFirst, ref _constantDirtyEnd, start, checked((uint)constantWords.Length));
        }

        if (!textureBindings.IsEmpty)
        {
            uint start = GetTextureOffset(materialIndex);
            textureBindings.CopyTo(_textureBindings.AsSpan(checked((int)start)));
            _textureBindingCount = Math.Max(_textureBindingCount, checked(start + _maximumTextureBindingsPerMaterial));
            MarkArenaDirty(ref _textureDirtyFirst, ref _textureDirtyEnd, start, checked((uint)textureBindings.Length));
        }
    }

    private static bool LayoutEquals(in AdvancedMaterialLayoutRecord left, in AdvancedMaterialLayoutRecord right)
        => left.LayoutHash == right.LayoutHash &&
           left.ConstantWordCount == right.ConstantWordCount &&
           left.TextureReferenceCount == right.TextureReferenceCount &&
           left.RequiredAttributeMask == right.RequiredAttributeMask &&
           left.Flags == right.Flags &&
           left.Reserved0 == right.Reserved0 &&
           left.Reserved1 == right.Reserved1;

    private static bool KernelEquals(in AdvancedShadingKernelRecord left, in AdvancedShadingKernelRecord right)
        => left.MaterialLayoutHash == right.MaterialLayoutHash &&
           left.RequiredAttributeMask == right.RequiredAttributeMask &&
           left.SupportedCoverageMask == right.SupportedCoverageMask &&
           left.SupportedEligibility == right.SupportedEligibility &&
           left.SupportedFeatures == right.SupportedFeatures &&
           left.ShaderIdentityHash == right.ShaderIdentityHash &&
           left.RenderStateClassMask == right.RenderStateClassMask &&
           left.Flags == right.Flags &&
           left.Reserved0 == right.Reserved0 && left.Reserved1 == right.Reserved1 &&
           left.Reserved2 == right.Reserved2 && left.Reserved3 == right.Reserved3;

    private static AdvancedShadingKernelRecord PrepareKernelRecord(
        in AdvancedMaterialLayoutRecord layout,
        in AdvancedShadingKernelRecord source)
    {
        AdvancedShadingKernelRecord record = source;
        record.StableKernelId = 0u;
        record.Generation = 0u;
        record.MaterialLayoutHash = layout.LayoutHash;
        record.RequiredAttributeMask |= layout.RequiredAttributeMask;
        return record;
    }

    private static bool TextureBindingsEqual(
        ReadOnlySpan<AdvancedMaterialTextureBinding> left,
        ReadOnlySpan<AdvancedMaterialTextureBinding> right)
    {
        if (left.Length != right.Length)
            return false;

        for (int index = 0; index < left.Length; ++index)
        {
            AdvancedMaterialTextureBinding leftBinding = left[index];
            AdvancedMaterialTextureBinding rightBinding = right[index];
            if (!TextureReferenceEquals(leftBinding.Texture, rightBinding.Texture) ||
                !SamplerReferenceEquals(leftBinding.Sampler, rightBinding.Sampler))
                return false;
        }

        return true;
    }

    private static bool TextureReferenceEquals(
        AdvancedTextureReference left,
        AdvancedTextureReference right)
        => left.Handle.Index == right.Handle.Index &&
           left.Handle.Generation == right.Handle.Generation &&
           left.Fallback == right.Fallback &&
           left.Reserved == right.Reserved;

    private static bool SamplerReferenceEquals(
        AdvancedSamplerReference left,
        AdvancedSamplerReference right)
        => left.Handle.Index == right.Handle.Index &&
           left.Handle.Generation == right.Handle.Generation &&
           left.Fallback == right.Fallback &&
           left.Reserved == right.Reserved;

    private uint GetConstantOffset(uint materialIndex)
        => checked(materialIndex * _maximumConstantWordsPerMaterial);

    private uint GetTextureOffset(uint materialIndex)
        => checked(materialIndex * _maximumTextureBindingsPerMaterial);

    private void GrowMaterialPayloadArenas(
        uint constantWordCapacity,
        uint textureBindingCapacity)
    {
        if (constantWordCapacity > (uint)_constantWords.Length)
            Array.Resize(ref _constantWords, checked((int)constantWordCapacity));
        if (textureBindingCapacity > (uint)_textureBindings.Length)
            Array.Resize(ref _textureBindings, checked((int)textureBindingCapacity));
    }

    private static void MarkArenaDirty(
        ref uint dirtyFirst,
        ref uint dirtyEnd,
        uint start,
        uint count)
    {
        if (count == 0u)
            return;

        dirtyFirst = Math.Min(dirtyFirst, start);
        dirtyEnd = Math.Max(dirtyEnd, checked(start + count));
    }

    private static bool TryConsumeArenaDirtyRange(
        ref uint dirtyFirst,
        ref uint dirtyEnd,
        ulong generation,
        out AdvancedMaterialDirtyRange range)
    {
        if (dirtyFirst == uint.MaxValue)
        {
            range = default;
            return false;
        }

        range = new(dirtyFirst, dirtyEnd - dirtyFirst, generation);
        dirtyFirst = uint.MaxValue;
        dirtyEnd = 0u;
        return true;
    }

    private void MarkMaterialDirty(AdvancedGpuHandle handle)
    {
        if (!_materials.TryGetDenseIndex(handle, out uint denseIndex))
            throw new InvalidOperationException("Current material handle has no dense row.");

        MarkMaterialDirty(denseIndex);
    }

    private void MarkMaterialDirty(uint denseIndex)
    {
        uint end = checked(denseIndex + 1u);
        if (!_materialDirty)
        {
            _materialDirty = true;
            _materialDirtyFirst = denseIndex;
            _materialDirtyEnd = end;
            return;
        }

        _materialDirtyFirst = Math.Min(_materialDirtyFirst, denseIndex);
        _materialDirtyEnd = Math.Max(_materialDirtyEnd, end);
    }

    private static void IncrementGeneration(ref ulong generation)
    {
        generation++;
        if (generation == 0ul)
            generation = 1ul;
    }
}

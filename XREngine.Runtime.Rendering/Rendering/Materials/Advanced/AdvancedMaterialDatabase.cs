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
        uint textureBindingCapacity = 0u)
    {
        _materials = new AdvancedGpuRecordTable<AdvancedMaterialRecord>(materialCapacity);
        _kernels = new AdvancedGpuRecordTable<AdvancedShadingKernelRecord>(kernelCapacity);
        _layouts = new AdvancedGpuRecordTable<AdvancedMaterialLayoutRecord>(layoutCapacity);
        _layoutMembers = new AdvancedMaterialLayoutMember[checked((int)layoutMemberCapacity)];
        _constantWords = new uint[checked((int)constantWordCapacity)];
        _textureBindings = new AdvancedMaterialTextureBinding[checked((int)textureBindingCapacity)];
    }

    public AdvancedGpuRecordTable<AdvancedMaterialRecord> Materials => _materials;
    public AdvancedGpuRecordTable<AdvancedShadingKernelRecord> Kernels => _kernels;
    public AdvancedGpuRecordTable<AdvancedMaterialLayoutRecord> Layouts => _layouts;
    public ReadOnlySpan<AdvancedMaterialLayoutMember> LayoutMembers => _layoutMembers.AsSpan(0, checked((int)_layoutMemberCount));
    public ReadOnlySpan<uint> ConstantWords => _constantWords.AsSpan(0, checked((int)_constantWordCount));
    public ReadOnlySpan<AdvancedMaterialTextureBinding> TextureBindings => _textureBindings.AsSpan(0, checked((int)_textureBindingCount));
    public AdvancedMaterialDatabaseGenerations Generations => new(_materialGeneration, _kernelGeneration, _layoutGeneration);

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
    /// against its declared layout. Packed arenas are append-only during a frame.
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

        uint constantOffset = _constantWordCount;
        uint textureOffset = _textureBindingCount;
        record.ConstantWordOffset = constantOffset;
        record.ConstantWordCount = checked((uint)constantWords.Length);
        record.TextureReferenceOffset = textureOffset;
        record.TextureReferenceCount = checked((uint)textureBindings.Length);
        if (!_materials.TryAdd(record, out handle))
            return false;

        record.StableRowId = handle.Index;
        record.Generation = handle.Generation;
        if (!_materials.TryReplace(handle, record))
            throw new InvalidOperationException("Newly inserted material row could not be initialized.");

        AppendMaterialPayload(constantWords, textureBindings);
        MarkMaterialDirty(handle);
        IncrementGeneration(ref _materialGeneration);
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
    /// Replaces a material header while appending a new immutable packed payload.
    /// Arena reclamation is intentionally deferred to a structural boundary.
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
        if (!_materials.IsCurrent(materialHandle))
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

        record.ConstantWordOffset = _constantWordCount;
        record.ConstantWordCount = checked((uint)constantWords.Length);
        record.TextureReferenceOffset = _textureBindingCount;
        record.TextureReferenceCount = checked((uint)textureBindings.Length);
        record.StableRowId = materialHandle.Index;
        record.Generation = materialHandle.Generation;
        if (!_materials.TryReplace(materialHandle, record))
            return false;

        AppendMaterialPayload(constantWords, textureBindings);
        MarkMaterialDirty(materialHandle);
        IncrementGeneration(ref _materialGeneration);
        return true;
    }

    public bool RemoveMaterial(AdvancedGpuHandle materialHandle)
    {
        if (!_materials.TryGetDenseIndex(materialHandle, out uint denseIndex))
            return false;
        if (!_materials.Remove(materialHandle))
            return false;

        MarkMaterialDirty(denseIndex);
        IncrementGeneration(ref _materialGeneration);
        return true;
    }

    public bool RemoveKernel(AdvancedGpuHandle kernelHandle)
    {
        if (!_kernels.Remove(kernelHandle))
            return false;

        IncrementGeneration(ref _kernelGeneration);
        return true;
    }

    public bool RemoveLayout(AdvancedGpuHandle layoutHandle)
    {
        if (!_layouts.Remove(layoutHandle))
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

        ReadOnlySpan<AdvancedMaterialLayoutMember> declared = GetLayoutMembers(layout);
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
        record = source;
        if (!_layouts.TryGet(layoutHandle, out AdvancedMaterialLayoutRecord layout) ||
            !_kernels.TryGet(kernelHandle, out AdvancedShadingKernelRecord kernel) ||
            kernel.MaterialLayoutHash != layout.LayoutHash)
        {
            return false;
        }

        AdvancedMaterialValidationResult validation = ValidateValues(layoutHandle, values);
        if (!validation.IsValid ||
            constantWords.Length != layout.ConstantWordCount ||
            textureBindings.Length != layout.TextureReferenceCount ||
            (uint)constantWords.Length > (uint)_constantWords.Length - _constantWordCount ||
            (uint)textureBindings.Length > (uint)_textureBindings.Length - _textureBindingCount)
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

    private void AppendMaterialPayload(
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings)
    {
        if (!constantWords.IsEmpty)
        {
            uint start = _constantWordCount;
            constantWords.CopyTo(_constantWords.AsSpan(checked((int)start)));
            _constantWordCount += checked((uint)constantWords.Length);
            MarkArenaDirty(ref _constantDirtyFirst, ref _constantDirtyEnd, start, checked((uint)constantWords.Length));
        }

        if (!textureBindings.IsEmpty)
        {
            uint start = _textureBindingCount;
            textureBindings.CopyTo(_textureBindings.AsSpan(checked((int)start)));
            _textureBindingCount += checked((uint)textureBindings.Length);
            MarkArenaDirty(ref _textureDirtyFirst, ref _textureDirtyEnd, start, checked((uint)textureBindings.Length));
        }
    }

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

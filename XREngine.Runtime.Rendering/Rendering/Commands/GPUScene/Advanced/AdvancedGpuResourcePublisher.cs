using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Boundary-owned logical texture and sampler publisher. One acquired binding
/// owns one reference to each valid logical resource; physical descriptors and
/// native image realization remain backend-owned.
/// </summary>
public sealed class AdvancedGpuResourcePublisher
{
    private readonly AdvancedGlobalResourceDatabase _database;
    private TextureEntry[] _textures;
    private SamplerEntry[] _samplers;
    private uint[] _textureSourceSlots;
    private uint[] _textureHandleSlots;
    private uint[] _samplerRecordSlots;
    private uint[] _samplerHandleSlots;
    private uint[] _pendingTextureSlots;
    private uint[] _pendingSamplerSlots;
    private int[] _preflightTextureSourceIndices;
    private uint[] _preflightTextureStamps;
    private uint[] _textureAcquireCounts;
    private uint[] _samplerAcquireCounts;
    private uint[] _textureReleaseCounts;
    private uint[] _samplerReleaseCounts;
    private int _preflightTextureAdds;
    private int _preflightTextureReplacements;
    private int _preflightSamplerAdds;
    private uint _preflightGeneration;
    private int _textureCount;
    private int _samplerCount;

    public AdvancedGpuResourcePublisher(
        AdvancedGlobalResourceDatabase database,
        uint textureCapacity = 0u,
        uint samplerCapacity = 0u)
    {
        ArgumentNullException.ThrowIfNull(database);
        textureCapacity = textureCapacity == 0u ? database.Textures.Capacity : textureCapacity;
        samplerCapacity = samplerCapacity == 0u ? database.Samplers.Capacity : samplerCapacity;
        if (textureCapacity == 0u || samplerCapacity == 0u ||
            textureCapacity > database.Textures.Capacity ||
            samplerCapacity > database.Samplers.Capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(textureCapacity),
                "Publisher capacities must be nonzero and fit the canonical resource tables.");
        }

        _database = database;
        _textures = new TextureEntry[checked((int)textureCapacity)];
        _samplers = new SamplerEntry[checked((int)samplerCapacity)];
        _textureSourceSlots = new uint[GetSlotCapacity(textureCapacity)];
        _textureHandleSlots = new uint[GetSlotCapacity(textureCapacity)];
        _samplerRecordSlots = new uint[GetSlotCapacity(samplerCapacity)];
        _samplerHandleSlots = new uint[GetSlotCapacity(samplerCapacity)];
        _pendingTextureSlots = new uint[GetSlotCapacity(textureCapacity)];
        _pendingSamplerSlots = new uint[GetSlotCapacity(samplerCapacity)];
        _preflightTextureSourceIndices = new int[_textures.Length];
        _preflightTextureStamps = new uint[_textures.Length];
        _textureAcquireCounts = new uint[_textures.Length];
        _samplerAcquireCounts = new uint[_samplers.Length];
        _textureReleaseCounts = new uint[_textures.Length];
        _samplerReleaseCounts = new uint[_samplers.Length];
    }

    public int TextureCount => _textureCount;

    public int SamplerCount => _samplerCount;

    /// <summary>
    /// Grows publisher-owned lookup and scratch storage after the shared
    /// database has grown at the same publication boundary.
    /// </summary>
    public void GrowRegistryAtFrameBoundary(uint textureCapacity, uint samplerCapacity)
    {
        if (textureCapacity > _database.Textures.Capacity ||
            samplerCapacity > _database.Samplers.Capacity)
        {
            throw new InvalidOperationException(
                "Grow the shared resource database before its publisher registry.");
        }

        if (textureCapacity > (uint)_textures.Length)
        {
            Array.Resize(ref _textures, checked((int)textureCapacity));
            Array.Resize(ref _preflightTextureSourceIndices, _textures.Length);
            Array.Resize(ref _preflightTextureStamps, _textures.Length);
            Array.Resize(ref _textureAcquireCounts, _textures.Length);
            Array.Resize(ref _textureReleaseCounts, _textures.Length);
            _textureSourceSlots = new uint[GetSlotCapacity(textureCapacity)];
            _textureHandleSlots = new uint[GetSlotCapacity(textureCapacity)];
            _pendingTextureSlots = new uint[GetSlotCapacity(textureCapacity)];
            for (int index = 0; index < _textureCount; ++index)
            {
                InsertTextureSourceSlot(index);
                InsertTextureHandleSlot(index);
            }
        }

        if (samplerCapacity <= (uint)_samplers.Length)
            return;

        Array.Resize(ref _samplers, checked((int)samplerCapacity));
        Array.Resize(ref _samplerAcquireCounts, _samplers.Length);
        Array.Resize(ref _samplerReleaseCounts, _samplers.Length);
        _samplerRecordSlots = new uint[GetSlotCapacity(samplerCapacity)];
        _samplerHandleSlots = new uint[GetSlotCapacity(samplerCapacity)];
        _pendingSamplerSlots = new uint[GetSlotCapacity(samplerCapacity)];
        for (int index = 0; index < _samplerCount; ++index)
        {
            InsertSamplerRecordSlot(index);
            InsertSamplerHandleSlot(index);
        }
    }

    /// <summary>
    /// Preflights all distinct additions and texture metadata updates in one
    /// allocation-free batch. A false result leaves publisher and database state
    /// unchanged.
    /// </summary>
    public bool TryPreflightAcquireBatch(
        ReadOnlySpan<AdvancedGpuResourceBindingSource> sources,
        out string reason)
    {
        BeginPreflight();
        int textureAdds = 0;
        int textureReplacements = 0;
        int samplerAdds = 0;

        for (int sourceIndex = 0; sourceIndex < sources.Length; ++sourceIndex)
        {
            ref readonly AdvancedGpuResourceBindingSource source = ref sources[sourceIndex];
            if (!ValidateSource(in source, out reason))
                return false;
            if (source.Texture is null)
                continue;

            int textureIndex = FindTextureBySource(source.Texture);
            if (textureIndex >= 0)
            {
                if (_textureAcquireCounts[textureIndex] == uint.MaxValue ||
                    _textureAcquireCounts[textureIndex] > uint.MaxValue - _textures[textureIndex].ReferenceCount)
                {
                    reason = "The acquisition batch would overflow a logical texture reference count.";
                    return false;
                }
                ++_textureAcquireCounts[textureIndex];
                if (_preflightTextureStamps[textureIndex] == _preflightGeneration)
                {
                    int firstSourceIndex = _preflightTextureSourceIndices[textureIndex];
                    if (!TextureEquals(
                            sources[firstSourceIndex].TextureRecord,
                            source.TextureRecord))
                    {
                        reason = "One texture identity produced conflicting canonical metadata in the same batch.";
                        return false;
                    }
                }
                else
                {
                    _preflightTextureStamps[textureIndex] = _preflightGeneration;
                    _preflightTextureSourceIndices[textureIndex] = sourceIndex;
                    if (!TextureEquals(_textures[textureIndex].Record, source.TextureRecord))
                        ++textureReplacements;
                }
            }
            else if (!TryInsertPendingTexture(sources, sourceIndex, ref textureAdds, out reason))
            {
                return false;
            }

            int samplerIndex = FindSamplerByRecord(source.SamplerRecord);
            if (samplerIndex >= 0)
            {
                if (_samplerAcquireCounts[samplerIndex] == uint.MaxValue ||
                    _samplerAcquireCounts[samplerIndex] > uint.MaxValue - _samplers[samplerIndex].ReferenceCount)
                {
                    reason = "The acquisition batch would overflow a logical sampler reference count.";
                    return false;
                }
                ++_samplerAcquireCounts[samplerIndex];
            }
            else if (!TryInsertPendingSampler(sources, sourceIndex, ref samplerAdds, out reason))
            {
                return false;
            }
        }

        if (textureAdds > _textures.Length - _textureCount)
        {
            reason = "The logical texture publisher registry is full; grow it at a publication boundary.";
            return false;
        }
        if (samplerAdds > _samplers.Length - _samplerCount)
        {
            reason = "The logical sampler publisher registry is full; grow it at a publication boundary.";
            return false;
        }
        if (!_database.Textures.CanApply(
                textureAdds,
                checked(textureAdds + textureReplacements),
                0))
        {
            reason = "The canonical texture table cannot accept the complete acquisition batch.";
            return false;
        }
        if (!_database.Samplers.CanApply(samplerAdds, samplerAdds, 0))
        {
            reason = "The canonical sampler table cannot accept the complete acquisition batch.";
            return false;
        }

        _preflightTextureAdds = textureAdds;
        _preflightTextureReplacements = textureReplacements;
        _preflightSamplerAdds = samplerAdds;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Acquires a complete batch after fail-before-write preflight. The caller
    /// supplies destination storage so acquisition performs no heap allocation.
    /// </summary>
    public bool TryAcquireBatch(
        ReadOnlySpan<AdvancedGpuResourceBindingSource> sources,
        Span<AdvancedMaterialTextureBinding> destination,
        out string reason)
    {
        if (destination.Length < sources.Length)
        {
            reason = "The material binding destination is smaller than the source batch.";
            return false;
        }
        if (!TryPreflightAcquireBatch(sources, out reason))
            return false;

        AcquireBatchAfterPreflight(sources, destination);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Atomically preflights peak old-plus-new ownership, acquires the complete
    /// replacement batch, and only then releases the previous bindings.
    /// </summary>
    public bool TryReplaceBatch(
        ReadOnlySpan<AdvancedGpuResourceBindingSource> sources,
        ReadOnlySpan<AdvancedMaterialTextureBinding> previousBindings,
        Span<AdvancedMaterialTextureBinding> destination,
        out string reason)
    {
        if (destination.Length < sources.Length)
        {
            reason = "The material binding destination is smaller than the replacement source batch.";
            return false;
        }
        if (destination.Overlaps(previousBindings))
        {
            reason = "Replacement output must not overwrite previous bindings before their release.";
            return false;
        }
        if (!TryPreflightTransition(sources, previousBindings, out reason))
            return false;

        ApplyPreflightedAcquisitions(sources, destination);
        ApplyPreflightedReleases();
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Preflights a whole-scene acquire-before-release transition while leaving
    /// application order to the scene owner: acquire resources, publish material
    /// and draw replacements, then retire old resources.
    /// </summary>
    internal bool TryPreflightTransition(
        ReadOnlySpan<AdvancedGpuResourceBindingSource> sources,
        ReadOnlySpan<AdvancedMaterialTextureBinding> previousBindings,
        out string reason)
    {
        if (!TryPreflightAcquireBatch(sources, out reason) ||
            !TryCollectReleaseBatch(
                previousBindings,
                includePreflightAcquisitions: true,
                out int textureTombstones,
                out int samplerTombstones,
                out reason))
        {
            return false;
        }
        if (!_database.Textures.CanApply(
                _preflightTextureAdds,
                checked(_preflightTextureAdds + _preflightTextureReplacements),
                textureTombstones) ||
            !_database.Samplers.CanApply(
                _preflightSamplerAdds,
                _preflightSamplerAdds,
                samplerTombstones))
        {
            reason = "The canonical resource tables cannot accept the complete acquire-before-release transaction.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal void ApplyPreflightedAcquisitions(
        ReadOnlySpan<AdvancedGpuResourceBindingSource> sources,
        Span<AdvancedMaterialTextureBinding> destination)
    {
        if (destination.Length < sources.Length)
            throw new ArgumentException("The material binding destination is smaller than the preflighted source batch.", nameof(destination));
        AcquireBatchAfterPreflight(sources, destination);
    }

    internal void ApplyPreflightedReleases()
        => ReleaseBatchAfterPreflight();

    private void AcquireBatchAfterPreflight(
        ReadOnlySpan<AdvancedGpuResourceBindingSource> sources,
        Span<AdvancedMaterialTextureBinding> destination)
    {

        for (int sourceIndex = 0; sourceIndex < sources.Length; ++sourceIndex)
        {
            ref readonly AdvancedGpuResourceBindingSource source = ref sources[sourceIndex];
            if (source.Texture is null)
            {
                destination[sourceIndex] = new(
                    AdvancedTextureReference.Invalid(source.Fallback),
                    AdvancedSamplerReference.Invalid);
                continue;
            }

            int samplerIndex = FindSamplerByRecord(source.SamplerRecord);
            if (samplerIndex < 0)
                samplerIndex = AddSamplerAfterPreflight(source.SamplerRecord);

            int textureIndex = FindTextureBySource(source.Texture);
            if (textureIndex < 0)
                textureIndex = AddTextureAfterPreflight(source.Texture, source.TextureRecord);
            else if (!TextureEquals(_textures[textureIndex].Record, source.TextureRecord))
                ReplaceTextureAfterPreflight(textureIndex, source.TextureRecord);

            ref TextureEntry texture = ref _textures[textureIndex];
            ref SamplerEntry sampler = ref _samplers[samplerIndex];
            checked
            {
                ++texture.ReferenceCount;
                ++sampler.ReferenceCount;
            }
            destination[sourceIndex] = new(
                new AdvancedTextureReference(texture.Handle, source.Fallback, 0u),
                new AdvancedSamplerReference(sampler.Handle, EAdvancedResourceFallback.Zero, 0u));
        }

    }

    public bool TryAcquire(
        in AdvancedGpuResourceBindingSource source,
        out AdvancedMaterialTextureBinding binding,
        out string reason)
    {
        ReadOnlySpan<AdvancedGpuResourceBindingSource> sources =
            MemoryMarshal.CreateReadOnlySpan(in source, 1);
        Span<AdvancedMaterialTextureBinding> bindings = stackalloc AdvancedMaterialTextureBinding[1];
        bool acquired = TryAcquireBatch(sources, bindings, out reason);
        binding = acquired ? bindings[0] : default;
        return acquired;
    }

    /// <summary>
    /// Releases a complete binding batch. Final references are tombstoned only
    /// after every handle and table journal has been preflighted.
    /// </summary>
    public bool TryReleaseBatch(
        ReadOnlySpan<AdvancedMaterialTextureBinding> bindings,
        out string reason)
    {
        if (!TryCollectReleaseBatch(
                bindings,
                includePreflightAcquisitions: false,
                out int textureTombstones,
                out int samplerTombstones,
                out reason))
        {
            return false;
        }
        if (!_database.Textures.CanApply(0, 0, textureTombstones) ||
            !_database.Samplers.CanApply(0, 0, samplerTombstones))
        {
            reason = "The canonical resource tables cannot retire the complete release batch.";
            return false;
        }

        ReleaseBatchAfterPreflight();
        reason = string.Empty;
        return true;
    }

    private bool TryCollectReleaseBatch(
        ReadOnlySpan<AdvancedMaterialTextureBinding> bindings,
        bool includePreflightAcquisitions,
        out int textureTombstones,
        out int samplerTombstones,
        out string reason)
    {
        textureTombstones = 0;
        samplerTombstones = 0;
        Array.Clear(_textureReleaseCounts);
        Array.Clear(_samplerReleaseCounts);
        for (int bindingIndex = 0; bindingIndex < bindings.Length; ++bindingIndex)
        {
            ref readonly AdvancedMaterialTextureBinding binding = ref bindings[bindingIndex];
            bool hasTexture = binding.Texture.Handle.IsValid;
            bool hasSampler = binding.Sampler.Handle.IsValid;
            if (!hasTexture && !hasSampler)
                continue;
            if (hasTexture != hasSampler)
            {
                reason = "A canonical material binding must retain both texture and sampler or neither.";
                return false;
            }

            int textureIndex = FindTextureByHandle(binding.Texture.Handle);
            int samplerIndex = FindSamplerByHandle(binding.Sampler.Handle);
            if (textureIndex < 0 || samplerIndex < 0)
            {
                reason = "The release batch contains a stale or foreign logical resource handle.";
                return false;
            }
            checked
            {
                ++_textureReleaseCounts[textureIndex];
                ++_samplerReleaseCounts[samplerIndex];
            }
        }

        for (int index = 0; index < _textureCount; ++index)
        {
            uint releases = _textureReleaseCounts[index];
            ulong available = _textures[index].ReferenceCount +
                (includePreflightAcquisitions ? _textureAcquireCounts[index] : 0u);
            if (releases > available)
            {
                reason = "The release batch exceeds a logical texture's reference count.";
                return false;
            }
            if (releases != 0u && releases == available)
                ++textureTombstones;
        }

        for (int index = 0; index < _samplerCount; ++index)
        {
            uint releases = _samplerReleaseCounts[index];
            ulong available = _samplers[index].ReferenceCount +
                (includePreflightAcquisitions ? _samplerAcquireCounts[index] : 0u);
            if (releases > available)
            {
                reason = "The release batch exceeds a logical sampler's reference count.";
                return false;
            }
            if (releases != 0u && releases == available)
                ++samplerTombstones;
        }

        reason = string.Empty;
        return true;
    }

    private void ReleaseBatchAfterPreflight()
    {
        for (int index = 0; index < _textureCount; ++index)
        {
            uint releases = _textureReleaseCounts[index];
            if (releases == 0u)
                continue;
            if (releases == _textures[index].ReferenceCount &&
                !_database.RemoveTexture(_textures[index].Handle))
            {
                throw new InvalidOperationException("Preflighted logical texture retirement failed.");
            }
            _textures[index].ReferenceCount -= releases;
        }
        for (int index = 0; index < _samplerCount; ++index)
        {
            uint releases = _samplerReleaseCounts[index];
            if (releases == 0u)
                continue;
            if (releases == _samplers[index].ReferenceCount &&
                !_database.RemoveSampler(_samplers[index].Handle))
            {
                throw new InvalidOperationException("Preflighted logical sampler retirement failed.");
            }
            _samplers[index].ReferenceCount -= releases;
        }

        for (int index = _textureCount - 1; index >= 0; --index)
            if (_textures[index].ReferenceCount == 0u)
                RemoveTextureEntry(index);
        for (int index = _samplerCount - 1; index >= 0; --index)
            if (_samplers[index].ReferenceCount == 0u)
                RemoveSamplerEntry(index);

    }

    public bool TryRelease(in AdvancedMaterialTextureBinding binding, out string reason)
        => TryReleaseBatch(MemoryMarshal.CreateReadOnlySpan(in binding, 1), out reason);

    public bool TryGetTextureHandle(XRTexture texture, out AdvancedGpuHandle handle)
    {
        ArgumentNullException.ThrowIfNull(texture);
        int index = FindTextureBySource(texture);
        handle = index < 0 ? AdvancedGpuHandle.Invalid : _textures[index].Handle;
        return handle.IsValid;
    }

    /// <summary>
    /// Captures the exact live source closure into a prepared publication-ring
    /// entry. The snapshot owns the strong references after this method returns.
    /// </summary>
    internal bool TryCapturePublication(
        ulong sequence,
        AdvancedGpuResourcePublicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.TryBeginSourceCapture(
                sequence,
                _textureCount,
                _samplerCount))
        {
            return false;
        }

        for (int index = 0; index < _textureCount; ++index)
        {
            ref readonly TextureEntry entry = ref _textures[index];
            if (!snapshot.Textures.TryGet(
                    entry.Handle,
                    out AdvancedTextureRecord retainedRecord) ||
                !TextureEquals(retainedRecord, entry.Record) ||
                !snapshot.TryAddTextureSource(entry.Handle, entry.Source))
            {
                snapshot.AbortSourceCapture();
                return false;
            }
        }

        for (int index = 0; index < _samplerCount; ++index)
        {
            ref readonly SamplerEntry entry = ref _samplers[index];
            if (!snapshot.Samplers.TryGet(
                    entry.Handle,
                    out AdvancedSamplerRecord retainedRecord) ||
                !SamplerEquals(retainedRecord, entry.Record))
            {
                snapshot.AbortSourceCapture();
                return false;
            }
        }

        return snapshot.TryCompleteSourceCapture(sequence);
    }

    /// <summary>
    /// Checks whether a retained logical binding exactly represents a freshly
    /// encoded source, including texture description and immutable sampler key.
    /// </summary>
    internal bool BindingMatches(
        in AdvancedMaterialTextureBinding binding,
        in AdvancedGpuResourceBindingSource source)
    {
        if (source.Texture is null)
        {
            return !binding.Texture.Handle.IsValid &&
                !binding.Sampler.Handle.IsValid &&
                binding.Texture.Fallback == source.Fallback;
        }
        if (!binding.Texture.Handle.IsValid || !binding.Sampler.Handle.IsValid ||
            binding.Texture.Fallback != source.Fallback)
        {
            return false;
        }

        int textureIndex = FindTextureByHandle(binding.Texture.Handle);
        int samplerIndex = FindSamplerByHandle(binding.Sampler.Handle);
        return textureIndex >= 0 && samplerIndex >= 0 &&
            ReferenceEquals(_textures[textureIndex].Source, source.Texture) &&
            TextureEquals(_textures[textureIndex].Record, source.TextureRecord) &&
            SamplerEquals(_samplers[samplerIndex].Record, source.SamplerRecord);
    }

    private void BeginPreflight()
    {
        Array.Clear(_pendingTextureSlots);
        Array.Clear(_pendingSamplerSlots);
        Array.Clear(_textureAcquireCounts);
        Array.Clear(_samplerAcquireCounts);
        _preflightTextureAdds = 0;
        _preflightTextureReplacements = 0;
        _preflightSamplerAdds = 0;
        unchecked { ++_preflightGeneration; }
        if (_preflightGeneration != 0u)
            return;
        Array.Clear(_preflightTextureStamps);
        _preflightGeneration = 1u;
    }

    private static bool ValidateSource(
        in AdvancedGpuResourceBindingSource source,
        out string reason)
    {
        if ((uint)source.Fallback > (uint)EAdvancedResourceFallback.Identity)
        {
            reason = "The logical resource fallback is invalid.";
            return false;
        }
        if (source.Texture is null)
        {
            reason = string.Empty;
            return true;
        }
        if (source.TextureRecord.StableTextureId != 0u ||
            source.TextureRecord.Generation != 0u ||
            source.TextureRecord.EncodedReferenceIndex != 0u ||
            source.TextureRecord.DefaultSampler.IsValid ||
            (source.TextureRecord.Flags &
                ~(EAdvancedTextureRecordFlags.Srgb |
                  EAdvancedTextureRecordFlags.Storage |
                  EAdvancedTextureRecordFlags.Depth)) != 0)
        {
            reason = "Logical texture input must not contain canonical identity, backend encoding, or realized-residency state.";
            return false;
        }
        if (source.TextureRecord.Dimension != EAdvancedTextureDimension.Texture2D ||
            source.TextureRecord.Width == 0u ||
            source.TextureRecord.Height == 0u ||
            source.TextureRecord.DepthOrLayers != 1u ||
            source.TextureRecord.MipCount == 0u ||
            source.TextureRecord.FormatClass == (uint)EAdvancedTextureFormatClass.Unknown ||
            source.TextureRecord.FormatClass > (uint)EAdvancedTextureFormatClass.Stencil8)
        {
            reason = "Logical material textures must describe one supported, nonempty 2D resource.";
            return false;
        }
        bool depthFormat = IsDepthFormat(source.TextureRecord.FormatClass);
        if (((source.TextureRecord.Flags & EAdvancedTextureRecordFlags.Depth) != 0) != depthFormat)
        {
            reason = "Logical texture depth classification must match its stable format class.";
            return false;
        }
        if (!IsFinite(source.TextureRecord.UvScaleBias) ||
            !IsFinite(source.SamplerRecord.LodBiasMinMaxAnisotropy) ||
            !IsFinite(source.SamplerRecord.BorderColor))
        {
            reason = "Logical texture and sampler floating-point metadata must be finite.";
            return false;
        }
        if (source.SamplerRecord.StableSamplerId != 0u ||
            source.SamplerRecord.Generation != 0u)
        {
            reason = "Logical sampler input must not contain canonical identity.";
            return false;
        }
        const EAdvancedSamplerRecordFlags supportedSamplerFlags =
            EAdvancedSamplerRecordFlags.UsesMipmaps |
            EAdvancedSamplerRecordFlags.LinearMipmapInterpolation |
            EAdvancedSamplerRecordFlags.NearestMinification |
            EAdvancedSamplerRecordFlags.NearestMagnification |
            EAdvancedSamplerRecordFlags.ComparisonEnabled |
            EAdvancedSamplerRecordFlags.AnisotropyEnabled;
        if ((uint)source.SamplerRecord.Filter > (uint)EAdvancedSamplerFilter.Linear ||
            (source.SamplerRecord.Flags & ~supportedSamplerFlags) != 0 ||
            (uint)source.SamplerRecord.AddressU > (uint)EAdvancedSamplerAddressMode.ClampToBorder ||
            (uint)source.SamplerRecord.AddressV > (uint)EAdvancedSamplerAddressMode.ClampToBorder ||
            (uint)source.SamplerRecord.AddressW > (uint)EAdvancedSamplerAddressMode.ClampToBorder ||
            (uint)source.SamplerRecord.CompareOperation > (uint)EAdvancedCompareOperation.Always)
        {
            reason = "Logical sampler metadata contains an unsupported enum or flag value.";
            return false;
        }
        bool comparisonEnabled =
            (source.SamplerRecord.Flags & EAdvancedSamplerRecordFlags.ComparisonEnabled) != 0;
        bool anisotropyEnabled =
            (source.SamplerRecord.Flags & EAdvancedSamplerRecordFlags.AnisotropyEnabled) != 0;
        bool usesMipmaps =
            (source.SamplerRecord.Flags & EAdvancedSamplerRecordFlags.UsesMipmaps) != 0;
        bool linearMipmapInterpolation =
            (source.SamplerRecord.Flags & EAdvancedSamplerRecordFlags.LinearMipmapInterpolation) != 0;
        bool nearestMinification =
            (source.SamplerRecord.Flags & EAdvancedSamplerRecordFlags.NearestMinification) != 0;
        bool nearestMagnification =
            (source.SamplerRecord.Flags & EAdvancedSamplerRecordFlags.NearestMagnification) != 0;
        EAdvancedSamplerFilter expectedFilter = nearestMinification && nearestMagnification
            ? EAdvancedSamplerFilter.Nearest
            : EAdvancedSamplerFilter.Linear;
        Vector4 lod = source.SamplerRecord.LodBiasMinMaxAnisotropy;
        if (source.SamplerRecord.Filter != expectedFilter ||
            (linearMipmapInterpolation && !usesMipmaps) ||
            lod.Y > lod.Z || lod.W < 1.0f ||
            (!comparisonEnabled && source.SamplerRecord.CompareOperation != EAdvancedCompareOperation.Never) ||
            (comparisonEnabled && !depthFormat) ||
            (anisotropyEnabled != (lod.W > 1.0f)))
        {
            reason = "Logical sampler metadata is not canonically normalized for its enabled features.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsDepthFormat(uint formatClass)
        => formatClass is
            (uint)EAdvancedTextureFormatClass.Depth16 or
            (uint)EAdvancedTextureFormatClass.Depth24 or
            (uint)EAdvancedTextureFormatClass.Depth32Float or
            (uint)EAdvancedTextureFormatClass.Depth24Stencil8 or
            (uint)EAdvancedTextureFormatClass.Depth32FloatStencil8;

    private bool TryInsertPendingTexture(
        ReadOnlySpan<AdvancedGpuResourceBindingSource> sources,
        int sourceIndex,
        ref int additionCount,
        out string reason)
    {
        XRTexture texture = sources[sourceIndex].Texture!;
        int mask = _pendingTextureSlots.Length - 1;
        int slot = (int)TextureSourceHash(texture) & mask;
        for (int probe = 0; probe < _pendingTextureSlots.Length; ++probe, slot = (slot + 1) & mask)
        {
            uint stored = _pendingTextureSlots[slot];
            if (stored == 0u)
            {
                _pendingTextureSlots[slot] = checked((uint)sourceIndex + 1u);
                ++additionCount;
                reason = string.Empty;
                return true;
            }

            int existingSourceIndex = checked((int)stored - 1);
            ref readonly AdvancedGpuResourceBindingSource existing = ref sources[existingSourceIndex];
            if (!ReferenceEquals(existing.Texture, texture))
                continue;
            if (!TextureEquals(existing.TextureRecord, sources[sourceIndex].TextureRecord))
            {
                reason = "One new texture identity produced conflicting canonical metadata in the same batch.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        reason = "The pending logical texture preflight table is full.";
        return false;
    }

    private bool TryInsertPendingSampler(
        ReadOnlySpan<AdvancedGpuResourceBindingSource> sources,
        int sourceIndex,
        ref int additionCount,
        out string reason)
    {
        AdvancedSamplerRecord sampler = sources[sourceIndex].SamplerRecord;
        int mask = _pendingSamplerSlots.Length - 1;
        int slot = (int)SamplerHash(sampler) & mask;
        for (int probe = 0; probe < _pendingSamplerSlots.Length; ++probe, slot = (slot + 1) & mask)
        {
            uint stored = _pendingSamplerSlots[slot];
            if (stored == 0u)
            {
                _pendingSamplerSlots[slot] = checked((uint)sourceIndex + 1u);
                ++additionCount;
                reason = string.Empty;
                return true;
            }
            int existingSourceIndex = checked((int)stored - 1);
            if (!SamplerEquals(sources[existingSourceIndex].SamplerRecord, sampler))
                continue;

            reason = string.Empty;
            return true;
        }

        reason = "The pending logical sampler preflight table is full.";
        return false;
    }

    private int AddTextureAfterPreflight(XRTexture source, AdvancedTextureRecord record)
    {
        if (!_database.TryAddTexture(record, out AdvancedGpuHandle handle))
            throw new InvalidOperationException("Preflighted logical texture insertion failed.");
        int index = _textureCount++;
        _textures[index] = new(source, record, handle, 0u);
        InsertTextureSourceSlot(index);
        InsertTextureHandleSlot(index);
        return index;
    }

    private int AddSamplerAfterPreflight(AdvancedSamplerRecord record)
    {
        if (!_database.TryAddSampler(record, out AdvancedGpuHandle handle))
            throw new InvalidOperationException("Preflighted logical sampler insertion failed.");
        int index = _samplerCount++;
        _samplers[index] = new(record, handle, 0u);
        InsertSamplerRecordSlot(index);
        InsertSamplerHandleSlot(index);
        return index;
    }

    private void ReplaceTextureAfterPreflight(int index, AdvancedTextureRecord record)
    {
        if (!_database.TryReplaceTexture(_textures[index].Handle, record))
            throw new InvalidOperationException("Preflighted logical texture replacement failed.");
        _textures[index].Record = record;
    }

    private int FindTextureBySource(XRTexture source)
    {
        int slot = FindTextureSourceSlot(source);
        return slot < 0 ? -1 : checked((int)_textureSourceSlots[slot] - 1);
    }

    private int FindTextureSourceSlot(XRTexture source)
    {
        int mask = _textureSourceSlots.Length - 1;
        int slot = (int)TextureSourceHash(source) & mask;
        for (int probe = 0; probe < _textureSourceSlots.Length; ++probe, slot = (slot + 1) & mask)
        {
            uint stored = _textureSourceSlots[slot];
            if (stored == 0u)
                return -1;
            if (ReferenceEquals(_textures[checked((int)stored - 1)].Source, source))
                return slot;
        }
        return -1;
    }

    private int FindTextureByHandle(AdvancedGpuHandle handle)
    {
        int slot = FindHandleSlot(_textureHandleSlots, handle, true);
        return slot < 0 ? -1 : checked((int)_textureHandleSlots[slot] - 1);
    }

    private int FindSamplerByRecord(AdvancedSamplerRecord record)
    {
        int slot = FindSamplerRecordSlot(record);
        return slot < 0 ? -1 : checked((int)_samplerRecordSlots[slot] - 1);
    }

    private int FindSamplerRecordSlot(AdvancedSamplerRecord record)
    {
        int mask = _samplerRecordSlots.Length - 1;
        int slot = (int)SamplerHash(record) & mask;
        for (int probe = 0; probe < _samplerRecordSlots.Length; ++probe, slot = (slot + 1) & mask)
        {
            uint stored = _samplerRecordSlots[slot];
            if (stored == 0u)
                return -1;
            if (SamplerEquals(_samplers[checked((int)stored - 1)].Record, record))
                return slot;
        }
        return -1;
    }

    private int FindSamplerByHandle(AdvancedGpuHandle handle)
    {
        int slot = FindHandleSlot(_samplerHandleSlots, handle, false);
        return slot < 0 ? -1 : checked((int)_samplerHandleSlots[slot] - 1);
    }

    private int FindHandleSlot(uint[] slots, AdvancedGpuHandle handle, bool texture)
    {
        int mask = slots.Length - 1;
        int slot = (int)HandleHash(handle) & mask;
        for (int probe = 0; probe < slots.Length; ++probe, slot = (slot + 1) & mask)
        {
            uint stored = slots[slot];
            if (stored == 0u)
                return -1;
            int index = checked((int)stored - 1);
            AdvancedGpuHandle candidate = texture ? _textures[index].Handle : _samplers[index].Handle;
            if (candidate == handle)
                return slot;
        }
        return -1;
    }

    private void InsertTextureSourceSlot(int index)
        => InsertSlot(_textureSourceSlots, TextureSourceHash(_textures[index].Source), index);

    private void InsertTextureHandleSlot(int index)
        => InsertSlot(_textureHandleSlots, HandleHash(_textures[index].Handle), index);

    private void InsertSamplerRecordSlot(int index)
        => InsertSlot(_samplerRecordSlots, SamplerHash(_samplers[index].Record), index);

    private void InsertSamplerHandleSlot(int index)
        => InsertSlot(_samplerHandleSlots, HandleHash(_samplers[index].Handle), index);

    private static void InsertSlot(uint[] slots, uint hash, int index)
    {
        int mask = slots.Length - 1;
        int slot = (int)hash & mask;
        while (slots[slot] != 0u)
            slot = (slot + 1) & mask;
        slots[slot] = checked((uint)index + 1u);
    }

    private void RemoveTextureEntry(int index)
    {
        ref TextureEntry entry = ref _textures[index];
        RemoveSlot(_textureSourceSlots, FindTextureSourceSlot(entry.Source), SlotKind.TextureSource);
        RemoveSlot(_textureHandleSlots, FindHandleSlot(_textureHandleSlots, entry.Handle, true), SlotKind.TextureHandle);
        int last = --_textureCount;
        if (index != last)
        {
            ref TextureEntry replacement = ref _textures[last];
            RemoveSlot(_textureSourceSlots, FindTextureSourceSlot(replacement.Source), SlotKind.TextureSource);
            RemoveSlot(_textureHandleSlots, FindHandleSlot(_textureHandleSlots, replacement.Handle, true), SlotKind.TextureHandle);
            _textures[index] = replacement;
            InsertTextureSourceSlot(index);
            InsertTextureHandleSlot(index);
        }
        _textures[last] = default;
    }

    private void RemoveSamplerEntry(int index)
    {
        ref SamplerEntry entry = ref _samplers[index];
        RemoveSlot(_samplerRecordSlots, FindSamplerRecordSlot(entry.Record), SlotKind.SamplerRecord);
        RemoveSlot(_samplerHandleSlots, FindHandleSlot(_samplerHandleSlots, entry.Handle, false), SlotKind.SamplerHandle);
        int last = --_samplerCount;
        if (index != last)
        {
            ref SamplerEntry replacement = ref _samplers[last];
            RemoveSlot(_samplerRecordSlots, FindSamplerRecordSlot(replacement.Record), SlotKind.SamplerRecord);
            RemoveSlot(_samplerHandleSlots, FindHandleSlot(_samplerHandleSlots, replacement.Handle, false), SlotKind.SamplerHandle);
            _samplers[index] = replacement;
            InsertSamplerRecordSlot(index);
            InsertSamplerHandleSlot(index);
        }
        _samplers[last] = default;
    }

    private void RemoveSlot(uint[] slots, int slot, SlotKind kind)
    {
        if (slot < 0)
            throw new InvalidOperationException("The logical resource lookup table lost a registered entry.");
        int mask = slots.Length - 1;
        slots[slot] = 0u;
        for (int next = (slot + 1) & mask; slots[next] != 0u; next = (next + 1) & mask)
        {
            int index = checked((int)slots[next] - 1);
            slots[next] = 0u;
            uint hash = kind switch
            {
                SlotKind.TextureSource => TextureSourceHash(_textures[index].Source),
                SlotKind.TextureHandle => HandleHash(_textures[index].Handle),
                SlotKind.SamplerRecord => SamplerHash(_samplers[index].Record),
                SlotKind.SamplerHandle => HandleHash(_samplers[index].Handle),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            InsertSlot(slots, hash, index);
        }
    }

    private static bool TextureEquals(AdvancedTextureRecord left, AdvancedTextureRecord right)
        => left.Dimension == right.Dimension && left.Flags == right.Flags &&
           left.Width == right.Width && left.Height == right.Height &&
           left.DepthOrLayers == right.DepthOrLayers && left.MipCount == right.MipCount &&
           left.FormatClass == right.FormatClass &&
           left.EncodedReferenceIndex == right.EncodedReferenceIndex &&
           left.DefaultSampler == right.DefaultSampler && left.UvScaleBias == right.UvScaleBias;

    private static bool SamplerEquals(AdvancedSamplerRecord left, AdvancedSamplerRecord right)
        => left.Filter == right.Filter && left.Flags == right.Flags &&
           left.AddressU == right.AddressU && left.AddressV == right.AddressV &&
           left.AddressW == right.AddressW && left.CompareOperation == right.CompareOperation &&
           VectorBitsEqual(left.LodBiasMinMaxAnisotropy, right.LodBiasMinMaxAnisotropy) &&
           VectorBitsEqual(left.BorderColor, right.BorderColor);

    private static uint TextureSourceHash(XRTexture source)
        => Mix(unchecked((uint)RuntimeHelpers.GetHashCode(source)), 0x9E3779B9u);

    private static uint HandleHash(AdvancedGpuHandle handle)
        => Mix(handle.Index, handle.Generation);

    private static uint SamplerHash(AdvancedSamplerRecord record)
    {
        uint hash = Mix((uint)record.Filter, (uint)record.Flags);
        hash = Mix(hash, (uint)record.AddressU);
        hash = Mix(hash, (uint)record.AddressV);
        hash = Mix(hash, (uint)record.AddressW);
        hash = Mix(hash, (uint)record.CompareOperation);
        hash = MixVector(hash, record.LodBiasMinMaxAnisotropy);
        return MixVector(hash, record.BorderColor);
    }

    private static uint MixVector(uint hash, Vector4 value)
    {
        hash = Mix(hash, CanonicalFloatBits(value.X));
        hash = Mix(hash, CanonicalFloatBits(value.Y));
        hash = Mix(hash, CanonicalFloatBits(value.Z));
        return Mix(hash, CanonicalFloatBits(value.W));
    }

    private static bool VectorBitsEqual(Vector4 left, Vector4 right)
        => CanonicalFloatBits(left.X) == CanonicalFloatBits(right.X) &&
           CanonicalFloatBits(left.Y) == CanonicalFloatBits(right.Y) &&
           CanonicalFloatBits(left.Z) == CanonicalFloatBits(right.Z) &&
           CanonicalFloatBits(left.W) == CanonicalFloatBits(right.W);

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static uint CanonicalFloatBits(float value)
        => value == 0.0f ? 0u : BitConverter.SingleToUInt32Bits(value);

    private static uint Mix(uint hash, uint value)
    {
        hash ^= value + 0x9E3779B9u + (hash << 6) + (hash >> 2);
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        return hash ^ (hash >> 15);
    }

    private static int GetSlotCapacity(uint capacity)
    {
        uint required = checked(capacity * 2u);
        uint result = BitOperations.RoundUpToPowerOf2(required);
        if (result == 0u || result > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        return checked((int)result);
    }

    private struct TextureEntry(
        XRTexture source,
        AdvancedTextureRecord record,
        AdvancedGpuHandle handle,
        uint referenceCount)
    {
        public XRTexture Source = source;
        public AdvancedTextureRecord Record = record;
        public AdvancedGpuHandle Handle = handle;
        public uint ReferenceCount = referenceCount;
    }

    private struct SamplerEntry(
        AdvancedSamplerRecord record,
        AdvancedGpuHandle handle,
        uint referenceCount)
    {
        public AdvancedSamplerRecord Record = record;
        public AdvancedGpuHandle Handle = handle;
        public uint ReferenceCount = referenceCount;
    }

    private enum SlotKind : byte
    {
        TextureSource,
        TextureHandle,
        SamplerRecord,
        SamplerHandle,
    }
}

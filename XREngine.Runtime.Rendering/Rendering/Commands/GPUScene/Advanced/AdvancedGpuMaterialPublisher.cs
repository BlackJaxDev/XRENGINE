using System.Runtime.CompilerServices;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Boundary-owned canonical material publisher. It interns one material row per
/// material/layout/kernel/coverage/state variant; resource owners supply logical
/// texture and sampler handles, so this class never embeds backend descriptor or
/// native-object indices in canonical payloads.
/// </summary>
public sealed class AdvancedGpuMaterialPublisher
{
    private MaterialVariantEntry[] _variants;
    private uint[] _variantSlots;
    private uint[] _materialSlots;
    private uint[] _preflightAcquireCounts;
    private uint[] _preflightReleaseCounts;
    private readonly AdvancedMaterialDatabase _database;
    private int _variantCount;

    public AdvancedGpuMaterialPublisher(AdvancedMaterialDatabase database, uint capacity = 64u)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (capacity == 0u || capacity > int.MaxValue / 2u)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _database = database;
        _variants = new MaterialVariantEntry[checked((int)capacity)];
        _variantSlots = new uint[GetSlotCapacity(capacity)];
        _materialSlots = new uint[GetSlotCapacity(capacity)];
        _preflightAcquireCounts = new uint[_variants.Length];
        _preflightReleaseCounts = new uint[_variants.Length];
    }

    public int VariantCount => _variantCount;

    /// <summary>Reserves storage at a publication boundary. Ordinary acquire/update/release never grows.</summary>
    public void GrowAtFrameBoundary(uint capacity)
    {
        if (capacity <= (uint)_variants.Length)
            return;
        Array.Resize(ref _variants, checked((int)capacity));
        Array.Resize(ref _preflightAcquireCounts, _variants.Length);
        Array.Resize(ref _preflightReleaseCounts, _variants.Length);
        _variantSlots = new uint[GetSlotCapacity(capacity)];
        _materialSlots = new uint[GetSlotCapacity(capacity)];
        for (int index = 0; index < _variantCount; ++index)
        {
            InsertVariantSlot(index);
            InsertMaterialSlot(index);
        }
    }

    /// <summary>Checks the precise legacy layout bridge before mutating any canonical owner.</summary>
    public static bool TryTranslateLayout(MaterialBindingLayout layout, out AdvancedMaterialLayoutTranslation translation, out string reason)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (ReferenceEquals(layout, MaterialBindingLayouts.OpaqueDeferred))
        {
            translation = new(layout, EAdvancedMaterialCoverageMode.Opaque);
            reason = string.Empty;
            return true;
        }
        if (ReferenceEquals(layout, MaterialBindingLayouts.ForwardOpaque))
        {
            translation = new(layout, EAdvancedMaterialCoverageMode.Opaque);
            reason = string.Empty;
            return true;
        }
        if (ReferenceEquals(layout, MaterialBindingLayouts.MaskedForward))
        {
            translation = new(layout, EAdvancedMaterialCoverageMode.Masked);
            reason = string.Empty;
            return true;
        }
        translation = default;
        reason = "Only OpaqueDeferred, ForwardOpaque, and MaskedForward have a canonical advanced-material translation.";
        return false;
    }

    /// <summary>Preflights capacity and layout support without changing database or refcounts.</summary>
    public bool TryPreflight(
        XRMaterial? material,
        MaterialBindingLayout layout,
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedMaterialRenderStateClass state,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings,
        out string reason)
    {
        if (!TryTranslateLayout(layout, out AdvancedMaterialLayoutTranslation translation, out reason))
            return false;
        if (coverage != translation.RequiredCoverage)
        {
            reason = "The requested coverage mode is incompatible with the selected canonical material layout.";
            return false;
        }
        if (constantWords.Length > _database.MaximumConstantWordsPerMaterial || textureBindings.Length > _database.MaximumTextureBindingsPerMaterial)
        {
            reason = "The material payload exceeds the configured fixed logical-slot stride.";
            return false;
        }
        if (FindVariant(material, Hash(layout.LayoutHash), coverage, state) >= 0)
        {
            reason = string.Empty;
            return true;
        }
        if (_variantCount >= _variants.Length)
        {
            reason = "The material variant registry is full; grow it at a publication boundary.";
            return false;
        }
        if (!_database.Materials.CanApply(1, 1, 0))
        {
            reason = "The canonical material table is full; grow it at a publication boundary.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Preflights every material acquire, payload replacement, and owner release
    /// in one transaction. Requests must contain one row per unique variant.
    /// </summary>
    internal bool TryPreflightTransition(
        Span<AdvancedGpuMaterialTransitionRequest> requests,
        ReadOnlySpan<AdvancedGpuMaterialRelease> releases,
        out string reason)
    {
        Array.Clear(_preflightAcquireCounts, 0, _variantCount);
        Array.Clear(_preflightReleaseCounts, 0, _variantCount);

        int materialAdditions = 0;
        int materialUpdates = 0;
        int missingLayoutCount = 0;
        int missingKernelCount = 0;
        uint missingLayoutMemberCount = 0u;
        Span<byte> missingLayouts = stackalloc byte[3];
        Span<byte> missingKernels = stackalloc byte[21];
        int maximumMemberCount = GetMaximumSupportedLayoutMemberCount();
        Span<AdvancedMaterialLayoutMember> memberScratch =
            stackalloc AdvancedMaterialLayoutMember[maximumMemberCount];

        for (int requestIndex = 0; requestIndex < requests.Length; ++requestIndex)
        {
            ref AdvancedGpuMaterialTransitionRequest request = ref requests[requestIndex];
            request.MaterialHandle = AdvancedGpuHandle.Invalid;
            if (!TryTranslateLayout(request.Layout, out AdvancedMaterialLayoutTranslation translation, out reason))
                return false;
            if (request.Coverage != translation.RequiredCoverage)
            {
                reason = "The requested coverage mode is incompatible with the selected canonical material layout.";
                return false;
            }
            if (request.State is <= EAdvancedMaterialRenderStateClass.Invalid or > EAdvancedMaterialRenderStateClass.Refractive)
            {
                reason = "The requested render-state class is not a canonical material state.";
                return false;
            }
            if (request.ConstantWordCount != request.Layout.RowWordCount ||
                request.TextureBindingCount != (uint)request.Layout.Textures.Count ||
                request.ConstantWordCount > _database.MaximumConstantWordsPerMaterial ||
                request.TextureBindingCount > _database.MaximumTextureBindingsPerMaterial)
            {
                reason = "The material payload does not match the selected fixed canonical layout.";
                return false;
            }

            int existingIndex = FindVariant(
                request.Material,
                Hash(request.Layout.LayoutHash),
                request.Coverage,
                request.State);
            if (existingIndex >= 0)
            {
                ref readonly MaterialVariantEntry existing = ref _variants[existingIndex];
                if (request.AcquireCount > uint.MaxValue - existing.ReferenceCount ||
                    _preflightAcquireCounts[existingIndex] > uint.MaxValue - request.AcquireCount ||
                    _preflightAcquireCounts[existingIndex] + request.AcquireCount >
                        uint.MaxValue - existing.ReferenceCount)
                {
                    reason = "The material ownership transition would overflow a variant reference count.";
                    return false;
                }

                _preflightAcquireCounts[existingIndex] += request.AcquireCount;
                request.MaterialHandle = existing.Material;
                if (request.RequiresPayloadUpdate)
                    ++materialUpdates;
                continue;
            }

            if (request.AcquireCount == 0u)
            {
                reason = "A new material variant must acquire at least one draw owner.";
                return false;
            }
            ++materialAdditions;

            int layoutIndex = GetSupportedLayoutIndex(request.Layout);
            AdvancedMaterialLayoutRecord layoutRecord =
                CreateLayoutRecord(request.Layout, memberScratch, out int memberCount);
            bool hasLayout = _database.TryFindLayoutHandle(
                layoutRecord,
                memberScratch[..memberCount],
                out AdvancedGpuHandle layoutHandle);
            if (!hasLayout && missingLayouts[layoutIndex] == 0)
            {
                missingLayouts[layoutIndex] = 1;
                ++missingLayoutCount;
                missingLayoutMemberCount = checked(missingLayoutMemberCount + (uint)memberCount);
            }

            int kernelIndex = checked(layoutIndex * 7 + (int)request.State);
            if (missingKernels[kernelIndex] != 0)
                continue;

            AdvancedShadingKernelRecord kernel =
                CreateKernelRecord(request.Layout, request.Coverage, request.State);
            if (!hasLayout || !_database.TryFindKernelHandle(layoutHandle, kernel, out _))
            {
                missingKernels[kernelIndex] = 1;
                ++missingKernelCount;
            }
        }

        for (int releaseIndex = 0; releaseIndex < releases.Length; ++releaseIndex)
        {
            ref readonly AdvancedGpuMaterialRelease release = ref releases[releaseIndex];
            if (release.Count == 0u)
                continue;
            int variantIndex = FindVariantByMaterial(release.Material);
            if (variantIndex < 0 ||
                _preflightReleaseCounts[variantIndex] > uint.MaxValue - release.Count)
            {
                reason = "The material ownership transition contains an unknown or overflowing release.";
                return false;
            }
            _preflightReleaseCounts[variantIndex] += release.Count;
        }

        int materialTombstones = 0;
        for (int variantIndex = 0; variantIndex < _variantCount; ++variantIndex)
        {
            uint acquired = _preflightAcquireCounts[variantIndex];
            uint released = _preflightReleaseCounts[variantIndex];
            uint available = checked(_variants[variantIndex].ReferenceCount + acquired);
            if (released > available)
            {
                reason = "The material ownership transition releases more references than the variant owns.";
                return false;
            }
            if (released == available)
                ++materialTombstones;
        }

        if (materialAdditions > _variants.Length - _variantCount)
        {
            reason = "The material variant registry is full; grow it at a publication boundary.";
            return false;
        }
        if (missingLayoutMemberCount > _database.LayoutMemberCapacity - _database.LayoutMemberCount)
        {
            reason = "The canonical material-layout member arena is full; grow it at a publication boundary.";
            return false;
        }
        if (!_database.Layouts.CanApply(missingLayoutCount, missingLayoutCount, 0) ||
            !_database.Kernels.CanApply(missingKernelCount, missingKernelCount, 0) ||
            !_database.Materials.CanApply(
                materialAdditions,
                checked(materialAdditions + materialUpdates),
                materialTombstones))
        {
            reason = "The canonical material tables cannot accept the complete ownership transition.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Applies one request from the most recently successful whole-scene preflight.</summary>
    internal void ApplyPreflightedRequest(
        ref AdvancedGpuMaterialTransitionRequest request,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings)
    {
        if (request.MaterialHandle.IsValid)
        {
            if (request.RequiresPayloadUpdate &&
                !TryUpdate(
                    request.MaterialHandle,
                    request.Layout,
                    request.Coverage,
                    request.State,
                    constantWords,
                    textureBindings,
                    out string updateReason))
            {
                throw new InvalidOperationException(
                    $"Preflighted material payload replacement failed: {updateReason}");
            }

            RetainAfterPreflight(request.MaterialHandle, request.AcquireCount);
            return;
        }

        if (!TryAcquire(
                request.Material,
                request.Layout,
                request.Coverage,
                request.State,
                constantWords,
                textureBindings,
                out AdvancedGpuHandle materialHandle,
                out string acquireReason))
        {
            throw new InvalidOperationException(
                $"Preflighted material variant creation failed: {acquireReason}");
        }

        request.MaterialHandle = materialHandle;
        RetainAfterPreflight(materialHandle, request.AcquireCount - 1u);
    }

    /// <summary>Applies aggregated draw-owner releases after every draw has switched ownership.</summary>
    internal void ApplyPreflightedReleases(ReadOnlySpan<AdvancedGpuMaterialRelease> releases)
    {
        for (int releaseIndex = 0; releaseIndex < releases.Length; ++releaseIndex)
        {
            ref readonly AdvancedGpuMaterialRelease release = ref releases[releaseIndex];
            if (release.Count == 0u)
                continue;

            int variantIndex = FindVariantByMaterial(release.Material);
            if (variantIndex < 0)
                throw new InvalidOperationException("A preflighted material release lost its canonical variant.");

            ref MaterialVariantEntry entry = ref _variants[variantIndex];
            if (release.Count > entry.ReferenceCount)
                throw new InvalidOperationException("A preflighted material release exceeded its retained ownership.");
            if (release.Count < entry.ReferenceCount)
            {
                entry.ReferenceCount -= release.Count;
                continue;
            }

            if (!_database.RemoveMaterial(entry.Material))
                throw new InvalidOperationException("A preflighted final material release could not tombstone its row.");
            RemoveVariant(variantIndex);
        }
    }

    internal bool TryFindVariant(
        XRMaterial? material,
        MaterialBindingLayout layout,
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedMaterialRenderStateClass state,
        out AdvancedGpuHandle materialHandle)
    {
        int index = FindVariant(material, Hash(layout.LayoutHash), coverage, state);
        materialHandle = index < 0 ? AdvancedGpuHandle.Invalid : _variants[index].Material;
        return index >= 0;
    }

    internal bool TryGetReferenceCount(AdvancedGpuHandle materialHandle, out uint referenceCount)
    {
        int index = FindVariantByMaterial(materialHandle);
        referenceCount = index < 0 ? 0u : _variants[index].ReferenceCount;
        return index >= 0;
    }

    internal bool HeaderMatches(
        AdvancedGpuHandle materialHandle,
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedMaterialRenderStateClass state,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings)
    {
        if (!_database.Materials.TryGet(materialHandle, out AdvancedMaterialRecord current))
            return false;
        AdvancedMaterialRecord expected =
            CreateMaterialRecord(coverage, state, textureBindings);
        return current.RenderStateClass == expected.RenderStateClass &&
            current.CoverageMode == expected.CoverageMode &&
            current.RequiredAttributeMask == expected.RequiredAttributeMask &&
            current.FeatureFlags == expected.FeatureFlags &&
            current.EligibilityFlags == expected.EligibilityFlags;
    }

    /// <summary>Acquires a shared material variant, creating its layout, kernel, and material rows on first use.</summary>
    public bool TryAcquire(
        XRMaterial? material,
        MaterialBindingLayout layout,
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedMaterialRenderStateClass state,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings,
        out AdvancedGpuHandle materialHandle,
        out string reason)
    {
        materialHandle = AdvancedGpuHandle.Invalid;
        if (!TryPreflight(material, layout, coverage, state, constantWords, textureBindings, out reason))
            return false;
        int existing = FindVariant(material, Hash(layout.LayoutHash), coverage, state);
        if (existing >= 0)
        {
            ref MaterialVariantEntry entry = ref _variants[existing];
            checked { ++entry.ReferenceCount; }
            materialHandle = entry.Material;
            return true;
        }

        Span<AdvancedMaterialLayoutMember> members = stackalloc AdvancedMaterialLayoutMember[
            layout.PackedMembers.Count + layout.Textures.Count];
        AdvancedMaterialLayoutRecord layoutRecord = CreateLayoutRecord(layout, members, out int memberCount);
        AdvancedShadingKernelRecord kernelRecord = CreateKernelRecord(layout, coverage, state);
        AdvancedMaterialRecord materialRecord =
            CreateMaterialRecord(coverage, state, textureBindings);
        if (!_database.TryAddMaterialWithInternedSchema(
                layoutRecord,
                members[..memberCount],
                kernelRecord,
                materialRecord,
                ReadOnlySpan<AdvancedMaterialValueDescriptor>.Empty,
                constantWords,
                textureBindings,
                out AdvancedMaterialVariantHandles handles,
                out EAdvancedMaterialVariantCreationFailure failure))
        {
            reason = $"Canonical material variant creation failed ({(uint)failure}: {failure}).";
            return false;
        }
        materialHandle = handles.Material;

        int index = _variantCount++;
        _variants[index] = new MaterialVariantEntry(material, Hash(layout.LayoutHash), coverage, state, materialHandle, 1u);
        InsertVariantSlot(index);
        InsertMaterialSlot(index);
        reason = string.Empty;
        return true;
    }

    /// <summary>Overwrites an existing variant's fixed payload slot without allocating or changing its identity.</summary>
    public bool TryUpdate(
        AdvancedGpuHandle materialHandle,
        MaterialBindingLayout layout,
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedMaterialRenderStateClass state,
        ReadOnlySpan<uint> constantWords,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings,
        out string reason)
    {
        reason = string.Empty;
        int index = FindVariantByMaterial(materialHandle);
        if (index < 0)
        {
            reason = "Unknown canonical material variant.";
            return false;
        }

        ref readonly MaterialVariantEntry entry = ref _variants[index];
        if (entry.LayoutHash != Hash(layout.LayoutHash) || entry.Coverage != coverage || entry.State != state)
        {
            reason = "Material variant identity is immutable; acquire the new layout/coverage/state variant before releasing the old one.";
            return false;
        }

        if (!TryPreflight(entry.MaterialReference, layout, coverage, state, constantWords, textureBindings, out reason))
        {
            return false;
        }
        if (!_database.TryGetLayoutHandle(materialHandle, out AdvancedGpuHandle layoutHandle) ||
            !_database.Materials.TryGet(materialHandle, out AdvancedMaterialRecord existingMaterial))
        {
            reason = "Canonical material variant schema is no longer current.";
            return false;
        }
        AdvancedGpuHandle kernelHandle = new(
            existingMaterial.ShadingKernelId,
            existingMaterial.ShadingKernelGeneration);
        if (!_database.Kernels.IsCurrent(kernelHandle))
        {
            reason = "Canonical material variant kernel is no longer current.";
            return false;
        }
        AdvancedMaterialRecord record =
            CreateMaterialRecord(coverage, state, textureBindings);
        if (!_database.TryReplaceMaterial(materialHandle, layoutHandle, kernelHandle, record, ReadOnlySpan<AdvancedMaterialValueDescriptor>.Empty, constantWords, textureBindings))
        {
            reason = "Canonical material replacement failed validation.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Releases one draw/reference; the material row is retired only when its shared refcount reaches zero.</summary>
    public bool TryRelease(AdvancedGpuHandle materialHandle)
    {
        int index = FindVariantByMaterial(materialHandle);
        if (index < 0)
            return false;
        ref MaterialVariantEntry entry = ref _variants[index];
        if (entry.ReferenceCount > 1u)
        {
            --entry.ReferenceCount;
            return true;
        }
        if (!_database.RemoveMaterial(materialHandle))
            return false;
        RemoveVariant(index);
        return true;
    }

    private static AdvancedMaterialLayoutRecord CreateLayoutRecord(
        MaterialBindingLayout layout,
        Span<AdvancedMaterialLayoutMember> members,
        out int memberCount)
    {
        int count = 0;
        for (int memberIndex = 0; memberIndex < layout.PackedMembers.Count; ++memberIndex)
        {
            MaterialBindingPackedMember member = layout.PackedMembers[memberIndex];
            members[count++] = new(
                Hash(member.Name),
                TranslateValueKind(member.GlslType),
                member.WordOffset,
                member.WordCount,
                0u,
                member.WordOffset);
        }
        for (int textureIndex = 0; textureIndex < layout.Textures.Count; ++textureIndex)
        {
            MaterialTextureBinding texture = layout.Textures[textureIndex];
            members[count++] = new(
                Hash(texture.Semantic),
                EAdvancedMaterialValueKind.Texture,
                checked((uint)textureIndex),
                1u);
        }
        memberCount = count;
        return new AdvancedMaterialLayoutRecord
        {
            LayoutHash = Hash(layout.LayoutHash),
            ConstantWordCount = layout.RowWordCount,
            TextureReferenceCount = checked((uint)layout.Textures.Count),
        };
    }

    private static EAdvancedMaterialValueKind TranslateValueKind(string glslType)
        => glslType switch
        {
            "uint" => EAdvancedMaterialValueKind.UInt,
            "int" => EAdvancedMaterialValueKind.Int,
            "float" => EAdvancedMaterialValueKind.Float,
            "vec2" => EAdvancedMaterialValueKind.Vector2,
            "vec3" => EAdvancedMaterialValueKind.Vector3,
            "vec4" => EAdvancedMaterialValueKind.Vector4,
            "mat4" => EAdvancedMaterialValueKind.Matrix4x4,
            _ => throw new NotSupportedException(
                $"GLSL material member type '{glslType}' has no canonical value-kind translation."),
        };

    private static int GetSupportedLayoutIndex(MaterialBindingLayout layout)
    {
        if (ReferenceEquals(layout, MaterialBindingLayouts.OpaqueDeferred))
            return 0;
        if (ReferenceEquals(layout, MaterialBindingLayouts.ForwardOpaque))
            return 1;
        if (ReferenceEquals(layout, MaterialBindingLayouts.MaskedForward))
            return 2;
        throw new ArgumentOutOfRangeException(nameof(layout), "The layout is not part of the bounded canonical bridge.");
    }

    private static int GetMaximumSupportedLayoutMemberCount()
        => Math.Max(
            MaterialBindingLayouts.OpaqueDeferred.PackedMembers.Count +
                MaterialBindingLayouts.OpaqueDeferred.Textures.Count,
            Math.Max(
                MaterialBindingLayouts.ForwardOpaque.PackedMembers.Count +
                    MaterialBindingLayouts.ForwardOpaque.Textures.Count,
                MaterialBindingLayouts.MaskedForward.PackedMembers.Count +
                    MaterialBindingLayouts.MaskedForward.Textures.Count));

    private static AdvancedShadingKernelRecord CreateKernelRecord(
        MaterialBindingLayout layout,
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedMaterialRenderStateClass state)
    {
        uint coverageMask = 1u << checked((int)coverage);
        return new AdvancedShadingKernelRecord
        {
            MaterialLayoutHash = Hash(layout.LayoutHash),
            SupportedCoverageMask = coverageMask,
            SupportedEligibility = EAdvancedMaterialEligibilityFlags.NativeOpaque | EAdvancedMaterialEligibilityFlags.NativeMasked | EAdvancedMaterialEligibilityFlags.LateTransparent | EAdvancedMaterialEligibilityFlags.LateRefractive | EAdvancedMaterialEligibilityFlags.Unlit,
            SupportedFeatures = EAdvancedMaterialFeatureFlags.BaseColorTexture | EAdvancedMaterialFeatureFlags.NormalTexture | EAdvancedMaterialFeatureFlags.MetallicRoughnessTexture | EAdvancedMaterialFeatureFlags.Emissive | EAdvancedMaterialFeatureFlags.DoubleSided | EAdvancedMaterialFeatureFlags.ReceivesShadows | EAdvancedMaterialFeatureFlags.CastsShadows | EAdvancedMaterialFeatureFlags.VertexDeformation | EAdvancedMaterialFeatureFlags.Animated,
            ShaderIdentityHash = Mix(Hash(layout.LayoutHash), ((ulong)(uint)coverage << 32) | (uint)state),
            RenderStateClassMask = 1u << checked((int)state),
        };
    }

    private static AdvancedMaterialRecord CreateMaterialRecord(
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedMaterialRenderStateClass state,
        ReadOnlySpan<AdvancedMaterialTextureBinding> textureBindings)
    {
        EAdvancedMaterialFeatureFlags features = EAdvancedMaterialFeatureFlags.None;
        if (textureBindings.Length > 0 && textureBindings[0].Texture.Handle.IsValid)
            features |= EAdvancedMaterialFeatureFlags.BaseColorTexture;
        if (textureBindings.Length > 1 && textureBindings[1].Texture.Handle.IsValid)
            features |= EAdvancedMaterialFeatureFlags.NormalTexture;
        if (textureBindings.Length > 2 && textureBindings[2].Texture.Handle.IsValid)
            features |= EAdvancedMaterialFeatureFlags.MetallicRoughnessTexture;
        if (state is EAdvancedMaterialRenderStateClass.OpaqueDoubleSided or
            EAdvancedMaterialRenderStateClass.MaskedDoubleSided)
        {
            features |= EAdvancedMaterialFeatureFlags.DoubleSided;
        }

        EAdvancedMaterialEligibilityFlags eligibility = coverage switch
        {
            EAdvancedMaterialCoverageMode.Opaque =>
                EAdvancedMaterialEligibilityFlags.NativeOpaque,
            EAdvancedMaterialCoverageMode.Masked =>
                EAdvancedMaterialEligibilityFlags.NativeMasked,
            EAdvancedMaterialCoverageMode.Transparent =>
                EAdvancedMaterialEligibilityFlags.LateTransparent,
            EAdvancedMaterialCoverageMode.Refractive =>
                EAdvancedMaterialEligibilityFlags.LateRefractive,
            _ => EAdvancedMaterialEligibilityFlags.Unsupported,
        };
        return new AdvancedMaterialRecord
        {
            RenderStateClass = state,
            CoverageMode = coverage,
            RequiredAttributeMask = EAdvancedMaterialRequiredAttributeMask.None,
            FeatureFlags = features,
            EligibilityFlags = eligibility,
        };
    }

    private int FindVariant(XRMaterial? material, ulong layoutHash, EAdvancedMaterialCoverageMode coverage, EAdvancedMaterialRenderStateClass state)
    {
        int slot = FindVariantSlot(material, layoutHash, coverage, state);
        return slot < 0 ? -1 : checked((int)_variantSlots[slot] - 1);
    }

    private void RetainAfterPreflight(AdvancedGpuHandle materialHandle, uint count)
    {
        if (count == 0u)
            return;
        int index = FindVariantByMaterial(materialHandle);
        if (index < 0 || count > uint.MaxValue - _variants[index].ReferenceCount)
            throw new InvalidOperationException("A preflighted material retain lost its bounded variant capacity.");
        _variants[index].ReferenceCount += count;
    }

    private int FindVariantSlot(XRMaterial? material, ulong layoutHash, EAdvancedMaterialCoverageMode coverage, EAdvancedMaterialRenderStateClass state)
    {
        uint hash = VariantHash(material, layoutHash, coverage, state);
        int mask = _variantSlots.Length - 1;
        for (int slot = (int)hash & mask, probe = 0; probe < _variantSlots.Length; ++probe, slot = (slot + 1) & mask)
        {
            uint stored = _variantSlots[slot];
            if (stored == 0u)
                return -1;
            int index = checked((int)stored - 1);
            ref MaterialVariantEntry entry = ref _variants[index];
            if (ReferenceEquals(entry.MaterialReference, material) && entry.LayoutHash == layoutHash && entry.Coverage == coverage && entry.State == state)
                return slot;
        }
        return -1;
    }

    private int FindVariantByMaterial(AdvancedGpuHandle material)
    {
        int slot = FindMaterialSlot(material);
        return slot < 0 ? -1 : checked((int)_materialSlots[slot] - 1);
    }

    private int FindMaterialSlot(AdvancedGpuHandle material)
    {
        uint hash = MaterialHash(material);
        int mask = _materialSlots.Length - 1;
        for (int slot = (int)hash & mask, probe = 0; probe < _materialSlots.Length; ++probe, slot = (slot + 1) & mask)
        {
            uint stored = _materialSlots[slot];
            if (stored == 0u)
                return -1;
            int index = checked((int)stored - 1);
            if (_variants[index].Material == material)
                return slot;
        }
        return -1;
    }

    private void InsertVariantSlot(int index)
    {
        ref MaterialVariantEntry entry = ref _variants[index];
        InsertSlot(_variantSlots, VariantHash(entry.MaterialReference, entry.LayoutHash, entry.Coverage, entry.State), index);
    }

    private void InsertMaterialSlot(int index)
        => InsertSlot(_materialSlots, MaterialHash(_variants[index].Material), index);

    private static void InsertSlot(uint[] slots, uint hash, int index)
    {
        int mask = slots.Length - 1;
        int slot = (int)hash & mask;
        while (slots[slot] != 0u)
            slot = (slot + 1) & mask;
        slots[slot] = checked((uint)index + 1u);
    }

    private void RemoveVariant(int index)
    {
        ref MaterialVariantEntry entry = ref _variants[index];
        RemoveSlot(_variantSlots, FindVariantSlot(entry.MaterialReference, entry.LayoutHash, entry.Coverage, entry.State), false);
        RemoveSlot(_materialSlots, FindMaterialSlot(entry.Material), true);

        int last = --_variantCount;
        if (index != last)
        {
            ref MaterialVariantEntry replacement = ref _variants[last];
            RemoveSlot(_variantSlots, FindVariantSlot(replacement.MaterialReference, replacement.LayoutHash, replacement.Coverage, replacement.State), false);
            RemoveSlot(_materialSlots, FindMaterialSlot(replacement.Material), true);
            _variants[index] = _variants[last];
            InsertVariantSlot(index);
            InsertMaterialSlot(index);
        }
        _variants[last] = default;
    }

    private void RemoveSlot(uint[] slots, int slot, bool materialSlot)
    {
        if (slot < 0)
            throw new InvalidOperationException("The material variant lookup table lost a registered entry.");

        int mask = slots.Length - 1;
        slots[slot] = 0u;
        for (int next = (slot + 1) & mask; slots[next] != 0u; next = (next + 1) & mask)
        {
            int index = checked((int)slots[next] - 1);
            slots[next] = 0u;
            ref MaterialVariantEntry entry = ref _variants[index];
            uint hash = materialSlot
                ? MaterialHash(entry.Material)
                : VariantHash(entry.MaterialReference, entry.LayoutHash, entry.Coverage, entry.State);
            InsertSlot(slots, hash, index);
        }
    }

    private static uint VariantHash(XRMaterial? material, ulong layoutHash, EAdvancedMaterialCoverageMode coverage, EAdvancedMaterialRenderStateClass state)
        => unchecked((uint)Mix(layoutHash, ((ulong)(uint)RuntimeHelpers.GetHashCode(material!) << 32) | ((ulong)(uint)coverage << 16) | (uint)state));
    private static uint MaterialHash(AdvancedGpuHandle material)
        => unchecked((uint)Mix(material.Index, material.Generation));
    private static uint GetSlotCapacity(uint capacity)
    {
        uint result = 1u;
        while (result < checked(capacity * 2u)) result <<= 1;
        return result;
    }
    private static ulong Hash(string value)
    {
        ulong hash = 14695981039346656037ul;
        for (int i = 0; i < value.Length; ++i) { hash ^= value[i]; hash *= 1099511628211ul; }
        return hash;
    }
    private static ulong Mix(ulong value, ulong input) => (value ^ input) * 1099511628211ul;

    private struct MaterialVariantEntry
    {
        public MaterialVariantEntry(XRMaterial? materialReference, ulong layoutHash, EAdvancedMaterialCoverageMode coverage, EAdvancedMaterialRenderStateClass state, AdvancedGpuHandle material, uint referenceCount)
            => (MaterialReference, LayoutHash, Coverage, State, Material, ReferenceCount) = (materialReference, layoutHash, coverage, state, material, referenceCount);
        public XRMaterial? MaterialReference;
        public ulong LayoutHash;
        public EAdvancedMaterialCoverageMode Coverage;
        public EAdvancedMaterialRenderStateClass State;
        public AdvancedGpuHandle Material;
        public uint ReferenceCount;
    }
}

/// <summary>Resolved canonical translation for one supported legacy binding layout.</summary>
public readonly record struct AdvancedMaterialLayoutTranslation(MaterialBindingLayout Layout, EAdvancedMaterialCoverageMode RequiredCoverage);

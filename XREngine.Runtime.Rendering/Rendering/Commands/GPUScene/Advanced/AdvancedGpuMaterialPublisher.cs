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
    }

    /// <summary>Reserves storage at a publication boundary. Ordinary acquire/update/release never grows.</summary>
    public void GrowAtFrameBoundary(uint capacity)
    {
        if (capacity <= (uint)_variants.Length)
            return;
        Array.Resize(ref _variants, checked((int)capacity));
        _variantSlots = new uint[GetSlotCapacity(capacity)];
        for (int index = 0; index < _variantCount; ++index)
            InsertVariantSlot(index);
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
        if (FindVariant(material, Hash(layout.LayoutHash), coverage, state) >= 0 || _variantCount < _variants.Length)
        {
            reason = string.Empty;
            return true;
        }
        reason = "The material variant registry is full; grow it at a publication boundary.";
        return false;
    }

    /// <summary>Acquires a shared material variant, creating its layout/kernel/material rows atomically on first use.</summary>
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

        if (!TryGetOrAddLayout(layout, out AdvancedGpuHandle layoutHandle) ||
            !TryGetOrAddKernel(layoutHandle, layout, coverage, state, out AdvancedGpuHandle kernelHandle))
        {
            reason = "The canonical material layout or kernel table is full.";
            return false;
        }

        AdvancedMaterialRecord record = new()
        {
            RenderStateClass = state,
            CoverageMode = coverage,
            RequiredAttributeMask = EAdvancedMaterialRequiredAttributeMask.None,
        };
        if (!_database.TryAddMaterial(layoutHandle, kernelHandle, record, ReadOnlySpan<AdvancedMaterialValueDescriptor>.Empty,
            constantWords, textureBindings, out materialHandle))
        {
            reason = "The canonical material table or its fixed payload slots are full.";
            return false;
        }

        int index = _variantCount++;
        _variants[index] = new MaterialVariantEntry(material, Hash(layout.LayoutHash), coverage, state, materialHandle, 1u);
        InsertVariantSlot(index);
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
        if (index < 0 || !TryPreflight(_variants[index].MaterialReference, layout, coverage, state, constantWords, textureBindings, out reason) ||
            !TryGetOrAddLayout(layout, out AdvancedGpuHandle layoutHandle) ||
            !TryGetOrAddKernel(layoutHandle, layout, coverage, state, out AdvancedGpuHandle kernelHandle))
        {
            reason = string.IsNullOrEmpty(reason) ? "Unknown material variant or unavailable canonical layout/kernel." : reason;
            return false;
        }
        AdvancedMaterialRecord record = new() { RenderStateClass = state, CoverageMode = coverage };
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

    private bool TryGetOrAddLayout(MaterialBindingLayout layout, out AdvancedGpuHandle handle)
    {
        Span<AdvancedMaterialLayoutMember> members = stackalloc AdvancedMaterialLayoutMember[layout.PackedMembers.Count + layout.Textures.Count];
        int count = 0;
        foreach (MaterialBindingPackedMember member in layout.PackedMembers)
            members[count++] = new(Hash(member.Name), EAdvancedMaterialValueKind.Float, member.WordOffset, member.WordCount, 0u, member.WordOffset);
        foreach (MaterialTextureBinding texture in layout.Textures)
            members[count++] = new(Hash(texture.Semantic), EAdvancedMaterialValueKind.Texture, checked((uint)(count - layout.PackedMembers.Count - 1)), 1u);
        AdvancedMaterialLayoutRecord record = new()
        {
            LayoutHash = Hash(layout.LayoutHash),
            ConstantWordCount = layout.RowWordCount,
            TextureReferenceCount = checked((uint)layout.Textures.Count),
        };
        if (_database.TryFindLayoutHandle(in record, members[..count], out handle))
            return true;
        return _database.TryAddLayout(record, members[..count], out handle);
    }

    private bool TryGetOrAddKernel(AdvancedGpuHandle layoutHandle, MaterialBindingLayout layout, EAdvancedMaterialCoverageMode coverage, EAdvancedMaterialRenderStateClass state, out AdvancedGpuHandle handle)
    {
        uint coverageMask = 1u << checked((int)coverage);
        AdvancedShadingKernelRecord record = new()
        {
            MaterialLayoutHash = Hash(layout.LayoutHash),
            SupportedCoverageMask = coverageMask,
            SupportedEligibility = EAdvancedMaterialEligibilityFlags.NativeOpaque | EAdvancedMaterialEligibilityFlags.NativeMasked | EAdvancedMaterialEligibilityFlags.LateTransparent | EAdvancedMaterialEligibilityFlags.LateRefractive | EAdvancedMaterialEligibilityFlags.Unlit,
            SupportedFeatures = EAdvancedMaterialFeatureFlags.BaseColorTexture | EAdvancedMaterialFeatureFlags.NormalTexture | EAdvancedMaterialFeatureFlags.MetallicRoughnessTexture | EAdvancedMaterialFeatureFlags.Emissive | EAdvancedMaterialFeatureFlags.DoubleSided | EAdvancedMaterialFeatureFlags.ReceivesShadows | EAdvancedMaterialFeatureFlags.CastsShadows | EAdvancedMaterialFeatureFlags.VertexDeformation | EAdvancedMaterialFeatureFlags.Animated,
            ShaderIdentityHash = Mix(Hash(layout.LayoutHash), ((ulong)(uint)coverage << 32) | (uint)state),
            RenderStateClassMask = 1u << checked((int)state),
        };
        if (_database.TryFindKernelHandle(layoutHandle, in record, out handle))
            return true;
        return _database.TryAddKernel(layoutHandle, record, out handle);
    }

    private int FindVariant(XRMaterial? material, ulong layoutHash, EAdvancedMaterialCoverageMode coverage, EAdvancedMaterialRenderStateClass state)
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
                return index;
        }
        return -1;
    }

    private int FindVariantByMaterial(AdvancedGpuHandle material)
    {
        for (int index = 0; index < _variantCount; ++index)
            if (_variants[index].Material == material)
                return index;
        return -1;
    }

    private void InsertVariantSlot(int index)
    {
        ref MaterialVariantEntry entry = ref _variants[index];
        int mask = _variantSlots.Length - 1;
        int slot = (int)VariantHash(entry.MaterialReference, entry.LayoutHash, entry.Coverage, entry.State) & mask;
        while (_variantSlots[slot] != 0u)
            slot = (slot + 1) & mask;
        _variantSlots[slot] = checked((uint)index + 1u);
    }

    private void RemoveVariant(int index)
    {
        int last = --_variantCount;
        if (index != last)
            _variants[index] = _variants[last];
        _variants[last] = default;
        Array.Clear(_variantSlots);
        for (int rebuild = 0; rebuild < _variantCount; ++rebuild)
            InsertVariantSlot(rebuild);
    }

    private static uint VariantHash(XRMaterial? material, ulong layoutHash, EAdvancedMaterialCoverageMode coverage, EAdvancedMaterialRenderStateClass state)
        => unchecked((uint)Mix(layoutHash, ((ulong)(uint)RuntimeHelpers.GetHashCode(material!) << 32) | ((ulong)(uint)coverage << 16) | (uint)state));
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

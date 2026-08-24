using System.Threading;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Render-thread-owned, bounded resident-template table. Primary lookup is an
/// array access by canonical handle index. Each canonical draw owns exactly one
/// active sealed variant, so stable lookup avoids hashing, allocation, variant
/// scans, and structural comparison.
/// </summary>
internal sealed class VulkanResidentDrawTemplateTable : IDisposable
{
    private sealed class PrimarySlot
    {
        internal readonly VulkanResidentDrawTemplate?[] Variants;
        internal readonly uint[] EntryGenerations;
        internal ulong DatabaseEpoch;
        internal uint HandleGeneration;

        internal PrimarySlot(int variantsPerDraw)
        {
            Variants = new VulkanResidentDrawTemplate[variantsPerDraw];
            EntryGenerations = new uint[variantsPerDraw];
        }

        internal bool Matches(in AdvancedGpuSceneDrawIdentity primary)
            => DatabaseEpoch == primary.DatabaseEpoch &&
            HandleGeneration == primary.Handle.Generation;

        internal void SetIdentity(in AdvancedGpuSceneDrawIdentity primary)
        {
            DatabaseEpoch = primary.DatabaseEpoch;
            HandleGeneration = primary.Handle.Generation;
        }

        internal void ClearIdentity()
        {
            DatabaseEpoch = 0u;
            HandleGeneration = 0u;
        }
    }

    private readonly VulkanResourceRuntime _resourceRuntime;
    private readonly int _ownerThreadId;
    private readonly int _variantsPerDraw;
    private PrimarySlot[] _primarySlots;
    private int _residentCount;
    private long _hits;
    private long _misses;
    private long _creates;
    private long _replacements;
    private long _evictions;
    private long _fullStructuralComparisons;
    private long _dependencyRejects;
    private long _capacityFailures;
    private ulong _lastProjectionDatabaseEpoch;
    private ulong _lastProjectionSequence;

    internal VulkanResidentDrawTemplateTable(
        VulkanResourceRuntime resourceRuntime,
        uint primaryCapacity,
        uint variantsPerDraw)
    {
        ArgumentNullException.ThrowIfNull(resourceRuntime);
        if (primaryCapacity == 0u || primaryCapacity >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(primaryCapacity));
        if (variantsPerDraw != 1u)
            throw new ArgumentOutOfRangeException(nameof(variantsPerDraw));

        _resourceRuntime = resourceRuntime;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _variantsPerDraw = checked((int)variantsPerDraw);
        _primarySlots = CreateSlots(checked((int)primaryCapacity + 1), _variantsPerDraw);
    }

    internal uint PrimaryCapacity => checked((uint)_primarySlots.Length - 1u);
    internal int VariantsPerDraw => _variantsPerDraw;
    internal int ResidentCount => _residentCount;

    internal VulkanResidentDrawTemplateTelemetrySnapshot CaptureTelemetry()
        => new(
            unchecked((ulong)Volatile.Read(ref _hits)),
            unchecked((ulong)Volatile.Read(ref _misses)),
            unchecked((ulong)Volatile.Read(ref _creates)),
            unchecked((ulong)Volatile.Read(ref _replacements)),
            unchecked((ulong)Volatile.Read(ref _evictions)),
            unchecked((ulong)Volatile.Read(ref _fullStructuralComparisons)),
            unchecked((ulong)Volatile.Read(ref _dependencyRejects)),
            unchecked((ulong)Volatile.Read(ref _capacityFailures)),
            _residentCount);

    /// <summary>
    /// Stable-frame lookup: direct primary slot, exact active variant,
    /// separated non-content generations, and lease freshness only. It never
    /// hashes or performs full structural equality.
    /// </summary>
    internal bool TryResolve(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw,
        in VulkanResidentDrawTemplateVariantKey variant,
        in VulkanResidentDrawTemplateGenerationDomains generations,
        out VulkanResidentDrawTemplateHandle handle,
        out VulkanResidentDrawTemplate? template)
    {
        VerifyOwnerThread();
        handle = default;
        template = null;
        AdvancedGpuSceneDrawIdentity primary = canonicalDraw.Primary;
        if (!TryGetMatchingPrimarySlot(in primary, out PrimarySlot slot))
            return RecordMiss();

        const int index = 0;
        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        VulkanResidentDrawTemplate? candidate = variants[index];
        if (candidate is not null && candidate.Variant == variant)
        {
            if (!candidate.Generations.IsStructurallyCompatibleWith(in generations))
                return RecordMiss();
            if (!_resourceRuntime.IsResidentTemplateDependencyLeaseCurrent(
                    candidate.DependencyLease))
            {
                candidate.Dispose();
                variants[index] = null;
                slot.EntryGenerations[index] = NextGeneration(
                    slot.EntryGenerations[index]);
                --_residentCount;
                Interlocked.Increment(ref _dependencyRejects);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                    dependencyRejected: true);
                return RecordMiss();
            }

            if (candidate.Generations.DataContent != generations.DataContent)
                candidate.AdvanceDataContent(generations.DataContent);

            template = candidate;
            handle = new VulkanResidentDrawTemplateHandle(
                primary.Handle.Index,
                primary.Handle.Generation,
                primary.DatabaseEpoch,
                checked((ushort)index),
                slot.EntryGenerations[index]);
            Interlocked.Increment(ref _hits);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                hit: true);
            return true;
        }

        return RecordMiss();
    }

    /// <summary>
    /// Validates a producer-published direct address and its cheap routing key
    /// without rebuilding structural or artifact generations.
    /// </summary>
    internal bool TryGetResolved(
        in VulkanResidentDrawTemplateHandle handle,
        in VulkanResidentDrawTemplateVariantKey expectedVariant,
        out VulkanResidentDrawTemplate? template)
    {
        VerifyOwnerThread();
        template = null;
        if (!handle.IsValid || handle.PrimaryIndex >= (uint)_primarySlots.Length)
            return RecordMiss();

        PrimarySlot slot = _primarySlots[checked((int)handle.PrimaryIndex)];
        int variantOrdinal = handle.VariantOrdinal;
        if (slot.DatabaseEpoch != handle.DatabaseEpoch ||
            slot.HandleGeneration != handle.CanonicalHandleGeneration ||
            (uint)variantOrdinal >= (uint)slot.Variants.Length ||
            slot.EntryGenerations[variantOrdinal] != handle.EntryGeneration ||
            slot.Variants[variantOrdinal] is not { } candidate ||
            candidate.Variant != expectedVariant ||
            !_resourceRuntime.IsResidentTemplateDependencyLeaseCurrent(
                candidate.DependencyLease))
        {
            return RecordMiss();
        }

        template = candidate;
        Interlocked.Increment(ref _hits);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
            hit: true);
        return true;
    }

    /// <summary>
    /// Validates a previously resolved resident variant with exactly one primary
    /// and one variant access, then retains its native dependencies for prepared
    /// and submitted use.
    /// </summary>
    internal bool TryGetResolvedAndRetain(
        in VulkanResidentDrawTemplateHandle handle,
        out VulkanResidentDrawTemplate? template)
    {
        VerifyOwnerThread();
        template = null;
        if (!handle.IsValid || handle.PrimaryIndex >= (uint)_primarySlots.Length)
            return RecordMiss();

        PrimarySlot slot = _primarySlots[checked((int)handle.PrimaryIndex)];
        int variantOrdinal = handle.VariantOrdinal;
        if (slot.DatabaseEpoch != handle.DatabaseEpoch ||
            slot.HandleGeneration != handle.CanonicalHandleGeneration ||
            (uint)variantOrdinal >= (uint)slot.Variants.Length ||
            slot.EntryGenerations[variantOrdinal] != handle.EntryGeneration ||
            slot.Variants[variantOrdinal] is not { } candidate ||
            !_resourceRuntime.IsResidentTemplateDependencyLeaseCurrent(
                candidate.DependencyLease) ||
            !candidate.TryAcquireUse())
        {
            return RecordMiss();
        }

        template = candidate;
        Interlocked.Increment(ref _hits);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
            hit: true);
        return true;
    }

    /// <summary>
    /// Creates or atomically replaces one fixed variant slot. Callers must have
    /// already acquired <paramref name="dependencyLease"/> transactionally; this
    /// table disposes it on every rejected or superseded publication path.
    /// </summary>
    internal bool TryCreateOrReplace(
        in VulkanResidentDrawTemplateStructuralIdentity structuralIdentity,
        in VulkanResidentDrawTemplateVariantKey variant,
        in VulkanResidentDrawTemplateGenerationDomains generations,
        in VulkanResidentDrawTemplateNativeState nativeState,
        VulkanResidentTemplateDependencyLease? dependencyLease,
        out VulkanResidentDrawTemplate? template,
        out VulkanResidentDrawTemplateHandle handle)
    {
        VerifyOwnerThread();
        template = null;
        handle = default;
        AdvancedGpuSceneDrawIdentity primary = structuralIdentity.CanonicalDraw.Primary;
        if (!structuralIdentity.IsValid ||
            !nativeState.IsValid ||
            dependencyLease is null ||
            !_resourceRuntime.IsResidentTemplateDependencyLeaseCurrent(dependencyLease))
        {
            dependencyLease?.Dispose();
            Interlocked.Increment(ref _dependencyRejects);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                dependencyRejected: true);
            return false;
        }
        if (primary.Handle.Index >= (uint)_primarySlots.Length)
        {
            dependencyLease.Dispose();
            Interlocked.Increment(ref _capacityFailures);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                capacityFailure: true);
            return false;
        }

        PrimarySlot slot = _primarySlots[checked((int)primary.Handle.Index)];
        if (slot.DatabaseEpoch != 0u && !slot.Matches(in primary))
            EvictSlot(slot);
        if (slot.DatabaseEpoch == 0u)
            slot.SetIdentity(in primary);

        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        const int targetVariant = 0;

        VulkanResidentDrawTemplate? existing = variants[targetVariant];
        if (existing is not null && existing.Variant == variant)
        {
            Interlocked.Increment(ref _fullStructuralComparisons);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                structuralComparison: true);
            if (existing.StructuralIdentity.StructurallyEquals(in structuralIdentity) &&
                existing.Generations.IsStructurallyCompatibleWith(in generations))
            {
                dependencyLease.Dispose();
                existing.AdvanceDataContent(generations.DataContent);
                template = existing;
                handle = CreateHandle(in primary, slot, targetVariant);
                return true;
            }
        }

        // Construct before releasing the existing lease so an allocation failure
        // cannot leave the variant slot empty or partially published.
        VulkanResidentDrawTemplate replacement = new(
            structuralIdentity,
            variant,
            generations,
            nativeState,
            dependencyLease);
        variants[targetVariant] = replacement;
        slot.EntryGenerations[targetVariant] = NextGeneration(
            slot.EntryGenerations[targetVariant]);
        if (existing is null)
        {
            ++_residentCount;
            Interlocked.Increment(ref _creates);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                created: true);
        }
        else
        {
            existing.Detach();
            Interlocked.Increment(ref _replacements);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                replaced: true);
        }

        template = replacement;
        handle = CreateHandle(in primary, slot, targetVariant);
        return true;
    }

    internal bool TryEvict(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw,
        in VulkanResidentDrawTemplateVariantKey variant)
    {
        VerifyOwnerThread();
        AdvancedGpuSceneDrawIdentity primary = canonicalDraw.Primary;
        if (!TryGetMatchingPrimarySlot(in primary, out PrimarySlot slot))
            return false;

        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        const int index = 0;
        VulkanResidentDrawTemplate? candidate = variants[index];
        if (candidate is not null && candidate.Variant == variant)
        {
            candidate.Detach();
            variants[index] = null;
            slot.EntryGenerations[index] = NextGeneration(
                slot.EntryGenerations[index]);
            --_residentCount;
            Interlocked.Increment(ref _evictions);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                evicted: true);
            slot.ClearIdentity();
            return true;
        }

        return false;
    }

    /// <summary>Explicit boundary-only growth. Existing template leases remain owned.</summary>
    internal bool GrowAtBoundary(uint requiredPrimaryCapacity)
    {
        VerifyOwnerThread();
        if (requiredPrimaryCapacity <= PrimaryCapacity)
            return true;
        if (requiredPrimaryCapacity >= int.MaxValue)
            return false;

        PrimarySlot[] grown = CreateSlots(
            checked((int)requiredPrimaryCapacity + 1),
            _variantsPerDraw);
        Array.Copy(_primarySlots, grown, _primarySlots.Length);
        _primarySlots = grown;
        return true;
    }

    /// <summary>
    /// Applies the package's draw-owner invalidation journal directly to
    /// canonical primary slots. Until Phase 4 supplies reverse dependency
    /// manifests, a structural non-draw mutation conservatively clears the
    /// table so an offscreen template cannot retain stale native ownership.
    /// Data-only owner changes advance independent generations on lookup.
    /// </summary>
    internal void ApplyProjectionDeltas(
        ulong databaseEpoch,
        ulong publicationSequence,
        ReadOnlySpan<BackendTemplateProjectionDelta> deltas)
    {
        VerifyOwnerThread();
        if (databaseEpoch == 0u || publicationSequence == 0u)
            return;
        if (_lastProjectionDatabaseEpoch == databaseEpoch &&
            publicationSequence <= _lastProjectionSequence)
            return;

        bool clearForStructuralOwnerMutation = false;
        for (int index = 0; index < deltas.Length; ++index)
        {
            ref readonly BackendTemplateProjectionDelta delta = ref deltas[index];
            if (delta.PublicationGeneration != publicationSequence)
                continue;

            if (delta.Owner != EBackendReadyCanonicalOwner.Draw)
            {
                if (delta.Kind != EBackendTemplateProjectionDeltaKind.Add &&
                    delta.Domain != EBackendTemplateMutationDomain.DataContent)
                    clearForStructuralOwnerMutation = true;
                continue;
            }
            if (delta.Kind == EBackendTemplateProjectionDeltaKind.Add)
                continue;

            EvictPrimary(databaseEpoch, delta.Handle);
            if (delta.PreviousHandle.IsValid &&
                delta.PreviousHandle != delta.Handle)
                EvictPrimary(databaseEpoch, delta.PreviousHandle);
        }

        if (clearForStructuralOwnerMutation)
            Clear();

        _lastProjectionDatabaseEpoch = databaseEpoch;
        _lastProjectionSequence = publicationSequence;
    }

    internal void Clear()
    {
        VerifyOwnerThread();
        for (int index = 1; index < _primarySlots.Length; ++index)
            EvictSlot(_primarySlots[index]);
    }

    public void Dispose() => Clear();

    private bool TryGetMatchingPrimarySlot(
        in AdvancedGpuSceneDrawIdentity primary,
        out PrimarySlot slot)
    {
        slot = null!;
        if (!primary.IsValid || primary.Handle.Index >= (uint)_primarySlots.Length)
            return false;

        PrimarySlot candidate = _primarySlots[checked((int)primary.Handle.Index)];
        if (!candidate.Matches(in primary))
            return false;

        slot = candidate;
        return true;
    }

    private static VulkanResidentDrawTemplateHandle CreateHandle(
        in AdvancedGpuSceneDrawIdentity primary,
        PrimarySlot slot,
        int variantOrdinal)
        => new(
            primary.Handle.Index,
            primary.Handle.Generation,
            primary.DatabaseEpoch,
            checked((ushort)variantOrdinal),
            slot.EntryGenerations[variantOrdinal]);

    private void EvictSlot(PrimarySlot slot)
    {
        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        for (int index = 0; index < variants.Length; ++index)
        {
            VulkanResidentDrawTemplate? template = variants[index];
            if (template is null)
                continue;

            template.Detach();
            variants[index] = null;
            slot.EntryGenerations[index] = NextGeneration(
                slot.EntryGenerations[index]);
            --_residentCount;
            Interlocked.Increment(ref _evictions);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                evicted: true);
        }
        slot.ClearIdentity();
    }

    private void EvictPrimary(ulong databaseEpoch, AdvancedGpuHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)_primarySlots.Length)
            return;

        PrimarySlot slot = _primarySlots[checked((int)handle.Index)];
        if (slot.DatabaseEpoch != databaseEpoch ||
            slot.HandleGeneration != handle.Generation)
            return;
        EvictSlot(slot);
    }

    private static PrimarySlot[] CreateSlots(int length, int variantsPerDraw)
    {
        PrimarySlot[] slots = new PrimarySlot[length];
        for (int index = 0; index < slots.Length; ++index)
            slots[index] = new PrimarySlot(variantsPerDraw);
        return slots;
    }

    private bool RecordMiss()
    {
        Interlocked.Increment(ref _misses);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
            miss: true);
        return false;
    }

    private static uint NextGeneration(uint generation)
    {
        unchecked
        {
            generation++;
        }
        return generation == 0u ? 1u : generation;
    }

    private void VerifyOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException(
                "Vulkan resident draw templates are owned by their render thread.");
    }
}

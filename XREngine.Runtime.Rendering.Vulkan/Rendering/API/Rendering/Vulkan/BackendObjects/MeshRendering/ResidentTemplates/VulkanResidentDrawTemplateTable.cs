using System.Threading;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Render-thread-owned, bounded resident-template table. Primary lookup is an
/// array access by canonical handle index. Each canonical draw owns a bounded
/// set of independently-addressed sealed variants.
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
    private long _exactDependencyInvalidations;
    private long _broadFallbackInvalidations;
    private long _broadFallbackEntries;
    private readonly VulkanResidentDrawTemplateHandle[][] _reverseDependencyHeads;
    private readonly VulkanStableBinMembership _stableBinMembership;
    private readonly VulkanStableBinManifestCache _stableBinManifestCache = new();
    private VulkanResidentDrawTemplateHandle[] _nativeDependencySlots;
    private ulong _lastProjectionDatabaseEpoch;
    private ulong _lastProjectionSequence;
    private VulkanResidentTemplateBroadInvalidationRecord _lastBroadInvalidation;

    internal VulkanResidentDrawTemplateTable(
        VulkanResourceRuntime resourceRuntime,
        uint primaryCapacity,
        uint variantsPerDraw)
    {
        ArgumentNullException.ThrowIfNull(resourceRuntime);
        if (primaryCapacity == 0u || primaryCapacity >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(primaryCapacity));
        if (variantsPerDraw == 0u || variantsPerDraw > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(variantsPerDraw));

        _resourceRuntime = resourceRuntime;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _variantsPerDraw = checked((int)variantsPerDraw);
        _primarySlots = CreateSlots(checked((int)primaryCapacity + 1), _variantsPerDraw);
        _nativeDependencySlots = new VulkanResidentDrawTemplateHandle[
            checked((int)(primaryCapacity * variantsPerDraw) + 1)];
        _stableBinMembership = new VulkanStableBinMembership(
            primaryCapacity,
            _variantsPerDraw);
        _reverseDependencyHeads = new VulkanResidentDrawTemplateHandle[(int)EBackendReadyCanonicalOwner.Probe + 1][];
        for (int index = 0; index < _reverseDependencyHeads.Length; ++index)
            _reverseDependencyHeads[index] = new VulkanResidentDrawTemplateHandle[1];
    }

    internal uint PrimaryCapacity => checked((uint)_primarySlots.Length - 1u);
    internal int VariantsPerDraw => _variantsPerDraw;
    internal int ResidentCount => _residentCount;
    internal VulkanStableBinMembership StableBinMembership => _stableBinMembership;
    internal VulkanStableBinManifestCache StableBinManifestCache => _stableBinManifestCache;
    internal VulkanResidentTemplateBroadInvalidationRecord LastBroadInvalidation
        => _lastBroadInvalidation;

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
            _residentCount,
            unchecked((ulong)Volatile.Read(ref _exactDependencyInvalidations)),
            unchecked((ulong)Volatile.Read(ref _broadFallbackInvalidations)),
            unchecked((ulong)Volatile.Read(ref _broadFallbackEntries)));

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

        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        if (TryFindVariantOrdinal(slot, in variant, out int index))
        {
            VulkanResidentDrawTemplate candidate = variants[index]!;
            if (!candidate.Generations.IsStructurallyCompatibleWith(in generations))
                return RecordMiss();
            if (!_resourceRuntime.IsResidentTemplateDependencyLeaseCurrent(
                    candidate.DependencyLease))
            {
                _stableBinMembership.Remove(CreateHandle(in primary, slot, index));
                UnlinkReverseDependencies(candidate);
                RetireNativeDependencies(candidate);
                candidate.Dispose();
                variants[index] = null;
                slot.EntryGenerations[index] = NextGeneration(
                    slot.EntryGenerations[index]);
                --_residentCount;
                if (!HasAnyVariant(slot))
                    slot.ClearIdentity();
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
    /// Resolves a generation-checked retained address for sealed bin preparation.
    /// It does not acquire a use: the prepared-frame lifetime transfer remains
    /// the sole owner of frame-slot retention.
    /// </summary>
    internal bool TryGetLive(
        in VulkanResidentDrawTemplateHandle handle,
        out VulkanResidentDrawTemplate? template)
    {
        VerifyOwnerThread();
        template = null;
        if (!handle.IsValid || handle.PrimaryIndex >= (uint)_primarySlots.Length)
            return false;

        PrimarySlot slot = _primarySlots[checked((int)handle.PrimaryIndex)];
        int variantOrdinal = handle.VariantOrdinal;
        if (slot.DatabaseEpoch != handle.DatabaseEpoch ||
            slot.HandleGeneration != handle.CanonicalHandleGeneration ||
            (uint)variantOrdinal >= (uint)slot.Variants.Length ||
            slot.EntryGenerations[variantOrdinal] != handle.EntryGeneration ||
            slot.Variants[variantOrdinal] is not { } candidate ||
            !_resourceRuntime.IsResidentTemplateDependencyLeaseCurrent(
                candidate.DependencyLease))
        {
            return false;
        }

        template = candidate;
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
        if (!VulkanResidentDrawDependencyManifest.TryCreate(
                structuralIdentity.CanonicalDraw,
                out VulkanResidentDrawDependencyManifest? dependencyManifest) ||
            dependencyManifest is null)
        {
            dependencyLease.Dispose();
            Interlocked.Increment(ref _dependencyRejects);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                dependencyRejected: true);
            return false;
        }
        EnsureReverseDependencyCapacity(dependencyManifest);

        PrimarySlot slot = _primarySlots[checked((int)primary.Handle.Index)];
        if (slot.DatabaseEpoch != 0u && !slot.Matches(in primary))
            EvictSlot(slot);
        if (slot.DatabaseEpoch == 0u)
            slot.SetIdentity(in primary);

        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        int targetVariant = FindExistingOrVacantVariantOrdinal(slot, in variant);
        if (targetVariant < 0)
        {
            dependencyLease.Dispose();
            Interlocked.Increment(ref _capacityFailures);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(capacityFailure: true);
            return false;
        }

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
            dependencyManifest,
            dependencyLease);
        if (!CanRegisterNativeDependencies(replacement))
        {
            replacement.Dispose();
            Interlocked.Increment(ref _dependencyRejects);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                dependencyRejected: true);
            return false;
        }
        VulkanResidentDrawTemplateHandle replacementHandle = new(
            primary.Handle.Index,
            primary.Handle.Generation,
            primary.DatabaseEpoch,
            checked((ushort)targetVariant),
            NextGeneration(slot.EntryGenerations[targetVariant]));
        if (replacement.IsStableBinEligible &&
            !_stableBinMembership.TryUpdateTopology(
                replacementHandle,
                replacement.RenderBinKey,
                out _))
        {
            replacement.Dispose();
            Interlocked.Increment(ref _capacityFailures);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                capacityFailure: true);
            return false;
        }
        if (!replacement.IsStableBinEligible && existing is not null)
            _stableBinMembership.Remove(CreateHandle(in primary, slot, targetVariant));
        if (existing is not null)
        {
            UnlinkReverseDependencies(existing);
            RetireNativeDependencies(existing);
        }
        variants[targetVariant] = replacement;
        slot.EntryGenerations[targetVariant] = NextGeneration(
            slot.EntryGenerations[targetVariant]);
        VulkanResidentDrawTemplateHandle publishedHandle = CreateHandle(in primary, slot, targetVariant);
        LinkReverseDependencies(in publishedHandle, replacement);
        replacement.NativeDependencyIdentity = CreateNativeDependencyIdentity(publishedHandle);
        RegisterNativeDependencies(replacement, in publishedHandle);
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
        handle = publishedHandle;
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
        if (TryFindVariantOrdinal(slot, in variant, out int index))
        {
            VulkanResidentDrawTemplate candidate = variants[index]!;
            _stableBinMembership.Remove(CreateHandle(in primary, slot, index));
            UnlinkReverseDependencies(candidate);
            RetireNativeDependencies(candidate);
            candidate.Detach();
            variants[index] = null;
            slot.EntryGenerations[index] = NextGeneration(
                slot.EntryGenerations[index]);
            --_residentCount;
            Interlocked.Increment(ref _evictions);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(
                evicted: true);
            if (!HasAnyVariant(slot))
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
        Array.Resize(
            ref _nativeDependencySlots,
            checked((int)(requiredPrimaryCapacity * (uint)_variantsPerDraw) + 1));
        return _stableBinMembership.GrowAtBoundary(requiredPrimaryCapacity);
    }

    /// <summary>
    /// Applies the package's exact mutation journal through compact reverse
    /// dependency arrays. Data-only changes leave resident structure warm.
    /// </summary>
    internal void ApplyProjectionDeltas(
        ulong databaseEpoch,
        ulong publicationSequence,
        ReadOnlySpan<BackendTemplateProjectionDelta> deltas)
    {
        VerifyOwnerThread();
        DrainNativeDependencyInvalidations();
        if (databaseEpoch == 0u || publicationSequence == 0u)
            return;
        if (_lastProjectionDatabaseEpoch == databaseEpoch &&
            publicationSequence <= _lastProjectionSequence)
            return;

        for (int index = 0; index < deltas.Length; ++index)
        {
            ref readonly BackendTemplateProjectionDelta delta = ref deltas[index];
            if (delta.PublicationGeneration != publicationSequence)
                continue;

            if (delta.Owner != EBackendReadyCanonicalOwner.Draw)
            {
                if (delta.Kind == EBackendTemplateProjectionDeltaKind.Add ||
                    delta.Domain == EBackendTemplateMutationDomain.DataContent)
                {
                    continue;
                }

                if (!TryEvictReverseDependents(
                        delta.Owner,
                        delta.Handle,
                        out int invalidated))
                {
                    RecordBroadFallback(
                        $"MissingOrInconsistentReverseManifest:{delta.Owner}:slot={delta.Handle.Index}:generation={delta.Handle.Generation}:domain={delta.Domain}",
                        delta.Owner,
                        delta.Domain,
                        publicationSequence);
                    break;
                }

                if (invalidated > 0)
                {
                    Interlocked.Add(ref _exactDependencyInvalidations, invalidated);
                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanResidentTemplateExactDependencyInvalidation(
                            invalidated);
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExactResourceInvalidation(
                        invalidated,
                        0,
                        _residentCount,
                        0);
                }
                continue;
            }
            if (delta.Kind == EBackendTemplateProjectionDeltaKind.Add)
                continue;

            EvictPrimary(databaseEpoch, delta.Handle);
            if (delta.PreviousHandle.IsValid &&
                delta.PreviousHandle != delta.Handle)
                EvictPrimary(databaseEpoch, delta.PreviousHandle);
        }

        _lastProjectionDatabaseEpoch = databaseEpoch;
        _lastProjectionSequence = publicationSequence;
    }

    internal void Clear()
    {
        VerifyOwnerThread();
        for (int index = 1; index < _primarySlots.Length; ++index)
            EvictSlot(_primarySlots[index]);
        ClearReverseDependencyHeads();
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

    private static bool TryFindVariantOrdinal(
        PrimarySlot slot,
        in VulkanResidentDrawTemplateVariantKey variant,
        out int ordinal)
    {
        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        for (int index = 0; index < variants.Length; ++index)
        {
            if (variants[index] is { } candidate && candidate.Variant == variant)
            {
                ordinal = index;
                return true;
            }
        }
        ordinal = -1;
        return false;
    }

    private static int FindExistingOrVacantVariantOrdinal(
        PrimarySlot slot,
        in VulkanResidentDrawTemplateVariantKey variant)
    {
        int vacant = -1;
        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        for (int index = 0; index < variants.Length; ++index)
        {
            VulkanResidentDrawTemplate? candidate = variants[index];
            if (candidate is not null && candidate.Variant == variant)
                return index;
            if (candidate is null && vacant < 0)
                vacant = index;
        }
        return vacant;
    }

    private static bool TryFindTemplateOrdinal(
        PrimarySlot slot,
        VulkanResidentDrawTemplate template,
        out int ordinal)
    {
        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        for (int index = 0; index < variants.Length; ++index)
        {
            if (ReferenceEquals(variants[index], template))
            {
                ordinal = index;
                return true;
            }
        }
        ordinal = -1;
        return false;
    }

    private static bool HasAnyVariant(PrimarySlot slot)
    {
        foreach (VulkanResidentDrawTemplate? variant in slot.Variants)
            if (variant is not null)
                return true;
        return false;
    }

    private ulong CreateNativeDependencyIdentity(
        in VulkanResidentDrawTemplateHandle handle)
        // Live primary/variant coordinates are injective within this table.
        // Graph generations distinguish retirement/republication of the same
        // coordinate, so correctness never depends on a hash collision.
        => checked(
            1UL +
            (ulong)handle.PrimaryIndex * (uint)_variantsPerDraw +
            handle.VariantOrdinal);

    private bool CanRegisterNativeDependencies(
        VulkanResidentDrawTemplate template)
    {
        VulkanNativeDependencyGraph graph = _resourceRuntime.NativeDependencies;
        if (!TryResolve(
                EVulkanNativeDependencyOwner.PipelineLayout,
                template.NativeState.PipelineLayout.Handle))
        {
            return false;
        }
        for (int primitiveIndex = 0;
             primitiveIndex < template.NativeState.PrimitiveCount;
             ++primitiveIndex)
        {
            if (!TryResolve(
                    EVulkanNativeDependencyOwner.Pipeline,
                    template.NativeState.GetPrimitive(primitiveIndex).Pipeline.Handle))
            {
                return false;
            }
        }
        return TryResolve(EVulkanNativeDependencyOwner.DescriptorTable, 1UL);

        bool TryResolve(EVulkanNativeDependencyOwner owner, ulong nativeHandle)
            => nativeHandle != 0 && graph.TryGet(owner, nativeHandle, out _);
    }

    private void RegisterNativeDependencies(
        VulkanResidentDrawTemplate template,
        in VulkanResidentDrawTemplateHandle publishedHandle)
    {
        VulkanNativeDependencyGraph graph = _resourceRuntime.NativeDependencies;
        VulkanNativeDependencyHandle dependent = graph.Register(
            EVulkanNativeDependencyOwner.ResidentVariant,
            template.NativeDependencyIdentity);
        if (!dependent.IsValid)
            throw new InvalidOperationException(
                "A preflighted resident variant could not register its native dependency identity.");
        EnsureNativeDependencySlotCapacity(dependent.Slot);
        template.NativeDependencyHandle = dependent;
        _nativeDependencySlots[dependent.Slot] = publishedHandle;
        LinkNativeDependencyRequired(EVulkanNativeDependencyOwner.PipelineLayout, template.NativeState.PipelineLayout.Handle, dependent);
        for (int primitiveIndex = 0; primitiveIndex < template.NativeState.PrimitiveCount; ++primitiveIndex)
            LinkNativeDependencyRequired(
                EVulkanNativeDependencyOwner.Pipeline,
                template.NativeState.GetPrimitive(primitiveIndex).Pipeline.Handle,
                dependent);
        LinkNativeDependencyRequired(EVulkanNativeDependencyOwner.DescriptorTable, 1UL, dependent);
    }

    private void RetireNativeDependencies(VulkanResidentDrawTemplate template)
    {
        if (template.NativeDependencyIdentity != 0)
        {
            if (template.NativeDependencyHandle.IsValid &&
                template.NativeDependencyHandle.Slot < (uint)_nativeDependencySlots.Length)
                _nativeDependencySlots[template.NativeDependencyHandle.Slot] = default;
            _ = _resourceRuntime.NativeDependencies.Retire(
                EVulkanNativeDependencyOwner.ResidentVariant,
                template.NativeDependencyIdentity,
                "ResidentVariant.Eviction");
            template.NativeDependencyHandle = default;
        }
    }

    private void EnsureNativeDependencySlotCapacity(uint requiredSlot)
    {
        if (requiredSlot < (uint)_nativeDependencySlots.Length)
            return;

        int required = checked((int)requiredSlot + 1);
        int capacity = Math.Max(required, checked(_nativeDependencySlots.Length * 2));
        Array.Resize(ref _nativeDependencySlots, capacity);
    }

    private void LinkNativeDependencyRequired(
        EVulkanNativeDependencyOwner owner,
        ulong nativeHandle,
        VulkanNativeDependencyHandle dependent)
    {
        VulkanNativeDependencyGraph graph = _resourceRuntime.NativeDependencies;
        if (nativeHandle == 0 ||
            !graph.TryGet(owner, nativeHandle, out VulkanNativeDependencyHandle source) ||
            !graph.Link(
                owner,
                source,
                EVulkanNativeDependencyOwner.ResidentVariant,
                dependent))
        {
            throw new InvalidOperationException(
                $"A preflighted resident variant lost its required {owner} native dependency before publication.");
        }
    }

    private void DrainNativeDependencyInvalidations()
    {
        VulkanNativeDependencyGraph graph = _resourceRuntime.NativeDependencies;
        while (graph.TryDequeueDirtyRecord(
                   EVulkanNativeDependencyOwner.ResidentVariant,
                   out VulkanNativeDependencyInvalidationRecord record))
        {
            if (record.DependentOwner != EVulkanNativeDependencyOwner.ResidentVariant ||
                record.Dependent.Slot >= (uint)_nativeDependencySlots.Length)
                continue;

            VulkanResidentDrawTemplateHandle handle = _nativeDependencySlots[record.Dependent.Slot];
            if (!handle.IsValid || handle.PrimaryIndex >= (uint)_primarySlots.Length)
                continue;

            PrimarySlot slot = _primarySlots[checked((int)handle.PrimaryIndex)];
            if (handle.VariantOrdinal >= slot.Variants.Length ||
                slot.EntryGenerations[handle.VariantOrdinal] != handle.EntryGeneration)
                continue;

            VulkanResidentDrawTemplate? template = slot.Variants[handle.VariantOrdinal];
            if (template is null || template.NativeDependencyHandle != record.Dependent)
                continue;

            EvictVariant(slot, handle.VariantOrdinal);
            Interlocked.Increment(ref _exactDependencyInvalidations);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentTemplateExactDependencyInvalidation(1);
        }
    }

    private void EvictSlot(PrimarySlot slot)
    {
        VulkanResidentDrawTemplate?[] variants = slot.Variants;
        for (int index = 0; index < variants.Length; ++index)
        {
            VulkanResidentDrawTemplate? template = variants[index];
            if (template is null)
                continue;

            UnlinkReverseDependencies(template);
            RetireNativeDependencies(template);
            AdvancedGpuSceneDrawIdentity primary =
                template.StructuralIdentity.CanonicalDraw.Primary;
            VulkanResidentDrawTemplateHandle handle = new(
                primary.Handle.Index,
                primary.Handle.Generation,
                primary.DatabaseEpoch,
                checked((ushort)index),
                slot.EntryGenerations[index]);
            _stableBinMembership.Remove(handle);
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

    private void EvictVariant(PrimarySlot slot, int index)
    {
        if ((uint)index >= (uint)slot.Variants.Length || slot.Variants[index] is not { } template)
            return;

        UnlinkReverseDependencies(template);
        RetireNativeDependencies(template);
        AdvancedGpuSceneDrawIdentity primary = template.StructuralIdentity.CanonicalDraw.Primary;
        _stableBinMembership.Remove(CreateHandle(in primary, slot, index));
        template.Detach();
        slot.Variants[index] = null;
        slot.EntryGenerations[index] = NextGeneration(slot.EntryGenerations[index]);
        --_residentCount;
        Interlocked.Increment(ref _evictions);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentDrawTemplate(evicted: true);
        if (!HasAnyVariant(slot))
            slot.ClearIdentity();
    }

    private void EnsureReverseDependencyCapacity(
        VulkanResidentDrawDependencyManifest manifest)
    {
        ReadOnlySpan<VulkanResidentDrawDependency> dependencies =
            manifest.CanonicalDependencies;
        for (int index = 0; index < dependencies.Length; ++index)
        {
            VulkanResidentDrawDependency dependency = dependencies[index];
            int owner = (int)dependency.Owner;
            if ((uint)owner >= (uint)_reverseDependencyHeads.Length)
                throw new InvalidOperationException("Unsupported resident reverse-dependency owner.");

            int required = checked((int)dependency.Handle.Index + 1);
            VulkanResidentDrawTemplateHandle[] heads = _reverseDependencyHeads[owner];
            if (heads.Length >= required)
                continue;

            int capacity = heads.Length;
            while (capacity < required)
                capacity = checked(capacity * 2);
            Array.Resize(ref _reverseDependencyHeads[owner], capacity);
        }
    }

    private void LinkReverseDependencies(
        in VulkanResidentDrawTemplateHandle handle,
        VulkanResidentDrawTemplate template)
    {
        ReadOnlySpan<VulkanResidentDrawDependency> dependencies =
            template.DependencyManifest.CanonicalDependencies;
        Span<VulkanResidentDrawDependencyLink> links =
            template.DependencyManifest.ReverseLinks;
        for (int index = 0; index < dependencies.Length; ++index)
        {
            VulkanResidentDrawDependency dependency = dependencies[index];
            VulkanResidentDrawTemplateHandle[] heads = _reverseDependencyHeads[(int)dependency.Owner];
            int dependencyIndex = checked((int)dependency.Handle.Index);
            VulkanResidentDrawTemplateHandle previousHead = heads[dependencyIndex];
            links[index] = new VulkanResidentDrawDependencyLink
            {
                Next = previousHead,
                IsLinked = true,
            };
            if (previousHead.IsValid &&
                TryGetReverseLink(
                    in previousHead,
                    dependency.Owner,
                    dependency.Handle.Index,
                    out VulkanResidentDrawDependencyManifest? headManifest,
                    out int headLinkIndex))
            {
                headManifest!.ReverseLinks[headLinkIndex].Previous = handle;
            }
            heads[dependencyIndex] = handle;
        }
    }

    private void UnlinkReverseDependencies(VulkanResidentDrawTemplate template)
    {
        ReadOnlySpan<VulkanResidentDrawDependency> dependencies =
            template.DependencyManifest.CanonicalDependencies;
        Span<VulkanResidentDrawDependencyLink> links =
            template.DependencyManifest.ReverseLinks;
        for (int index = 0; index < dependencies.Length; ++index)
        {
            ref VulkanResidentDrawDependencyLink link = ref links[index];
            if (!link.IsLinked)
                continue;

            VulkanResidentDrawDependency dependency = dependencies[index];
            int owner = (int)dependency.Owner;
            int dependencyIndex = checked((int)dependency.Handle.Index);
            VulkanResidentDrawTemplateHandle[] heads = _reverseDependencyHeads[owner];
            if (!link.Previous.IsValid)
            {
                heads[dependencyIndex] = link.Next;
            }
            else if (TryGetReverseLink(
                         in link.Previous,
                         dependency.Owner,
                         dependency.Handle.Index,
                         out VulkanResidentDrawDependencyManifest? previousManifest,
                         out int previousLinkIndex))
            {
                previousManifest!.ReverseLinks[previousLinkIndex].Next = link.Next;
            }

            if (link.Next.IsValid &&
                TryGetReverseLink(
                    in link.Next,
                    dependency.Owner,
                    dependency.Handle.Index,
                    out VulkanResidentDrawDependencyManifest? nextManifest,
                    out int nextLinkIndex))
            {
                nextManifest!.ReverseLinks[nextLinkIndex].Previous = link.Previous;
            }
            link = default;
        }
    }

    private bool TryEvictReverseDependents(
        EBackendReadyCanonicalOwner owner,
        AdvancedGpuHandle handle,
        out int invalidated)
    {
        invalidated = 0;
        int ownerIndex = (int)owner;
        if (!handle.IsValid ||
            (uint)ownerIndex >= (uint)_reverseDependencyHeads.Length)
        {
            return false;
        }

        VulkanResidentDrawTemplateHandle[] heads = _reverseDependencyHeads[ownerIndex];
        if (handle.Index >= (uint)heads.Length)
            return true;

        VulkanResidentDrawTemplateHandle dependent = heads[checked((int)handle.Index)];
        int traversalBudget = _residentCount + 1;
        VulkanResidentDrawTemplateHandle expectedPrevious = default;
        while (dependent.IsValid)
        {
            if (--traversalBudget < 0 ||
                !TryGetReverseLink(
                    in dependent,
                    owner,
                    handle.Index,
                    out VulkanResidentDrawDependencyManifest? manifest,
                    out int linkIndex))
            {
                return false;
            }

            VulkanResidentDrawDependencyLink link = manifest!.ReverseLinks[linkIndex];
            if (link.Previous != expectedPrevious)
                return false;
            VulkanResidentDrawTemplateHandle next = link.Next;
            if (next.IsValid &&
                (!TryGetReverseLink(
                    in next,
                    owner,
                    handle.Index,
                    out VulkanResidentDrawDependencyManifest? nextManifest,
                    out int nextLinkIndex) ||
                 nextManifest!.ReverseLinks[nextLinkIndex].Previous != dependent))
            {
                return false;
            }
            VulkanResidentDrawDependency dependency =
                manifest.CanonicalDependencies[linkIndex];
            if (dependency.Handle == handle)
            {
                EvictVariant(
                    _primarySlots[checked((int)dependent.PrimaryIndex)],
                    dependent.VariantOrdinal);
                ++invalidated;
            }
            else
            {
                expectedPrevious = dependent;
            }
            dependent = next;
        }
        return true;
    }

    private bool TryGetReverseLink(
        in VulkanResidentDrawTemplateHandle handle,
        EBackendReadyCanonicalOwner owner,
        uint dependencyIndex,
        out VulkanResidentDrawDependencyManifest? manifest,
        out int linkIndex)
    {
        manifest = null;
        linkIndex = -1;
        if (!handle.IsValid || handle.PrimaryIndex >= (uint)_primarySlots.Length)
        {
            return false;
        }

        PrimarySlot slot = _primarySlots[checked((int)handle.PrimaryIndex)];
        if (slot.DatabaseEpoch != handle.DatabaseEpoch ||
            slot.HandleGeneration != handle.CanonicalHandleGeneration ||
            handle.VariantOrdinal >= slot.Variants.Length ||
            slot.EntryGenerations[handle.VariantOrdinal] != handle.EntryGeneration ||
            slot.Variants[handle.VariantOrdinal] is not { } template)
            return false;

        VulkanResidentDrawDependencyManifest candidate =
            template.DependencyManifest;
        ReadOnlySpan<VulkanResidentDrawDependency> dependencies =
            candidate.CanonicalDependencies;
        for (int index = 0; index < dependencies.Length; ++index)
        {
            if (dependencies[index].Owner != owner ||
                dependencies[index].Handle.Index != dependencyIndex ||
                !candidate.ReverseLinks[index].IsLinked)
            {
                continue;
            }

            manifest = candidate;
            linkIndex = index;
            return true;
        }
        return false;
    }

    private void RecordBroadFallback(
        string reason,
        EBackendReadyCanonicalOwner owner,
        EBackendTemplateMutationDomain domain,
        ulong publicationSequence)
    {
        int affected = _residentCount;
        _lastBroadInvalidation = new VulkanResidentTemplateBroadInvalidationRecord(
            reason,
            owner,
            domain,
            affected,
            publicationSequence);
        Interlocked.Increment(ref _broadFallbackInvalidations);
        Interlocked.Add(ref _broadFallbackEntries, affected);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResidentTemplateBroadFallback(
            affected);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExactResourceInvalidation(
            0,
            0,
            0,
            1);
        Clear();
    }

    private void ClearReverseDependencyHeads()
    {
        for (int owner = 0; owner < _reverseDependencyHeads.Length; ++owner)
            Array.Clear(_reverseDependencyHeads[owner]);
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

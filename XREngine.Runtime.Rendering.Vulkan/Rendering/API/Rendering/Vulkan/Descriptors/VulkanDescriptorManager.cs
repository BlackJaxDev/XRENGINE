using System.Collections.Concurrent;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns descriptor caches and allocation bookkeeping for one Vulkan logical-device lifetime.
/// Native creation and retirement remain renderer operations so they continue to use the
/// renderer's lifetime ledger and device-loss policy.
/// </summary>
internal sealed unsafe partial class VulkanDescriptorManager
{
    internal const uint GlobalsSetIndex = 0;
    internal const uint ComputeSetIndex = 1;
    internal const uint MaterialSetIndex = 2;
    internal const uint PerPassSetIndex = 3;
    internal const uint SetTierCount = 4;
    private Func<uint, DescriptorSetLayoutBinding[], (bool Success, DescriptorSetLayout Layout, bool UsesUpdateAfterBind, bool UsesVariableDescriptorCount)>? _acquireLayout;
    private Action<DescriptorSetLayout>? _releaseLayout;
    internal readonly object _descriptorSetLayoutCacheLock = new();
    internal readonly Dictionary<ulong, List<CachedDescriptorSetLayout>> _descriptorSetLayoutsByHash = new();
    internal readonly Dictionary<ulong, CachedDescriptorSetLayout> _descriptorSetLayoutsByHandle = new();
    internal readonly object _descriptorUpdateTemplateCacheLock = new();
    internal readonly Dictionary<ulong, List<CachedDescriptorUpdateTemplate>> _descriptorUpdateTemplateCache = new();
    private readonly object _sharedMeshDescriptorAllocationLock = new();
    private readonly Dictionary<
        VkMeshRenderer.DescriptorAllocationKey,
        List<VkMeshRenderer.DescriptorAllocation>> _sharedMeshDescriptorAllocations = [];
    private long _descriptorSetContentUpdateGeneration;
    private int _descriptorUpdateInvalidationDiagnosticCount;
    private int _meshOwnershipDiagnosticCount;
    // Detached cache pools are intentionally not reinserted after a failed
    // handoff: reusing them would republish superseded descriptor bindings.
    // Keep their normal lifetime-retirement handoff durable until it succeeds.
    private readonly Dictionary<ulong, DescriptorPool>
        _pendingSupersededComputePoolRetirements = [];
    private int _frameSlotCount = 2;
    private VulkanBackendObjectContext? _backendContext;
    private VulkanWrapperLookupPort? _wrapperLookup;
    private VulkanFrameTelemetry? _frameTelemetry;
    private int _desktopFrameSlot;

    private VulkanBackendObjectContext BackendContext
        => _backendContext ?? throw new InvalidOperationException("The descriptor manager has no published backend-object context.");
    private Vk Api => BackendContext.Api;
    private VulkanDeviceContext DeviceContext => BackendContext.DeviceContext;
    private VulkanFrameTelemetry FrameTelemetry
        => _frameTelemetry ?? throw new InvalidOperationException("The descriptor manager has no published frame telemetry.");
    private VulkanResourceRuntime ResourceRuntime => BackendContext.Resources;
    private VulkanWrapperLookupPort WrapperLookup
        => _wrapperLookup ?? throw new InvalidOperationException("The descriptor manager has no published Vulkan wrapper lookup port.");
    private int CurrentDesktopFrameSlot => Volatile.Read(ref _desktopFrameSlot);
    private bool AllowSynchronousResourceUploads => BackendContext.Resources.AllowSynchronousResourceUploads;

    private T? GenericToAPI<T>(GenericRenderObject data)
        where T : class
        => WrapperLookup.GetOrCreate(data) as T;

    private void RecordVulkanDescriptorTableGeneration(string _) => ResourceRuntime.DescriptorLifetime.RecordTableGeneration();
    private void SetDebugDescriptorSetName(DescriptorSet set, string name) => ResourceRuntime.DescriptorLifetime.SetDebugName(set, name);
    private void RetireDescriptorPool(DescriptorPool pool) => ResourceRuntime.DescriptorLifetime.RetireDescriptorPool(pool);

    private (Buffer Buffer, DeviceMemory Memory) CreateDedicatedBufferRaw(
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        bool enableDeviceAddress = false)
        => BackendContext.Resources.Buffers.CreateRaw(
            BackendContext,
            size,
            usage,
            properties,
            enableDeviceAddress,
            "DescriptorHeap");

    private void DestroyBuffer(Buffer buffer, DeviceMemory memory)
        => BackendContext.Resources.Buffers.DestroyUnpublished(BackendContext, buffer, memory);
    private ulong GetBufferDeviceAddress(Buffer buffer)
        => BackendContext.Resources.Buffers.GetDeviceAddress(BackendContext, buffer);

    internal Sampler[] CanonicalImmutableSamplers { get; } = new Sampler[5];

    internal bool TryGetCanonicalImmutableSampler(VulkanCanonicalSampler sampler, out Sampler handle)
    {
        int index = (int)sampler;
        handle = (uint)index < (uint)CanonicalImmutableSamplers.Length
            ? CanonicalImmutableSamplers[index]
            : default;
        return handle.Handle != 0;
    }
    internal ConcurrentDictionary<ulong, string> LiveDescriptorSetLayoutHandles { get; } = new();
    internal object MeshDescriptorPoolSlabLock { get; } = new();
    internal object SamplerLifetimeLock { get; } = new();
    internal HashSet<ulong> LiveSamplerHandles { get; } = [];
    internal Dictionary<ulong, SamplerCreateInfo> DescriptorHeapSamplerCreateInfos { get; } = [];
    internal ConcurrentDictionary<ulong, BufferViewCreateInfo> DescriptorHeapBufferViewCreateInfos { get; } = new();
    internal Dictionary<
        MeshDescriptorPoolSlabKey,
        List<MeshDescriptorPoolSlab>> MeshDescriptorPoolSlabs { get; } = [];
    internal VulkanBindlessMaterialTextureTableState BindlessMaterialTextures { get; } = new();
    internal VulkanComputeDescriptorCacheState Compute { get; } = new();
    internal VulkanDescriptorHeapState Heap { get; } = new();
    internal DescriptorSet[]? RootSets;
    internal DescriptorPool RootPool;
    internal DescriptorSetLayout RootSetLayout;

    internal int FrameSlotCount => Volatile.Read(ref _frameSlotCount);

    /// <summary>
    /// Publishes the generation-local native layout operations once the device owner is
    /// ready. Wrapper families use this authority instead of calling the renderer facade.
    /// </summary>
    internal void ConfigureLayoutOperations(
        Func<uint, DescriptorSetLayoutBinding[], (bool Success, DescriptorSetLayout Layout, bool UsesUpdateAfterBind, bool UsesVariableDescriptorCount)> acquireLayout,
        Action<DescriptorSetLayout> releaseLayout)
    {
        ArgumentNullException.ThrowIfNull(acquireLayout);
        ArgumentNullException.ThrowIfNull(releaseLayout);
        if (Interlocked.CompareExchange(ref _acquireLayout, acquireLayout, null) is not null)
            return;
        _releaseLayout = releaseLayout;
    }

    internal void PublishBackendObjectContext(VulkanBackendObjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        VulkanBackendObjectContext? current = Interlocked.CompareExchange(
            ref _backendContext,
            context,
            comparand: null);
        if (current is not null && !ReferenceEquals(current, context))
            throw new InvalidOperationException("The descriptor manager already owns a different backend context.");
    }

    internal void ConfigureDeviceServices(
        VulkanBackendObjectContext context,
        VulkanFrameTelemetry frameTelemetry,
        VulkanWrapperLookupPort wrapperLookup)
    {
        PublishBackendObjectContext(context);
        ArgumentNullException.ThrowIfNull(frameTelemetry);
        ArgumentNullException.ThrowIfNull(wrapperLookup);
        if (Interlocked.CompareExchange(ref _frameTelemetry, frameTelemetry, null) is { } currentTelemetry &&
            !ReferenceEquals(currentTelemetry, frameTelemetry))
            throw new InvalidOperationException("The descriptor manager already owns different frame telemetry.");
        if (Interlocked.CompareExchange(ref _wrapperLookup, wrapperLookup, null) is { } currentLookup &&
            !ReferenceEquals(currentLookup, wrapperLookup))
            throw new InvalidOperationException("The descriptor manager already owns a different Vulkan wrapper lookup port.");
    }

    internal void PublishFrameSlot(int frameSlot)
        => Volatile.Write(ref _desktopFrameSlot, frameSlot);

    internal bool TryAcquireProgramDescriptorSetLayout(
        uint setIndex,
        DescriptorSetLayoutBinding[] bindings,
        out DescriptorSetLayout layout,
        out bool usesUpdateAfterBind,
        out bool usesVariableDescriptorCount)
    {
        return TryAcquireCachedDescriptorSetLayout(
            setIndex,
            bindings,
            out layout,
            out usesUpdateAfterBind,
            out usesVariableDescriptorCount);
    }

    internal void ReleaseProgramDescriptorSetLayout(DescriptorSetLayout layout)
        => ReleaseCachedDescriptorSetLayout(layout);

    internal bool EnsureFrameSlotCountFloor(int frameSlotCount)
    {
        if (frameSlotCount <= 0)
            return false;

        while (true)
        {
            int current = Volatile.Read(ref _frameSlotCount);
            if (current >= frameSlotCount)
                return false;

            if (Interlocked.CompareExchange(ref _frameSlotCount, frameSlotCount, current) == current)
                return true;
        }
    }
    internal long SnapshotDescriptorSetContentUpdateGeneration()
        => Volatile.Read(ref _descriptorSetContentUpdateGeneration);

    internal void RegisterLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return;

        using (VulkanFrameLockScope.Enter(
                   SamplerLifetimeLock,
                   EVulkanFrameWaitReason.DescriptorPublicationLock))
            LiveSamplerHandles.Add(sampler.Handle);
    }

    internal void RegisterLiveSampler(Sampler sampler, in SamplerCreateInfo createInfo)
    {
        if (sampler.Handle == 0)
            return;

        using (VulkanFrameLockScope.Enter(
                   SamplerLifetimeLock,
                   EVulkanFrameWaitReason.DescriptorPublicationLock))
        {
            LiveSamplerHandles.Add(sampler.Handle);
            DescriptorHeapSamplerCreateInfos[sampler.Handle] = createInfo with { PNext = null };
        }
    }

    internal void UnregisterLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return;

        using (VulkanFrameLockScope.Enter(
                   SamplerLifetimeLock,
                   EVulkanFrameWaitReason.DescriptorPublicationLock))
        {
            LiveSamplerHandles.Remove(sampler.Handle);
            DescriptorHeapSamplerCreateInfos.Remove(sampler.Handle);
        }
    }

    internal bool IsLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return false;

        using (VulkanFrameLockScope.Enter(
                   SamplerLifetimeLock,
                   EVulkanFrameWaitReason.DescriptorPublicationLock))
            return LiveSamplerHandles.Contains(sampler.Handle);
    }

    internal bool TryGetSamplerCreateInfo(Sampler sampler, out SamplerCreateInfo createInfo)
    {
        if (sampler.Handle != 0)
        {
            using (VulkanFrameLockScope.Enter(
                       SamplerLifetimeLock,
                       EVulkanFrameWaitReason.DescriptorPublicationLock))
            {
                if (DescriptorHeapSamplerCreateInfos.TryGetValue(sampler.Handle, out createInfo))
                    return true;
            }
        }

        createInfo = default;
        return false;
    }

    internal static DescriptorHeapPushDataPayload CreateHeapPushDataPayload(
        DescriptorHeapProgramLayout? layout)
        => layout is { PushDwordCount: > 0 }
            ? new DescriptorHeapPushDataPayload(new uint[layout.PushDwordCount])
            : DescriptorHeapPushDataPayload.Empty;

    internal bool TryGetBufferViewCreateInfo(
        BufferView bufferView,
        out BufferViewCreateInfo createInfo)
    {
        if (bufferView.Handle != 0 &&
            DescriptorHeapBufferViewCreateInfos.TryGetValue(bufferView.Handle, out createInfo))
        {
            return true;
        }

        createInfo = default;
        return false;
    }

    internal ulong[] TakeLiveSamplerHandles()
    {
        using (VulkanFrameLockScope.Enter(
                   SamplerLifetimeLock,
                   EVulkanFrameWaitReason.DescriptorPublicationLock))
        {
            if (LiveSamplerHandles.Count == 0)
                return [];

            ulong[] handles = [.. LiveSamplerHandles];
            LiveSamplerHandles.Clear();
            DescriptorHeapSamplerCreateInfos.Clear();
            return handles;
        }
    }

    internal bool HaveDescriptorSetContentsUpdatedSince(long generation)
        => Volatile.Read(ref _descriptorSetContentUpdateGeneration) != generation;

    internal void RecordDescriptorSetContentUpdate()
        => Interlocked.Increment(ref _descriptorSetContentUpdateGeneration);

    internal int RecordDescriptorUpdateInvalidationDiagnostic()
        => Interlocked.Increment(ref _descriptorUpdateInvalidationDiagnosticCount);

    internal int RecordMeshOwnershipDiagnostic()
        => Interlocked.Increment(ref _meshOwnershipDiagnosticCount);

    /// <summary>
    /// Removes compute cache entries backed by descriptor pools that still own an
    /// exact retired buffer generation. Pools are retired as whole units because
    /// compute pools deliberately do not opt into individual descriptor-set free.
    /// </summary>
    internal int RetireSupersededComputeDescriptorPools(
        ReadOnlySpan<VulkanDescriptorSetGenerationReference> affectedSets)
    {
        if (affectedSets.IsEmpty)
            return DrainPendingSupersededComputePoolRetirements();

        lock (Compute.Gate)
        {
            ComputeDescriptorImageCache[]? caches = Compute.Caches;
            if (caches is null)
                return 0;

            HashSet<ulong> poolHandles = [];
            for (int cacheIndex = 0; cacheIndex < caches.Length; cacheIndex++)
            {
                ComputeDescriptorImageCache cache = caches[cacheIndex];
                foreach ((ComputeDescriptorCacheKey key, DescriptorSet[] sets) in cache.CachedSets)
                {
                    if (!cache.CachedSetPools.TryGetValue(key, out DescriptorPool pool) ||
                        pool.Handle == 0 ||
                        !ContainsCurrentDescriptorSet(sets, affectedSets))
                    {
                        continue;
                    }

                    poolHandles.Add(pool.Handle);
                }

                // A publication may already have dropped its fingerprint-keyed
                // cache entry while its allocation block still owns the exact
                // descriptor set. Discover that block through the authoritative
                // pool ownership ledger so it cannot pin a superseded buffer
                // generation indefinitely.
                foreach ((_, List<ComputeDescriptorPoolBlock> blocks) in cache.PoolsBySchema)
                {
                    for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
                    {
                        DescriptorPool pool = blocks[blockIndex].Pool;
                        if (pool.Handle != 0 &&
                            PoolOwnsCurrentDescriptorSet(pool, affectedSets))
                        {
                            poolHandles.Add(pool.Handle);
                        }
                    }
                }
            }
            if (poolHandles.Count == 0)
                return 0;

            for (int cacheIndex = 0; cacheIndex < caches.Length; cacheIndex++)
            {
                ComputeDescriptorImageCache cache = caches[cacheIndex];
                List<ComputeDescriptorCacheKey>? keysToRemove = null;
                foreach ((ComputeDescriptorCacheKey key, DescriptorPool pool) in cache.CachedSetPools)
                {
                    if (poolHandles.Contains(pool.Handle))
                        (keysToRemove ??= []).Add(key);
                }
                if (keysToRemove is not null)
                {
                    for (int keyIndex = 0; keyIndex < keysToRemove.Count; keyIndex++)
                    {
                        ComputeDescriptorCacheKey key = keysToRemove[keyIndex];
                        cache.CachedSets.Remove(key);
                        cache.CachedSetPools.Remove(key);
                    }
                }

                List<ulong>? schemasToRemove = null;
                foreach ((ulong schema, List<ComputeDescriptorPoolBlock> blocks) in cache.PoolsBySchema)
                {
                    for (int blockIndex = blocks.Count - 1; blockIndex >= 0; blockIndex--)
                    {
                        DescriptorPool pool = blocks[blockIndex].Pool;
                        if (!poolHandles.Contains(pool.Handle))
                            continue;

                        blocks.RemoveAt(blockIndex);
                        _pendingSupersededComputePoolRetirements[pool.Handle] = pool;
                    }
                    if (blocks.Count == 0)
                        (schemasToRemove ??= []).Add(schema);
                }
                if (schemasToRemove is not null)
                {
                    for (int schemaIndex = 0; schemaIndex < schemasToRemove.Count; schemaIndex++)
                        cache.PoolsBySchema.Remove(schemasToRemove[schemaIndex]);
                }
            }
        }

        return DrainPendingSupersededComputePoolRetirements();
    }

    internal int DrainPendingSupersededComputePoolRetirements()
    {
        int handedOffCount = 0;
        while (true)
        {
            DescriptorPool pool;
            lock (Compute.Gate)
            {
                if (_pendingSupersededComputePoolRetirements.Count == 0)
                    return handedOffCount;

                pool = default;
                foreach (DescriptorPool candidate in
                         _pendingSupersededComputePoolRetirements.Values)
                {
                    pool = candidate;
                    break;
                }
            }

            // This can enter lifetime authority and must stay outside Compute.Gate.
            RetireDescriptorPool(pool);
            lock (Compute.Gate)
            {
                if (_pendingSupersededComputePoolRetirements.Remove(pool.Handle))
                    handedOffCount++;
            }
        }
    }

    private bool PoolOwnsCurrentDescriptorSet(
        DescriptorPool pool,
        ReadOnlySpan<VulkanDescriptorSetGenerationReference> affectedSets)
    {
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            if (!tracker.DescriptorSetsByPool.TryGetValue(
                    pool.Handle,
                    out HashSet<ulong>? ownedSets))
            {
                return false;
            }

            for (int index = 0; index < affectedSets.Length; index++)
            {
                VulkanDescriptorSetGenerationReference affected = affectedSets[index];
                if (ownedSets.Contains(affected.Set.Handle) &&
                    tracker.DescriptorSetLifetimes.TryGetValue(
                        affected.Set.Handle,
                        out VulkanDescriptorSetLifetimeRecord? state) &&
                    state.Generation == affected.Generation)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool ContainsCurrentDescriptorSet(
        ReadOnlySpan<DescriptorSet> sets,
        ReadOnlySpan<VulkanDescriptorSetGenerationReference> affectedSets)
    {
        for (int index = 0; index < sets.Length; index++)
        {
            ulong handle = sets[index].Handle;
            for (int affectedIndex = 0; affectedIndex < affectedSets.Length; affectedIndex++)
            {
                VulkanDescriptorSetGenerationReference affected = affectedSets[affectedIndex];
                if (affected.Set.Handle == handle &&
                    ResourceRuntime.IsDescriptorSetGenerationCurrent(affected))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Republishes every descriptor snapshot that directly references a retiring
    /// generation. The lifetime lock must be held by the caller. Republish rather
    /// than remove is intentional: command submission still needs the exact
    /// reference closure and pins for the descriptor-set generation it observed.
    /// </summary>
    internal int InvalidateResourceReferencesNoLock(
        VulkanLifetimeAuthority lifetime,
        VulkanResourceLifetimeKey key)
    {
        if (!lifetime.Tracker.DescriptorSetsByReferencedResource.TryGetValue(
                key,
                out HashSet<ulong>? descriptorSets) ||
            descriptorSets.Count == 0)
        {
            return 0;
        }

        int invalidated = 0;
        foreach (ulong descriptorSetHandle in descriptorSets)
        {
            if (!lifetime.Tracker.DescriptorSetLifetimes.TryGetValue(
                    descriptorSetHandle,
                    out VulkanDescriptorSetLifetimeRecord? state))
            {
                continue;
            }

            state.Generation++;
            PublishSnapshotNoLock(lifetime, descriptorSetHandle, state);
            invalidated++;
        }

        return invalidated;
    }

    /// <summary>
    /// Removes one descriptor-set generation and releases every reference pin and
    /// reverse index it published. The caller must hold the lifetime lock.
    /// </summary>
    internal static void RemoveDescriptorSetLifetimeNoLock(
        VulkanLifetimeAuthority lifetime,
        ulong setHandle,
        bool forced)
    {
        VulkanResourceLifetimeTracker tracker = lifetime.Tracker;
        if (tracker.DescriptorSetLifetimes.Remove(
                setHandle,
                out VulkanDescriptorSetLifetimeRecord? state))
        {
            foreach ((VulkanResourceLifetimeKey key, ulong generation) in state.PinnedReferences)
            {
                if (tracker.TryResolveResourceGenerationNoLock(
                        key,
                        generation,
                        out VulkanResourceLifetimeRecord resource))
                {
                    resource.Pins.ReleaseDescriptorReference();
                }
            }
            state.PinnedReferences.Clear();
            if (state.Pool.Handle != 0 &&
                tracker.DescriptorSetsByPool.TryGetValue(state.Pool.Handle, out HashSet<ulong>? poolSets))
            {
                poolSets.Remove(setHandle);
                if (poolSets.Count == 0)
                    tracker.DescriptorSetsByPool.Remove(state.Pool.Handle);
            }
            foreach (VulkanResourceLifetimeKey reference in state.IndexedReferences)
            {
                if (!tracker.DescriptorSetsByReferencedResource.TryGetValue(
                        reference,
                        out HashSet<ulong>? sets))
                {
                    continue;
                }
                sets.Remove(setHandle);
                if (sets.Count == 0)
                    tracker.DescriptorSetsByReferencedResource.Remove(reference);
            }
        }

        tracker.RemovePublishedDescriptorSnapshotNoLock(setHandle);
        if (!tracker.ResourceLifetimes.TryGetValue(
                new VulkanResourceLifetimeKey(ObjectType.DescriptorSet, setHandle),
                out VulkanResourceLifetimeRecord? setResource))
        {
            return;
        }

        setResource.State = EVulkanResourceLifetimeState.Destroyed;
        if (forced)
            Interlocked.Increment(ref tracker.ForcedResourceDestructionCount);
    }

    internal static void RemoveDescriptorSetsOwnedByPoolNoLock(
        VulkanLifetimeAuthority lifetime,
        ulong poolHandle,
        bool forced)
    {
        VulkanResourceLifetimeTracker tracker = lifetime.Tracker;
        if (!tracker.DescriptorSetsByPool.TryGetValue(poolHandle, out HashSet<ulong>? ownedSets) ||
            ownedSets.Count == 0)
        {
            tracker.DescriptorSetsByPool.Remove(poolHandle);
            return;
        }

        ulong[] removedSets = [.. ownedSets];
        for (int index = 0; index < removedSets.Length; index++)
            RemoveDescriptorSetLifetimeNoLock(lifetime, removedSets[index], forced);
        tracker.DescriptorSetsByPool.Remove(poolHandle);
    }

    internal static void PublishSnapshotNoLock(
        VulkanLifetimeAuthority lifetime,
        ulong descriptorSetHandle,
        VulkanDescriptorSetLifetimeRecord state)
    {
        VulkanResourceLifetimeTracker tracker = lifetime.Tracker;
        HashSet<VulkanResourceLifetimeKey> references =
            tracker.DescriptorReferencesScratch.Value!;
        HashSet<VulkanResourceLifetimeKey> pinnedReferences =
            tracker.DescriptorPinnedReferencesScratch.Value!;
        references.Clear();
        pinnedReferences.Clear();
        try
        {
            foreach (VulkanDescriptorReferencePair pair in state.References.Values)
            {
                if (pair.First.IsValid)
                    references.Add(pair.First);
                if (pair.Second.IsValid)
                    references.Add(pair.Second);
            }

            VulkanPublishedDescriptorImageReference[] images = state.ImageReferences.Count == 0
                ? []
                : new VulkanPublishedDescriptorImageReference[state.ImageReferences.Count];
            int imageIndex = 0;
            foreach (((uint binding, uint element), VulkanDescriptorImageReference reference) in state.ImageReferences)
                images[imageIndex++] = new VulkanPublishedDescriptorImageReference(binding, element, reference);

            VulkanResourceLifetimeKey[] publishedReferences = references.Count == 0
                ? []
                : new VulkanResourceLifetimeKey[references.Count];
            references.CopyTo(publishedReferences);
            uint[] reflectedBindings = state.ReflectedImageBindings.Count == 0
                ? []
                : new uint[state.ReflectedImageBindings.Count];
            state.ReflectedImageBindings.CopyTo(reflectedBindings);
            VulkanResourceLifetimeKey descriptorSetKey = new(
                ObjectType.DescriptorSet,
                descriptorSetHandle);
            VulkanResourceSlotHandle descriptorSetSlot =
                tracker.TryGetResourceSlotNoLock(
                    descriptorSetKey,
                    out VulkanResourceSlotHandle capturedDescriptorSetSlot)
                        ? capturedDescriptorSetSlot
                        : VulkanResourceSlotHandle.Invalid;
            ulong descriptorSetLifetimeGeneration = descriptorSetSlot.Generation;

            // All fallible snapshot allocations precede semantic pin/index
            // mutation. This keeps a failed publication recoverable without
            // releasing the last conservative reference to the old payload.
            foreach (VulkanResourceLifetimeKey reference in references)
                AddPinnedReferenceClosureNoLock(
                    tracker,
                    reference,
                    pinnedReferences);
            VulkanResourceSlotHandle[] resourceClosure =
                new VulkanResourceSlotHandle[pinnedReferences.Count];
            int closureIndex = 0;
            foreach (VulkanResourceLifetimeKey reference in pinnedReferences)
            {
                if (!tracker.TryGetResourceSlotNoLock(
                        reference,
                        out VulkanResourceSlotHandle slot))
                {
                    throw new InvalidOperationException(
                        $"Descriptor set {descriptorSetKey} references untracked resource {reference}.");
                }

                resourceClosure[closureIndex++] = slot;
            }
            VulkanPublishedDescriptorSetSnapshot snapshot = new(
                state.Generation,
                state.ResourceClosureGeneration,
                state.ImagePayloadGeneration,
                descriptorSetLifetimeGeneration,
                descriptorSetSlot,
                resourceClosure,
                publishedReferences,
                images,
                reflectedBindings,
                state.HasReflection,
                state.NativePublicationState);
            UpdateReferenceIndexNoLock(
                tracker,
                descriptorSetHandle,
                state,
                references);
            UpdateGenerationPinsNoLock(tracker, state, pinnedReferences);
            tracker.PublishDescriptorSnapshotNoLock(
                descriptorSetHandle,
                snapshot);
        }
        finally
        {
            references.Clear();
            pinnedReferences.Clear();
        }
    }

    private static void AddPinnedReferenceClosureNoLock(
        VulkanResourceLifetimeTracker tracker,
        VulkanResourceLifetimeKey key,
        HashSet<VulkanResourceLifetimeKey> pins)
    {
        if (!key.IsValid || !pins.Add(key))
            return;

        if (key.Type == ObjectType.ImageView &&
            tracker.ImageViewBackingImages.TryGetValue(key.Handle, out ulong image) && image != 0)
        {
            pins.Add(new VulkanResourceLifetimeKey(ObjectType.Image, image));
        }
        else if (key.Type == ObjectType.BufferView &&
                 tracker.BufferViewBackingBuffers.TryGetValue(key.Handle, out ulong buffer) && buffer != 0)
        {
            pins.Add(new VulkanResourceLifetimeKey(ObjectType.Buffer, buffer));
        }
    }

    private static void UpdateGenerationPinsNoLock(
        VulkanResourceLifetimeTracker tracker,
        VulkanDescriptorSetLifetimeRecord state,
        HashSet<VulkanResourceLifetimeKey> references)
    {
        foreach ((VulkanResourceLifetimeKey key, ulong generation) in state.PinnedReferences)
            if (tracker.TryResolveResourceGenerationNoLock(
                    key,
                    generation,
                    out VulkanResourceLifetimeRecord resource))
                resource.Pins.ReleaseDescriptorReference();
        state.PinnedReferences.Clear();

        foreach (VulkanResourceLifetimeKey key in references)
        {
            if (!tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                continue;
            resource.Pins.AddDescriptorReference();
            state.PinnedReferences[key] = resource.Generation;
        }
    }

    private static void UpdateReferenceIndexNoLock(
        VulkanResourceLifetimeTracker tracker,
        ulong descriptorSetHandle,
        VulkanDescriptorSetLifetimeRecord state,
        HashSet<VulkanResourceLifetimeKey> references)
    {
        foreach (VulkanResourceLifetimeKey previous in state.IndexedReferences)
        {
            if (references.Contains(previous) ||
                !tracker.DescriptorSetsByReferencedResource.TryGetValue(previous, out HashSet<ulong>? sets))
                continue;
            sets.Remove(descriptorSetHandle);
            if (sets.Count == 0)
                tracker.DescriptorSetsByReferencedResource.Remove(previous);
        }

        foreach (VulkanResourceLifetimeKey reference in references)
        {
            if (state.IndexedReferences.Contains(reference))
                continue;
            if (!tracker.DescriptorSetsByReferencedResource.TryGetValue(reference, out HashSet<ulong>? sets))
                tracker.DescriptorSetsByReferencedResource[reference] = sets = [];
            sets.Add(descriptorSetHandle);
        }

        state.IndexedReferences.Clear();
        foreach (VulkanResourceLifetimeKey reference in references)
            state.IndexedReferences.Add(reference);
    }

    internal bool TryAcquireSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        XRMaterial material,
        out VkMeshRenderer.DescriptorAllocation allocation)
    {
        using (VulkanFrameLockScope.Enter(
                   _sharedMeshDescriptorAllocationLock,
                   EVulkanFrameWaitReason.DescriptorArena))
        {
            if (_sharedMeshDescriptorAllocations.TryGetValue(
                    key,
                    out List<VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    VkMeshRenderer.DescriptorAllocation candidate = candidates[i];
                    if (candidate.UsesSharedMaterialTier &&
                        !ReferenceEquals(candidate.Material, material))
                    {
                        continue;
                    }

                    candidate.SharedReferenceCount++;
                    allocation = candidate;
                    return true;
                }
            }
        }

        allocation = null!;
        return false;
    }

    internal VkMeshRenderer.DescriptorAllocation PublishSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        VkMeshRenderer.DescriptorAllocation allocation,
        out bool published)
    {
        using (VulkanFrameLockScope.Enter(
                   _sharedMeshDescriptorAllocationLock,
                   EVulkanFrameWaitReason.DescriptorArena))
        {
            if (!_sharedMeshDescriptorAllocations.TryGetValue(
                    key,
                    out List<VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                candidates = [];
                _sharedMeshDescriptorAllocations.Add(key, candidates);
            }
            else
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    VkMeshRenderer.DescriptorAllocation candidate = candidates[i];
                    if (candidate.UsesSharedMaterialTier &&
                        !ReferenceEquals(candidate.Material, allocation.Material))
                    {
                        continue;
                    }

                    candidate.SharedReferenceCount++;
                    published = false;
                    return candidate;
                }
            }

            allocation.SharedReferenceCount = 1;
            candidates.Add(allocation);
            published = true;
            return allocation;
        }
    }

    internal bool ReleaseSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        VkMeshRenderer.DescriptorAllocation allocation)
    {
        using (VulkanFrameLockScope.Enter(
                   _sharedMeshDescriptorAllocationLock,
                   EVulkanFrameWaitReason.DescriptorArena))
        {
            if (allocation.SharedReferenceCount > 0)
                allocation.SharedReferenceCount--;
            if (allocation.SharedReferenceCount != 0)
                return false;

            if (_sharedMeshDescriptorAllocations.TryGetValue(
                    key,
                    out List<VkMeshRenderer.DescriptorAllocation>? candidates))
            {
                candidates.Remove(allocation);
                if (candidates.Count == 0)
                    _sharedMeshDescriptorAllocations.Remove(key);
            }

            return true;
        }
    }
}

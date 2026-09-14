using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Rendering.Materials;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Persistent, completion-safe native material-table banks. Native bank creation is deliberately
/// owned by one bounded worker: command recording may publish a completed bank, but never creates
/// one inline. Banks remain scoped to one frame-slot identity and can only be reused after that
/// same slot has reset.
/// </summary>
internal sealed class VulkanMaterialTablePreparedMap
{
    private const ulong InitialCapacity = 4096UL;
    private const int PublicationPageBytes = 64 * 1024;
    private const int MaximumPendingAllocations = 4;
    // Keep one native-worker queue position available for demand. Standby replenishment is
    // advisory and must never prevent a newly required material table from being admitted.
    private const int MaximumPendingStandbyAllocations = MaximumPendingAllocations - 1;
    private const int MaximumStandbyRetryTargets = 32;
    private static readonly long StandbyRetryDelayTicks = Stopwatch.Frequency;
    private const int MaximumDiagnosticReadBytes = 16 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly List<Bank> _banks = [];
    private readonly List<PendingAllocation> _pending = [];
    private readonly Dictionary<StandbyKey, ulong> _standbyLargestObservedDemand = [];
    private readonly Dictionary<StandbyRetryTarget, long> _standbyRetryAfterTicks = [];
    // The resource runtime exists before frame arenas. Do not start a native worker until an
    // allocation is actually admitted, otherwise an early initialization failure leaks a thread.
    private NativeAllocationWorker? _allocationWorker;
    private long _nativeAllocations;
    private long _pageWrites;
    private long _bytesWritten;
    private long _reuses;
    private long _growthPending;
    private long _emergencyWaits;
    private long _standbyReplenishmentFailures;
    private long _standbyAllocationsQueued;
    private long _standbyAllocationsReady;
    private long _standbyClaims;
    private int _shutdownStarted;

    internal VulkanMaterialTablePreparedAuthority CreateAuthority(VulkanFrameDataArena arena, int frameSlot)
        => new(this, arena, arena.Identity, arena.Generation, frameSlot, arena.GetFrameSlotResetEpoch(frameSlot));

    internal bool IsCurrent(in VulkanMaterialTablePreparedAuthority authority)
        => ReferenceEquals(authority.Owner, this) && authority.ArenaIdentity != 0 &&
            authority.ArenaGeneration != 0 && authority.ResetEpoch != 0 &&
            authority.Arena.Identity == authority.ArenaIdentity &&
            authority.Arena.Generation == authority.ArenaGeneration && authority.FrameSlot >= 0 &&
            authority.Arena.GetFrameSlotResetEpoch(authority.FrameSlot) == authority.ResetEpoch;

    internal VulkanMaterialTablePreparedMapCounters SnapshotCounters()
    {
        lock (_sync)
        {
            int ownerBanks = 0;
            int standbyBanks = 0;
            foreach (Bank bank in _banks)
                if (bank.Kind == BankKind.Standby)
                    ++standbyBanks;
                else
                    ++ownerBanks;

            int demandPending = 0;
            int standbyPending = 0;
            foreach (PendingAllocation pending in _pending)
                if (pending.Kind == PendingAllocationKind.Standby)
                    ++standbyPending;
                else
                    ++demandPending;

            return new(
                Volatile.Read(ref _nativeAllocations), Volatile.Read(ref _pageWrites),
                Volatile.Read(ref _bytesWritten), Volatile.Read(ref _reuses),
                Volatile.Read(ref _growthPending), Volatile.Read(ref _emergencyWaits),
                ownerBanks, demandPending,
                Volatile.Read(ref _standbyAllocationsQueued),
                Volatile.Read(ref _standbyAllocationsReady),
                Volatile.Read(ref _standbyClaims),
                Volatile.Read(ref _standbyReplenishmentFailures), standbyBanks, standbyPending);
        }
    }

    internal bool TryPrepare(in VulkanMaterialTablePreparedAuthority authority,
        VulkanBackendObjectContext context, VulkanBufferResourceService buffers,
        GPUMaterialTablePublication publication, out string reason)
        => TryPrepare(in authority, context, buffers, publication, out _, out reason);

    /// <summary>
    /// Prepares an exact publication or returns a typed disposition which lets frame admission
    /// distinguish a normal asynchronous retry from a materialization failure.
    /// </summary>
    internal bool TryPrepare(in VulkanMaterialTablePreparedAuthority authority,
        VulkanBackendObjectContext context, VulkanBufferResourceService buffers,
        GPUMaterialTablePublication publication, out EVulkanMaterialTablePreparedDisposition disposition,
        out string reason)
    {
        disposition = EVulkanMaterialTablePreparedDisposition.Failed;
        if (!IsCurrent(in authority) || !context.IsDeviceOperational)
        {
            reason = "The Vulkan device is unavailable for material-table publication lowering.";
            return false;
        }

        ulong requiredBytes = checked((ulong)publication.RowCount * publication.RowByteStride);
        if (requiredBytes == 0)
        {
            reason = "An empty material-table publication cannot back an SSBO descriptor.";
            return false;
        }

        lock (_sync)
        {
            DrainCompletedAllocations(buffers, in authority);
            Bank? bank = FindExact(in authority, publication);
            if (bank is not null)
            {
                EnsureStandbyReplenishment(in authority, context, requiredBytes);
                disposition = EVulkanMaterialTablePreparedDisposition.Ready;
                reason = string.Empty;
                return true;
            }

            bank = FindReusable(in authority, publication.OwnerId, requiredBytes);
            if (bank is null)
            {
                bank = TryClaimCompletedStandby(in authority, publication.OwnerId, requiredBytes);
                if (bank is null &&
                    !TryQueueOrPublishAllocation(in authority, context, buffers, publication.OwnerId,
                        requiredBytes, out bank, out bool isPending, out reason))
                {
                    disposition = isPending
                        ? EVulkanMaterialTablePreparedDisposition.Pending
                        : EVulkanMaterialTablePreparedDisposition.Failed;
                    return false;
                }
            }
            else
                Interlocked.Increment(ref _reuses);

            Bank readyBank = bank ?? throw new InvalidOperationException("A completed material-table allocation did not publish a bank.");
            try
            {
                WriteChangedPages(readyBank, context, buffers, publication);
                readyBank.Assign(in authority, publication, requiredBytes,
                    context.Resources.GetPublishedGeneration(ObjectType.Buffer, readyBank.Buffer.Handle));
                RetireSupersededUndersizedBanks(in authority, publication.OwnerId, readyBank, buffers);
                EnsureStandbyReplenishment(in authority, context, requiredBytes);
                disposition = EVulkanMaterialTablePreparedDisposition.Ready;
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = $"The material-table native backing could not be prepared: {exception.Message}";
                return false;
            }
        }
    }

    internal bool TryResolve(in VulkanMaterialTablePreparedAuthority authority,
        GPUMaterialTablePublication publication, out VulkanMaterialTablePreparedBinding binding)
    {
        lock (_sync)
        {
            if (!IsCurrent(in authority))
            {
                binding = default;
                return false;
            }
            Bank? bank = FindExact(in authority, publication);
            if (bank is null || bank.NativeGeneration == 0)
            {
                binding = default;
                return false;
            }
            binding = new(bank.Buffer, bank.NativeGeneration, bank.Range, publication.RowByteStride,
                publication.OwnerId, publication.Generation, publication.DescriptorClosureGeneration);
            return true;
        }
    }

    /// <summary>Reads one exact material-table native publication for headless receipt validation.</summary>
    internal bool TryReadPublication(VulkanBackendObjectContext context,
        VulkanBufferResourceService buffers, GPUMaterialTablePublication publication,
        out VulkanMaterialTablePreparedBinding binding, out byte[] bytes)
    {
        binding = default;
        bytes = [];
        ulong range = checked((ulong)publication.RowCount * publication.RowByteStride);
        if (range == 0 || range > MaximumDiagnosticReadBytes)
            return false;

        lock (_sync)
        {
            Bank? bank = FindPublication(context, publication);
            if (bank is null || bank.Buffer.Handle == 0 || bank.Memory.Handle == 0 || bank.Range < range ||
                !buffers.TryCreateMappedSlice(context, bank.Buffer, bank.Memory, 0, range, out VulkanMappedMemorySlice slice) ||
                !buffers.TryAcquireRead(context, in slice, out VulkanMappedMemoryReadLease lease))
                return false;

            using (lease)
            {
                bytes = new byte[checked((int)range)];
                lease.Bytes.CopyTo(bytes);
            }
            binding = new(bank.Buffer, bank.NativeGeneration, bank.Range, publication.RowByteStride,
                publication.OwnerId, publication.Generation, publication.DescriptorClosureGeneration);
            return true;
        }
    }

    /// <summary>Joins native allocation before device teardown, then retires every owned backing.</summary>
    internal void Clear(VulkanBufferResourceService buffers)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            // Queue admission is under _sync. Let an already-admitted allocation become visible
            // before joining the worker, so no task can be appended after its final wake-up.
            NativeAllocationWorker? worker;
            lock (_sync)
                worker = _allocationWorker;
            worker?.Dispose();
        }

        lock (_sync)
        {
            foreach (PendingAllocation pending in _pending)
                RetireCompletedPending(pending, buffers);
            _pending.Clear();
            _standbyLargestObservedDemand.Clear();
            _standbyRetryAfterTicks.Clear();
            foreach (Bank bank in _banks)
                if (bank.Buffer.Handle != 0 && !bank.Retired)
                    buffers.Retire(bank.Buffer, bank.Memory, "MaterialTable.PreparedBacking");
            _banks.Clear();
        }
    }

    private bool TryQueueOrPublishAllocation(in VulkanMaterialTablePreparedAuthority authority,
        VulkanBackendObjectContext context, VulkanBufferResourceService buffers, ulong ownerId,
        ulong requiredBytes, out Bank? bank, out bool isPending, out string reason)
    {
        bank = null;
        isPending = false;
        PendingAllocation? existingPending = FindPending(in authority, ownerId);
        if (existingPending is { } completedPending && completedPending.Task.IsCompleted)
        {
            _pending.Remove(completedPending);
            AllocationResult result = completedPending.Task.GetAwaiter().GetResult();
            if (!result.Success)
            {
                reason = $"Material-table backing allocation failed; retry is permitted: {result.Error}";
                return false;
            }
            if (result.Capacity < requiredBytes)
            {
                buffers.Retire(result.Buffer, result.Memory, "MaterialTable.PreparedBacking.UndersizedPending");
                bool queued = QueueDemandAllocation(in authority, context, ownerId, requiredBytes, out _, out isPending, out reason);
                return queued;
            }

            bank = new Bank();
            bank.Reserve(in authority, ownerId, result);
            _banks.Add(bank);
            reason = string.Empty;
            return true;
        }

        if (existingPending is not null)
        {
            isPending = true;
            reason = "Material-table backing growth is pending on the native allocation worker; retry preparation.";
            return false;
        }

        return QueueDemandAllocation(in authority, context, ownerId, requiredBytes, out bank, out isPending, out reason);
    }

    private bool QueueDemandAllocation(in VulkanMaterialTablePreparedAuthority authority,
        VulkanBackendObjectContext context, ulong ownerId, ulong requiredBytes, out Bank? bank,
        out bool isPending, out string reason)
    {
        bank = null;
        isPending = false;
        if (Volatile.Read(ref _shutdownStarted) != 0)
        {
            reason = "Material-table backing allocation is unavailable because Vulkan shutdown has started.";
            return false;
        }
        if (_pending.Count >= MaximumPendingAllocations)
        {
            isPending = true;
            reason = $"Material-table backing allocation queue is full ({MaximumPendingAllocations}); retry preparation.";
            return false;
        }

        if (!TryCalculateAllocationCapacity(
                requiredBytes,
                GetStorageBufferAllocationLimit(context),
                requireNextCapacityClass: false,
                out ulong capacity))
        {
            reason = "Material-table backing demand exceeds the Vulkan storage-buffer allocation limit.";
            return false;
        }
        try
        {
            NativeAllocationWorker worker = _allocationWorker ??= new NativeAllocationWorker();
            Task<AllocationResult> task = worker.Enqueue(() => Allocate(context, capacity));
            _pending.Add(PendingAllocation.ForDemand(in authority, ownerId, task));
            Interlocked.Increment(ref _growthPending);
            isPending = true;
            reason = "Material-table backing growth was queued on the native allocation worker; retry preparation.";
            return false;
        }
        catch (ObjectDisposedException)
        {
            reason = "Material-table backing allocation worker is stopped; retry is not possible during shutdown.";
            return false;
        }
    }

    private void DrainCompletedAllocations(VulkanBufferResourceService buffers,
        in VulkanMaterialTablePreparedAuthority currentAuthority)
    {
        for (int index = _pending.Count - 1; index >= 0; --index)
        {
            PendingAllocation pending = _pending[index];
            if (pending.Kind == PendingAllocationKind.Standby)
            {
                if (!pending.Task.IsCompleted)
                    continue;

                _pending.RemoveAt(index);
                StandbyRetryTarget retryTarget = new(pending.StandbyKey, pending.RequestedCapacity);
                if (!pending.Task.IsCompletedSuccessfully)
                {
                    RecordStandbyReplenishmentFailure(in retryTarget, "the native allocation worker faulted");
                    continue;
                }

                AllocationResult standbyAllocation = pending.Task.GetAwaiter().GetResult();
                if (!standbyAllocation.Success || standbyAllocation.Buffer.Handle == 0)
                {
                    RecordStandbyReplenishmentFailure(
                        in retryTarget,
                        string.IsNullOrWhiteSpace(standbyAllocation.Error)
                            ? "the native allocation failed"
                            : standbyAllocation.Error);
                    continue;
                }

                // Reset epochs are deliberately absent from standby identity. A completed
                // reserve survives slot reuse, but never an arena generation replacement.
                StandbyKey standbyKey = pending.StandbyKey;
                if (!standbyKey.MatchesArena(in currentAuthority))
                {
                    _standbyRetryAfterTicks.Remove(retryTarget);
                    buffers.Retire(standbyAllocation.Buffer, standbyAllocation.Memory,
                        "MaterialTable.PreparedBacking.UnusedStandby");
                    continue;
                }

                Bank? existingStandby = FindCompletedStandby(in standbyKey);
                if (existingStandby is not null)
                {
                    if (existingStandby.Capacity >= standbyAllocation.Capacity)
                    {
                        _standbyRetryAfterTicks.Remove(retryTarget);
                        buffers.Retire(standbyAllocation.Buffer, standbyAllocation.Memory,
                            "MaterialTable.PreparedBacking.UndersizedStandbyReplacement");
                        continue;
                    }

                    existingStandby.Retired = true;
                    buffers.Retire(existingStandby.Buffer, existingStandby.Memory,
                        "MaterialTable.PreparedBacking.StandbyReplacement");
                    _banks.Remove(existingStandby);
                }

                Bank standby = new();
                standby.ReserveStandby(in standbyKey, standbyAllocation);
                _banks.Add(standby);
                _standbyRetryAfterTicks.Remove(retryTarget);
                Interlocked.Increment(ref _standbyAllocationsReady);
                continue;
            }

            VulkanMaterialTablePreparedAuthority pendingAuthority = pending.Authority;
            if (!pending.Task.IsCompleted || IsCurrent(in pendingAuthority))
                continue;
            _pending.RemoveAt(index);
            if (!pending.Task.IsCompletedSuccessfully)
                continue;
            AllocationResult allocation = pending.Task.GetAwaiter().GetResult();
            if (!allocation.Success || allocation.Buffer.Handle == 0)
                continue;
            Bank spare = new();
            spare.Reserve(in pendingAuthority, pending.TableOwnerId, allocation);
            _banks.Add(spare);
        }

        for (int index = _banks.Count - 1; index >= 0; --index)
        {
            Bank bank = _banks[index];
            if (bank.Kind != BankKind.Standby || bank.StandbyKey.MatchesArena(in currentAuthority))
                continue;
            bank.Retired = true;
            buffers.Retire(bank.Buffer, bank.Memory, "MaterialTable.PreparedBacking.StaleStandby");
            _banks.RemoveAt(index);
        }

        List<StandbyKey>? staleStandbyKeys = null;
        foreach (StandbyKey key in _standbyLargestObservedDemand.Keys)
        {
            if (key.MatchesArena(in currentAuthority))
                continue;
            staleStandbyKeys ??= [];
            staleStandbyKeys.Add(key);
        }
        if (staleStandbyKeys is not null)
            foreach (StandbyKey key in staleStandbyKeys)
                _standbyLargestObservedDemand.Remove(key);

        List<StandbyRetryTarget>? staleRetryTargets = null;
        foreach (StandbyRetryTarget target in _standbyRetryAfterTicks.Keys)
        {
            if (target.Key.MatchesArena(in currentAuthority))
                continue;
            staleRetryTargets ??= [];
            staleRetryTargets.Add(target);
        }
        if (staleRetryTargets is not null)
            foreach (StandbyRetryTarget target in staleRetryTargets)
                _standbyRetryAfterTicks.Remove(target);
    }

    private static void RetireCompletedPending(PendingAllocation pending, VulkanBufferResourceService buffers)
    {
        if (!pending.Task.IsCompletedSuccessfully)
            return;
        AllocationResult result = pending.Task.GetAwaiter().GetResult();
        if (result.Success && result.Buffer.Handle != 0)
            buffers.Retire(result.Buffer, result.Memory, "MaterialTable.PreparedBacking.Unpublished");
    }

    private AllocationResult Allocate(VulkanBackendObjectContext context, ulong capacity)
    {
        try
        {
            (Buffer buffer, DeviceMemory memory) = context.Resources.Buffers.CreateDedicatedRaw(context, capacity,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                owner: "MaterialTable.PreparedBacking");
            Interlocked.Increment(ref _nativeAllocations);
            return new(true, buffer, memory, capacity, string.Empty);
        }
        catch (Exception exception)
        {
            return new(false, default, default, 0, exception.Message);
        }
    }

    private void RetireSupersededUndersizedBanks(in VulkanMaterialTablePreparedAuthority authority,
        ulong ownerId, Bank replacement, VulkanBufferResourceService buffers)
    {
        for (int index = _banks.Count - 1; index >= 0; --index)
        {
            Bank candidate = _banks[index];
            if (ReferenceEquals(candidate, replacement) || candidate.Retired || candidate.Kind != BankKind.Owner || candidate.TableOwnerId != ownerId ||
                !candidate.IsReusableBy(in authority) ||
                candidate.Capacity >= replacement.Capacity)
                continue;
            candidate.Retired = true;
            buffers.Retire(candidate.Buffer, candidate.Memory, "MaterialTable.PreparedBacking.Grow");
            // Retire owns the actual destruction and completion proof. The map no longer has a
            // valid publication to resolve once retirement invalidates its native generation.
            _banks.RemoveAt(index);
        }
    }

    private Bank? FindExact(in VulkanMaterialTablePreparedAuthority authority, GPUMaterialTablePublication publication)
    {
        foreach (Bank bank in _banks)
            if (!bank.Retired && bank.Kind == BankKind.Owner && bank.Matches(in authority, publication))
                return bank;
        return null;
    }

    private Bank? FindPublication(VulkanBackendObjectContext context, GPUMaterialTablePublication publication)
    {
        foreach (Bank bank in _banks)
            if (!bank.Retired && bank.Kind == BankKind.Owner && bank.NativeGeneration != 0 &&
                context.Resources.GetPublishedGeneration(ObjectType.Buffer, bank.Buffer.Handle) == bank.NativeGeneration &&
                bank.TableOwnerId == publication.OwnerId && bank.PublicationGeneration == publication.Generation &&
                bank.DescriptorClosureGeneration == publication.DescriptorClosureGeneration)
                return bank;
        return null;
    }

    private Bank? FindReusable(in VulkanMaterialTablePreparedAuthority authority, ulong ownerId, ulong requiredBytes)
    {
        foreach (Bank bank in _banks)
            if (!bank.Retired && bank.Kind == BankKind.Owner && bank.TableOwnerId == ownerId && bank.IsReusableBy(in authority) &&
                bank.Capacity >= requiredBytes)
                return bank;
        return null;
    }

    private PendingAllocation? FindPending(in VulkanMaterialTablePreparedAuthority authority, ulong ownerId)
    {
        foreach (PendingAllocation pending in _pending)
            if (pending.Kind == PendingAllocationKind.Demand && pending.TableOwnerId == ownerId && pending.Authority.ArenaIdentity == authority.ArenaIdentity &&
                pending.Authority.ArenaGeneration == authority.ArenaGeneration &&
                pending.Authority.FrameSlot == authority.FrameSlot)
                return pending;
        return null;
    }

    private Bank? TryClaimCompletedStandby(
        in VulkanMaterialTablePreparedAuthority authority,
        ulong ownerId,
        ulong requiredBytes)
    {
        StandbyKey key = StandbyKey.From(in authority);
        Bank? standby = FindCompletedStandby(in key);
        if (standby is null || standby.Capacity < requiredBytes)
            return null;

        standby.ClaimStandby(in authority, ownerId);
        Interlocked.Increment(ref _standbyClaims);
        return standby;
    }

    private Bank? FindCompletedStandby(in StandbyKey key)
    {
        foreach (Bank bank in _banks)
            if (!bank.Retired && bank.Kind == BankKind.Standby && bank.StandbyKey == key)
                return bank;
        return null;
    }

    private void EnsureStandbyReplenishment(
        in VulkanMaterialTablePreparedAuthority authority,
        VulkanBackendObjectContext context,
        ulong requiredBytes)
    {
        try
        {
            EnsureStandbyReplenishmentCore(in authority, context, requiredBytes);
        }
        catch (Exception exception)
        {
            // A standby is advisory. A successful exact/reusable/claimed demand publication
            // remains valid if reserve setup, capability lookup, or worker admission fails.
            StandbyKey key = StandbyKey.From(in authority);
            RecordStandbyReplenishmentFailure(
                new StandbyRetryTarget(key, requiredBytes),
                $"reserve setup failed: {exception.Message}");
        }
    }

    private void EnsureStandbyReplenishmentCore(
        in VulkanMaterialTablePreparedAuthority authority,
        VulkanBackendObjectContext context,
        ulong requiredBytes)
    {
        StandbyKey key = StandbyKey.From(in authority);
        ulong largestObservedDemand = Math.Max(
            requiredBytes,
            _standbyLargestObservedDemand.TryGetValue(key, out ulong previousDemand)
                ? previousDemand
                : 0UL);
        _standbyLargestObservedDemand[key] = largestObservedDemand;

        if (!TryCalculateAllocationCapacity(
                largestObservedDemand, GetStorageBufferAllocationLimit(context),
                requireNextCapacityClass: true, out ulong capacity))
        {
            RecordStandbyReplenishmentFailure(
                new StandbyRetryTarget(key, largestObservedDemand),
                "the largest observed demand cannot fit in the Vulkan storage-buffer allocation limit");
            return;
        }

        StandbyRetryTarget retryTarget = new(key, capacity);
        if (FindCompletedStandby(in key) is { } completedStandby && completedStandby.Capacity >= capacity)
        {
            _standbyRetryAfterTicks.Remove(retryTarget);
            return;
        }
        if (FindPendingStandby(in key) is not null || !CanAttemptStandbyReplenishment(in retryTarget))
            return;

        if (Volatile.Read(ref _shutdownStarted) != 0)
        {
            RecordStandbyReplenishmentFailure(in retryTarget, "Vulkan shutdown has started");
            return;
        }
        if (_pending.Count >= MaximumPendingStandbyAllocations)
        {
            RecordStandbyReplenishmentFailure(
                in retryTarget,
                $"the bounded native allocation queue retains one demand position ({MaximumPendingAllocations})");
            return;
        }

        try
        {
            NativeAllocationWorker worker = _allocationWorker ??= new NativeAllocationWorker();
            Task<AllocationResult> task = worker.Enqueue(() => Allocate(context, capacity));
            _pending.Add(PendingAllocation.ForStandby(in authority, capacity, task));
            Interlocked.Increment(ref _standbyAllocationsQueued);
        }
        catch (Exception exception)
        {
            RecordStandbyReplenishmentFailure(in retryTarget,
                $"the native allocation worker did not admit the reserve: {exception.Message}");
        }
    }

    /// <summary>Counts advisory standby replenishment failures without changing demand admission.</summary>
    internal long StandbyReplenishmentFailureCount
        => Volatile.Read(ref _standbyReplenishmentFailures);

    private PendingAllocation? FindPendingStandby(in StandbyKey key)
    {
        foreach (PendingAllocation pending in _pending)
            if (pending.Kind == PendingAllocationKind.Standby && pending.StandbyKey == key)
                return pending;
        return null;
    }

    private bool CanAttemptStandbyReplenishment(in StandbyRetryTarget target)
        => !_standbyRetryAfterTicks.TryGetValue(target, out long retryAfter) ||
           Stopwatch.GetTimestamp() >= retryAfter;

    private void RecordStandbyReplenishmentFailure(in StandbyRetryTarget target, string detail)
    {
        if (_standbyRetryAfterTicks.Count >= MaximumStandbyRetryTargets &&
            !_standbyRetryAfterTicks.ContainsKey(target))
            _standbyRetryAfterTicks.Clear();
        _standbyRetryAfterTicks[target] = Stopwatch.GetTimestamp() + StandbyRetryDelayTicks;
        Interlocked.Increment(ref _standbyReplenishmentFailures);
        global::XREngine.Debug.VulkanWarningEvery(
            $"Vulkan.MaterialTable.Standby.{target.Key.ArenaIdentity}.{target.Key.ArenaGeneration}.{target.Key.FrameSlot}.{target.Capacity}",
            TimeSpan.FromSeconds(1),
            "[Vulkan][MaterialTable] standby replenishment deferred for arena={0} generation={1} slot={2} capacity={3}: {4}",
            target.Key.ArenaIdentity,
            target.Key.ArenaGeneration,
            target.Key.FrameSlot,
            target.Capacity,
            detail);
    }

    private static ulong GetStorageBufferAllocationLimit(VulkanBackendObjectContext context)
    {
        VulkanPhysicalDeviceCapabilitySnapshot capabilities =
            context.DeviceContext.PhysicalDeviceCapabilities
            ?? throw new InvalidOperationException(
                "The operational Vulkan device has no physical-device capability snapshot.");
        return Math.Min(
            Math.Max((ulong)capabilities.Properties.Limits.MaxStorageBufferRange, 1UL),
            int.MaxValue);
    }

    private static bool TryCalculateAllocationCapacity(
        ulong requiredBytes,
        ulong maximumCapacity,
        bool requireNextCapacityClass,
        out ulong capacity)
    {
        capacity = 0UL;
        if (requiredBytes == 0UL || requiredBytes > maximumCapacity)
            return false;

        capacity = Math.Min(InitialCapacity, maximumCapacity);
        while (capacity < requiredBytes)
        {
            if (capacity > maximumCapacity / 2UL)
            {
                capacity = maximumCapacity;
                break;
            }
            capacity *= 2UL;
        }

        if (requireNextCapacityClass && capacity < maximumCapacity)
            capacity = capacity > maximumCapacity / 2UL ? maximumCapacity : capacity * 2UL;

        return capacity >= requiredBytes;
    }

    private void WriteChangedPages(Bank bank, VulkanBackendObjectContext context,
        VulkanBufferResourceService buffers, GPUMaterialTablePublication publication)
    {
        ReadOnlySpan<ReadOnlyStoragePublication> pages = publication.Chunks;
        bool completeWrite = bank.PageTokens.Length != pages.Length;
        if (completeWrite)
            bank.PageTokens = new ulong[pages.Length];
        bank.EnsureDeltaScratch();
        for (int index = 0; index < pages.Length; ++index)
        {
            ReadOnlyStoragePublication page = pages[index];
            if (!completeWrite && bank.PageTokens[index] == page.TokenId)
                continue;
            int pageLength = page.Length;
            Span<byte> source = bank.PageScratch.AsSpan(0, pageLength);
            page.CopyTo(source);
            ulong pageOffset = checked((ulong)index * (ulong)PublicationPageBytes);
            WriteChangedRowRuns(bank, context, buffers, publication.RowByteStride, pageOffset, source);
            source.CopyTo(bank.Shadow.AsSpan(checked((int)pageOffset), pageLength));
            bank.PageTokens[index] = page.TokenId;
        }
        bank.HasShadow = true;
    }

    private void WriteChangedRowRuns(Bank bank, VulkanBackendObjectContext context,
        VulkanBufferResourceService buffers, uint rowByteStride, ulong pageOffset, ReadOnlySpan<byte> source)
    {
        if (rowByteStride == 0)
            throw new InvalidOperationException("A material-table publication has a zero row stride.");

        int position = 0;
        while (position < source.Length)
        {
            int rowLength = checked((int)Math.Min(
                (ulong)(source.Length - position),
                (ulong)rowByteStride - ((pageOffset + (ulong)position) % rowByteStride)));
            // Spare capacity is not initialized content. Newly exposed rows
            // must be uploaded even when their bytes match uninitialized shadow.
            bool changed = !bank.HasShadow || pageOffset + (ulong)position + (ulong)rowLength > bank.Range ||
                !source.Slice(position, rowLength).SequenceEqual(
                bank.Shadow.AsSpan(checked((int)pageOffset) + position, rowLength));
            if (!changed)
            {
                position += rowLength;
                continue;
            }

            int runStart = position;
            position += rowLength;
            while (position < source.Length)
            {
                rowLength = checked((int)Math.Min(
                    (ulong)(source.Length - position),
                    (ulong)rowByteStride - ((pageOffset + (ulong)position) % rowByteStride)));
                if (bank.HasShadow && pageOffset + (ulong)position + (ulong)rowLength <= bank.Range &&
                    source.Slice(position, rowLength).SequenceEqual(
                        bank.Shadow.AsSpan(checked((int)pageOffset) + position, rowLength)))
                    break;
                position += rowLength;
            }

            int runLength = position - runStart;
            ulong offset = pageOffset + (ulong)runStart;
            if (!buffers.TryCreateMappedSlice(context, bank.Buffer, bank.Memory, offset,
                    checked((ulong)runLength), out VulkanMappedMemorySlice slice) ||
                !buffers.TryAcquireWrite(context, in slice, out VulkanMappedMemoryWriteLease lease))
                throw new InvalidOperationException("The dedicated material-table backing is not writable.");
            using (lease)
                source.Slice(runStart, runLength).CopyTo(lease.Bytes);
            Interlocked.Increment(ref _pageWrites);
            Interlocked.Add(ref _bytesWritten, runLength);
        }
    }

    private enum BankKind : byte { Owner, Standby }

    private enum PendingAllocationKind : byte { Demand, Standby }

    private readonly record struct StandbyKey(
        ulong ArenaIdentity,
        ulong ArenaGeneration,
        int FrameSlot)
    {
        internal static StandbyKey From(in VulkanMaterialTablePreparedAuthority authority)
            => new(authority.ArenaIdentity, authority.ArenaGeneration, authority.FrameSlot);

        internal bool MatchesArena(in VulkanMaterialTablePreparedAuthority authority)
            => ArenaIdentity == authority.ArenaIdentity &&
               ArenaGeneration == authority.ArenaGeneration;
    }

    private readonly record struct StandbyRetryTarget(StandbyKey Key, ulong Capacity);

    private readonly record struct PendingAllocation(
        PendingAllocationKind Kind,
        VulkanMaterialTablePreparedAuthority Authority,
        ulong TableOwnerId,
        StandbyKey StandbyKey,
        ulong RequestedCapacity,
        Task<AllocationResult> Task)
    {
        internal static PendingAllocation ForDemand(
            in VulkanMaterialTablePreparedAuthority authority,
            ulong ownerId,
            Task<AllocationResult> task)
            => new(PendingAllocationKind.Demand, authority, ownerId, default, 0UL, task);

        internal static PendingAllocation ForStandby(
            in VulkanMaterialTablePreparedAuthority authority,
            ulong capacity,
            Task<AllocationResult> task)
            => new(
                PendingAllocationKind.Standby,
                authority,
                0UL,
                VulkanMaterialTablePreparedMap.StandbyKey.From(in authority),
                capacity,
                task);
    }

    private readonly record struct AllocationResult(bool Success, Buffer Buffer, DeviceMemory Memory,
        ulong Capacity, string Error);

    private sealed class Bank
    {
        internal BankKind Kind;
        internal StandbyKey StandbyKey;
        internal Buffer Buffer;
        internal DeviceMemory Memory;
        internal ulong Capacity;
        internal ulong Range;
        internal ulong NativeGeneration;
        internal ulong TableOwnerId;
        internal ulong ArenaIdentity;
        internal ulong ArenaGeneration;
        internal int FrameSlot = -1;
        internal ulong ResetEpoch;
        internal ulong PublicationGeneration;
        internal ulong DescriptorClosureGeneration;
        internal ulong[] PageTokens = [];
        internal byte[] Shadow = [];
        internal byte[] PageScratch = [];
        internal bool HasShadow;
        internal bool Retired;

        internal bool Matches(in VulkanMaterialTablePreparedAuthority authority, GPUMaterialTablePublication publication)
            => IsReserved(in authority) && TableOwnerId == publication.OwnerId &&
                PublicationGeneration == publication.Generation &&
                DescriptorClosureGeneration == publication.DescriptorClosureGeneration;

        internal bool IsReserved(in VulkanMaterialTablePreparedAuthority authority)
            => ArenaIdentity == authority.ArenaIdentity && ArenaGeneration == authority.ArenaGeneration &&
                FrameSlot == authority.FrameSlot && ResetEpoch == authority.ResetEpoch;

        internal bool IsReusableBy(in VulkanMaterialTablePreparedAuthority authority)
            => ArenaIdentity == authority.ArenaIdentity && ArenaGeneration == authority.ArenaGeneration &&
                FrameSlot == authority.FrameSlot && ResetEpoch != authority.ResetEpoch;

        internal void EnsureDeltaScratch()
        {
            int capacity = checked((int)Capacity);
            if (Shadow.Length != capacity)
            {
                Shadow = GC.AllocateUninitializedArray<byte>(capacity);
                HasShadow = false;
            }
            if (PageScratch.Length != PublicationPageBytes)
                PageScratch = GC.AllocateUninitializedArray<byte>(PublicationPageBytes);
        }

        internal void Reserve(in VulkanMaterialTablePreparedAuthority authority, ulong ownerId, in AllocationResult allocation)
        {
            Kind = BankKind.Owner;
            StandbyKey = default;
            Buffer = allocation.Buffer;
            Memory = allocation.Memory;
            Capacity = allocation.Capacity;
            ArenaIdentity = authority.ArenaIdentity;
            ArenaGeneration = authority.ArenaGeneration;
            FrameSlot = authority.FrameSlot;
            ResetEpoch = authority.ResetEpoch;
            TableOwnerId = ownerId;
            PageTokens = [];
            Shadow = [];
            PageScratch = [];
            HasShadow = false;
        }

        internal void ReserveStandby(in StandbyKey key, in AllocationResult allocation)
        {
            Kind = BankKind.Standby;
            StandbyKey = key;
            Buffer = allocation.Buffer;
            Memory = allocation.Memory;
            Capacity = allocation.Capacity;
            ArenaIdentity = key.ArenaIdentity;
            ArenaGeneration = key.ArenaGeneration;
            FrameSlot = key.FrameSlot;
            ResetEpoch = 0UL;
            TableOwnerId = 0UL;
            Range = 0UL;
            NativeGeneration = 0UL;
            PublicationGeneration = 0UL;
            DescriptorClosureGeneration = 0UL;
            PageTokens = [];
            Shadow = [];
            PageScratch = [];
            HasShadow = false;
        }

        internal void ClaimStandby(
            in VulkanMaterialTablePreparedAuthority authority,
            ulong ownerId)
        {
            if (Kind != BankKind.Standby ||
                StandbyKey != VulkanMaterialTablePreparedMap.StandbyKey.From(in authority))
                throw new InvalidOperationException("A material-table standby belongs to a different frame arena slot.");

            Kind = BankKind.Owner;
            StandbyKey = default;
            ArenaIdentity = authority.ArenaIdentity;
            ArenaGeneration = authority.ArenaGeneration;
            FrameSlot = authority.FrameSlot;
            ResetEpoch = authority.ResetEpoch;
            TableOwnerId = ownerId;
            Range = 0UL;
            NativeGeneration = 0UL;
            PublicationGeneration = 0UL;
            DescriptorClosureGeneration = 0UL;
            PageTokens = [];
            Shadow = [];
            PageScratch = [];
            HasShadow = false;
        }

        internal void Assign(in VulkanMaterialTablePreparedAuthority authority,
            GPUMaterialTablePublication publication, ulong range, ulong nativeGeneration)
        {
            if (nativeGeneration == 0)
                throw new InvalidOperationException("The material-table backing was not published by Vulkan resource tracking.");
            ArenaIdentity = authority.ArenaIdentity;
            ArenaGeneration = authority.ArenaGeneration;
            FrameSlot = authority.FrameSlot;
            ResetEpoch = authority.ResetEpoch;
            TableOwnerId = publication.OwnerId;
            PublicationGeneration = publication.Generation;
            DescriptorClosureGeneration = publication.DescriptorClosureGeneration;
            Range = range;
            NativeGeneration = nativeGeneration;
        }
    }

    /// <summary>Persistent queue ownership makes shutdown wait for every native allocation call.</summary>
    private sealed class NativeAllocationWorker : IDisposable
    {
        private readonly ConcurrentQueue<WorkItem> _queue = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly Thread _thread;
        private int _shutdown;

        internal NativeAllocationWorker()
        {
            _thread = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = "XRE Vulkan Material Table Allocation",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }

        internal Task<T> Enqueue<T>(Func<T> action)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _shutdown) != 0, this);
            WorkItem<T> item = new(action);
            _queue.Enqueue(item);
            _available.Release();
            return item.Completion.Task;
        }

        private void WorkerMain()
        {
            while (true)
            {
                _available.Wait();
                if (_queue.TryDequeue(out WorkItem? item))
                {
                    item.Execute();
                    continue;
                }
                if (Volatile.Read(ref _shutdown) != 0)
                    return;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _shutdown, 1) != 0)
                return;
            _available.Release();
            _thread.Join();
            _available.Dispose();
        }

        private abstract class WorkItem { internal abstract void Execute(); }

        private sealed class WorkItem<T>(Func<T> action) : WorkItem
        {
            internal TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override void Execute()
            {
                try { Completion.TrySetResult(action()); }
                catch (Exception exception) { Completion.TrySetException(exception); }
            }
        }
    }
}

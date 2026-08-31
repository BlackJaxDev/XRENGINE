using System.Collections.Concurrent;
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
    private const int MaximumDiagnosticReadBytes = 16 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly List<Bank> _banks = [];
    private readonly List<PendingAllocation> _pending = [];
    // The resource runtime exists before frame arenas. Do not start a native worker until an
    // allocation is actually admitted, otherwise an early initialization failure leaks a thread.
    private NativeAllocationWorker? _allocationWorker;
    private long _nativeAllocations;
    private long _pageWrites;
    private long _bytesWritten;
    private long _reuses;
    private long _growthPending;
    private long _emergencyWaits;
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
            return new(
                Volatile.Read(ref _nativeAllocations), Volatile.Read(ref _pageWrites),
                Volatile.Read(ref _bytesWritten), Volatile.Read(ref _reuses),
                Volatile.Read(ref _growthPending), Volatile.Read(ref _emergencyWaits),
                _banks.Count, _pending.Count);
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
            DrainCompletedAllocations(buffers);
            Bank? bank = FindExact(in authority, publication);
            if (bank is not null)
            {
                disposition = EVulkanMaterialTablePreparedDisposition.Ready;
                reason = string.Empty;
                return true;
            }

            bank = FindReusable(in authority, publication.OwnerId, requiredBytes);
            if (bank is null)
            {
                if (!TryQueueOrPublishAllocation(in authority, context, buffers, publication.OwnerId,
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
                bool queued = QueueAllocation(in authority, context, ownerId, requiredBytes, out _, out isPending, out reason);
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

        return QueueAllocation(in authority, context, ownerId, requiredBytes, out bank, out isPending, out reason);
    }

    private bool QueueAllocation(in VulkanMaterialTablePreparedAuthority authority,
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

        ulong capacity = CalculateCapacity(requiredBytes);
        try
        {
            NativeAllocationWorker worker = _allocationWorker ??= new NativeAllocationWorker();
            Task<AllocationResult> task = worker.Enqueue(() => Allocate(context, capacity));
            _pending.Add(new PendingAllocation(authority, ownerId, task));
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

    private void DrainCompletedAllocations(VulkanBufferResourceService buffers)
    {
        for (int index = _pending.Count - 1; index >= 0; --index)
        {
            PendingAllocation pending = _pending[index];
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
            if (ReferenceEquals(candidate, replacement) || candidate.Retired || candidate.TableOwnerId != ownerId ||
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
            if (!bank.Retired && bank.Matches(in authority, publication))
                return bank;
        return null;
    }

    private Bank? FindPublication(VulkanBackendObjectContext context, GPUMaterialTablePublication publication)
    {
        foreach (Bank bank in _banks)
            if (!bank.Retired && bank.NativeGeneration != 0 &&
                context.Resources.GetPublishedGeneration(ObjectType.Buffer, bank.Buffer.Handle) == bank.NativeGeneration &&
                bank.TableOwnerId == publication.OwnerId && bank.PublicationGeneration == publication.Generation &&
                bank.DescriptorClosureGeneration == publication.DescriptorClosureGeneration)
                return bank;
        return null;
    }

    private Bank? FindReusable(in VulkanMaterialTablePreparedAuthority authority, ulong ownerId, ulong requiredBytes)
    {
        foreach (Bank bank in _banks)
            if (!bank.Retired && bank.TableOwnerId == ownerId && bank.IsReusableBy(in authority) &&
                bank.Capacity >= requiredBytes)
                return bank;
        return null;
    }

    private PendingAllocation? FindPending(in VulkanMaterialTablePreparedAuthority authority, ulong ownerId)
    {
        foreach (PendingAllocation pending in _pending)
            if (pending.TableOwnerId == ownerId && pending.Authority.ArenaIdentity == authority.ArenaIdentity &&
                pending.Authority.ArenaGeneration == authority.ArenaGeneration &&
                pending.Authority.FrameSlot == authority.FrameSlot)
                return pending;
        return null;
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

    private static ulong CalculateCapacity(ulong requiredBytes)
    {
        ulong capacity = InitialCapacity;
        while (capacity < requiredBytes)
            capacity = checked(capacity * 2UL);
        return capacity;
    }

    private readonly record struct PendingAllocation(VulkanMaterialTablePreparedAuthority Authority,
        ulong TableOwnerId, Task<AllocationResult> Task);

    private readonly record struct AllocationResult(bool Success, Buffer Buffer, DeviceMemory Memory,
        ulong Capacity, string Error);

    private sealed class Bank
    {
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

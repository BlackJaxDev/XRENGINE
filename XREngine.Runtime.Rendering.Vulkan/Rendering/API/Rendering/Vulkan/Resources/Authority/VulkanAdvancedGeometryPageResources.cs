using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using XREngine.Rendering.Commands;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the native geometry page pool buffer and the page table buffers of the Advanced scene.
/// The meshlet task and mesh stages read cluster data of a geometry with
/// <see cref="EAdvancedGeometryFlags.StreamedPages"/> from the pool slot that the page table names
/// (visibility bindings 73 and 74).
/// </summary>
/// <remarks>
/// <para>
/// The pool is one buffer of <see cref="AdvancedGeometryPagePool.SlotCount"/> slots. When the pool
/// version changes, <see cref="TryPrepare"/> compares the slot images of
/// <see cref="AdvancedGeometryPagePool"/> with the images that the buffer holds and writes each
/// changed slot. The neutral pool reuses a slot only after no publication in flight names it, so
/// these writes never change memory that a submitted frame reads.
/// </para>
/// <para>
/// Each page table version gets a table buffer. A table buffer is pinned by every frame slot that
/// binds it, like the resident geometry banks, and is written again for a new version only after
/// those frame slots complete. An empty pool or table binds a 16-byte sentinel: zero pool slots,
/// and four not-resident table entries.
/// </para>
/// <para>
/// The caller holds the lock of <see cref="VulkanAdvancedGeometryResidentCache"/> for every call.
/// </para>
/// </remarks>
internal sealed class VulkanAdvancedGeometryPageResources
{
    private const int TableCapacity = 8;
    private const ulong SentinelBytes = 16UL;
    private const ulong MinimumTableBytes = 4096UL;
    private const ulong IdentityMask = 3UL << 62;

    private readonly VulkanResourceRuntime _resources;
    private readonly TableEntry[] _tables = new TableEntry[TableCapacity];
    private Buffer _poolBuffer;
    private DeviceMemory _poolMemory;
    private ulong _poolBytes;
    private ulong _poolIdentity;
    private int _poolSlotCount;
    private AdvancedGeometryPagePool? _poolSource;
    private byte[]?[] _writtenImages = [];
    private byte[]?[] _capturedImages = [];
    private ulong _writtenVersion;
    private bool _poolWritten;
    private ulong _nextIdentity;

    internal VulkanAdvancedGeometryPageResources(VulkanResourceRuntime resources)
        => _resources = resources;

    /// <summary>Native bytes of the pool buffer and every table buffer.</summary>
    internal ulong ResidentBytes
    {
        get
        {
            ulong bytes = _poolBytes;
            for (int index = 0; index < _tables.Length; ++index)
                if (_tables[index].IsOccupied)
                    bytes += _tables[index].Capacity;
            return bytes;
        }
    }

    /// <summary>
    /// Writes changed pool slots, finds or writes the table buffer of the publication's page table
    /// version, and pins it for <paramref name="frameSlot"/>. Returns the pool and table slices
    /// that the visibility descriptor set binds.
    /// </summary>
    internal bool TryPrepare(
        int frameSlot,
        ulong databaseEpoch,
        AdvancedGeometryPublicationSnapshot snapshot,
        VulkanBackendObjectContext context,
        out VulkanFrameDataSlice pool,
        out VulkanFrameDataSlice table,
        out bool tableNewlyPinned,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        pool = default;
        table = default;
        tableNewlyPinned = false;
        if (!TryPreparePool(snapshot.PagePool, context, out failure, out reason))
            return false;

        pool = CreateSlice(_poolBuffer, _poolMemory, _poolIdentity, _poolIdentity,
            _poolSlotCount == 0 ? (uint)SentinelBytes : checked((uint)_poolBytes));
        if (!TryPrepareTable(frameSlot, databaseEpoch, snapshot, context, out table, out tableNewlyPinned, out failure, out reason))
            return false;

        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    /// <summary>Removes the pins of a completed frame slot.</summary>
    internal void ReleaseFrameSlot(int frameSlot)
    {
        ulong pin = 1UL << frameSlot;
        for (int index = 0; index < _tables.Length; ++index)
            _tables[index].FrameSlotPins &= ~pin;
    }

    /// <summary>Removes a pin that a failed publication added.</summary>
    internal void RollbackTablePin(int frameSlot, in VulkanFrameDataSlice table)
    {
        ulong pin = 1UL << frameSlot;
        for (int index = 0; index < _tables.Length; ++index)
        {
            ref TableEntry entry = ref _tables[index];
            if (!entry.IsOccupied || entry.Buffer.Handle != table.Buffer.Handle)
                continue;
            entry.FrameSlotPins &= ~pin;
            return;
        }
    }

    /// <summary>Queues the pool and every table buffer for lifetime-tracked retirement.</summary>
    internal void RetireAll()
    {
        RetirePool("AdvancedGeometry.PagePool.Shutdown");
        for (int index = 0; index < _tables.Length; ++index)
            RetireTable(ref _tables[index], "AdvancedGeometry.PageTable.Shutdown");
    }

    private bool TryPreparePool(
        AdvancedGeometryPagePool source,
        VulkanBackendObjectContext context,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        int slotCount = source.SlotCount;
        if (!ReferenceEquals(source, _poolSource) || slotCount != _poolSlotCount || _poolBuffer.Handle == 0)
        {
            // A new database, or the first configuration of the pool. The old buffer stays alive
            // until the frames that bind it complete.
            RetirePool("AdvancedGeometry.PagePool.Replaced");
            ulong bytes = slotCount == 0
                ? SentinelBytes
                : checked((ulong)slotCount * AdvancedGeometryPagePool.SlotBytes);
            if (!TryValidateStorageRange(context, bytes, out reason))
            {
                failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
                return false;
            }
            try
            {
                (_poolBuffer, _poolMemory) = CreateBuffer(context, bytes, "AdvancedGeometry.PagePool");
            }
            catch (Exception exception)
            {
                failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                reason = $"The geometry page pool could not allocate {bytes} bytes: {exception.Message}";
                return false;
            }
            _poolBytes = bytes;
            _poolIdentity = NextIdentity();
            _poolSlotCount = slotCount;
            _poolSource = source;
            _writtenImages = new byte[]?[slotCount];
            _capturedImages = new byte[]?[slotCount];
            _poolWritten = false;
            if (slotCount == 0)
            {
                Span<byte> zeros = stackalloc byte[(int)SentinelBytes];
                zeros.Clear();
                if (!TryWrite(context, _poolBuffer, _poolMemory, 0UL, zeros, out reason))
                {
                    failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                    return false;
                }
            }
            Debug.Vulkan(
                "[VulkanAdvancedGeometry] Allocated geometry page pool: slots={0}, bytes={1}.",
                slotCount,
                bytes);
        }

        if (slotCount == 0 || (_poolWritten && source.Version == _writtenVersion))
        {
            failure = EVulkanAdvancedSceneResourceFailure.None;
            reason = string.Empty;
            return true;
        }

        ulong version = source.CaptureSlotImages(_capturedImages);
        for (int slot = 0; slot < slotCount; ++slot)
        {
            byte[]? image = _capturedImages[slot];
            if (ReferenceEquals(image, _writtenImages[slot]))
                continue;
            if (image is not null &&
                !TryWrite(context, _poolBuffer, _poolMemory, (ulong)slot * AdvancedGeometryPagePool.SlotBytes, image, out reason))
            {
                failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                return false;
            }
            _writtenImages[slot] = image;
        }
        // Drop the image references, so freed pages are not kept alive by this copy.
        Array.Clear(_capturedImages);
        _writtenVersion = version;
        _poolWritten = true;
        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = string.Empty;
        return true;
    }

    private bool TryPrepareTable(
        int frameSlot,
        ulong databaseEpoch,
        AdvancedGeometryPublicationSnapshot snapshot,
        VulkanBackendObjectContext context,
        out VulkanFrameDataSlice slice,
        out bool newlyPinned,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        slice = default;
        newlyPinned = false;
        ulong pin = 1UL << frameSlot;
        ulong version = snapshot.PageTableVersion;
        ReadOnlySpan<uint> entries = snapshot.PageTable;
        ulong tableBytes = Math.Max((ulong)entries.Length * sizeof(uint), SentinelBytes);

        for (int index = 0; index < _tables.Length; ++index)
        {
            ref TableEntry entry = ref _tables[index];
            if (!entry.IsOccupied || entry.DatabaseEpoch != databaseEpoch ||
                !ReferenceEquals(entry.Source, snapshot.PagePool) || entry.Version != version ||
                entry.Length != entries.Length)
                continue;
            newlyPinned = (entry.FrameSlotPins & pin) == 0u;
            entry.FrameSlotPins |= pin;
            slice = entry.CreateSlice(tableBytes);
            failure = EVulkanAdvancedSceneResourceFailure.None;
            reason = string.Empty;
            return true;
        }

        if (!TryValidateStorageRange(context, tableBytes, out reason))
        {
            failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            return false;
        }

        // Write the smallest unpinned buffer that fits, or create one in an empty or unpinned entry.
        int target = -1;
        for (int index = 0; index < _tables.Length; ++index)
        {
            ref TableEntry entry = ref _tables[index];
            if (!entry.IsOccupied || entry.FrameSlotPins != 0u || entry.Capacity < tableBytes)
                continue;
            if (target < 0 || entry.Capacity < _tables[target].Capacity)
                target = index;
        }
        if (target < 0)
        {
            for (int index = 0; index < _tables.Length && target < 0; ++index)
                if (!_tables[index].IsOccupied)
                    target = index;
            for (int index = 0; index < _tables.Length && target < 0; ++index)
                if (_tables[index].FrameSlotPins == 0u)
                    target = index;
            if (target < 0)
            {
                failure = EVulkanAdvancedSceneResourceFailure.FrameSlotStillInUse;
                reason = $"Every one of the {TableCapacity} geometry page table buffers is pinned by a frame slot in flight; retry after a frame slot completes.";
                return false;
            }

            ref TableEntry replaced = ref _tables[target];
            RetireTable(ref replaced, "AdvancedGeometry.PageTable.Replaced");
            ulong capacity = Math.Max(MinimumTableBytes, BitOperations.RoundUpToPowerOf2(tableBytes));
            try
            {
                (Buffer buffer, DeviceMemory memory) = CreateBuffer(context, capacity, "AdvancedGeometry.PageTable");
                replaced = new TableEntry
                {
                    Buffer = buffer,
                    Memory = memory,
                    Capacity = capacity,
                    Identity = NextIdentity(),
                };
            }
            catch (Exception exception)
            {
                failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                reason = $"The geometry page table could not allocate {capacity} bytes: {exception.Message}";
                return false;
            }
        }

        ref TableEntry written = ref _tables[target];
        if (!TryWriteTable(context, in written, entries, tableBytes, out reason))
        {
            RetireTable(ref written, "AdvancedGeometry.PageTable.UploadFailure");
            failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
            return false;
        }
        written.DatabaseEpoch = databaseEpoch;
        written.Source = snapshot.PagePool;
        written.Version = version;
        written.Length = entries.Length;
        // A new generation makes the slice differ from slices of the earlier contents.
        written.Generation = NextIdentity();
        written.FrameSlotPins = pin;
        newlyPinned = true;
        slice = written.CreateSlice(tableBytes);
        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Writes the table entries, and pads a table shorter than the sentinel with not-resident
    /// entries, so a shader that reads the padding finds no slot.
    /// </summary>
    private bool TryWriteTable(
        VulkanBackendObjectContext context,
        in TableEntry entry,
        ReadOnlySpan<uint> entries,
        ulong tableBytes,
        out string reason)
    {
        if (!_resources.Buffers.TryCreateMappedSlice(
                context, entry.Buffer, entry.Memory, 0UL, tableBytes, out VulkanMappedMemorySlice mapped) ||
            !_resources.Buffers.TryAcquireWrite(context, in mapped, out VulkanMappedMemoryWriteLease lease))
        {
            reason = "The geometry page table buffer could not acquire a writable mapped range.";
            return false;
        }
        using (lease)
        {
            Span<uint> destination = MemoryMarshal.Cast<byte, uint>(lease.Bytes);
            entries.CopyTo(destination);
            destination[entries.Length..].Fill(AdvancedGeometryPagePool.NotResident);
        }
        reason = string.Empty;
        return true;
    }

    private bool TryWrite(
        VulkanBackendObjectContext context,
        Buffer buffer,
        DeviceMemory memory,
        ulong offset,
        ReadOnlySpan<byte> source,
        out string reason)
    {
        if (!_resources.Buffers.TryCreateMappedSlice(
                context, buffer, memory, offset, (ulong)source.Length, out VulkanMappedMemorySlice mapped) ||
            !_resources.Buffers.TryAcquireWrite(context, in mapped, out VulkanMappedMemoryWriteLease lease))
        {
            reason = "The geometry page pool buffer could not acquire a writable mapped range.";
            return false;
        }
        using (lease)
            source.CopyTo(lease.Bytes);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Creates a buffer that the CPU writes and the GPU reads. It prefers device-local
    /// host-visible memory (resizable BAR), and falls back to system memory with a warning,
    /// because GPU reads then cross PCIe.
    /// </summary>
    private (Buffer Buffer, DeviceMemory Memory) CreateBuffer(VulkanBackendObjectContext context, ulong bytes, string owner)
    {
        const BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit;
        try
        {
            return _resources.Buffers.CreateDedicatedRaw(
                context,
                bytes,
                usage,
                MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                owner: owner);
        }
        catch (VulkanOutOfMemoryException exception)
        {
            Debug.VulkanWarningEvery(
                "AdvancedGeometry.PageResources.DeviceLocalFallback",
                TimeSpan.FromSeconds(10),
                "[Vulkan] The geometry page resources could not place a {0}-byte allocation in device-local host-visible memory ({1}). They use system memory, so GPU page reads cross PCIe.",
                bytes,
                exception.Message);
        }

        return _resources.Buffers.CreateDedicatedRaw(
            context,
            bytes,
            usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            owner: owner);
    }

    private static bool TryValidateStorageRange(VulkanBackendObjectContext context, ulong bytes, out string reason)
    {
        ulong reported = context.DeviceContext.PhysicalDeviceCapabilities?.Properties.Limits.MaxStorageBufferRange ?? 0UL;
        ulong maximum = reported == 0UL ? uint.MaxValue : Math.Min(reported, uint.MaxValue);
        if (bytes <= maximum)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"The geometry page resources request a {bytes}-byte storage range, exceeding this device's {maximum}-byte storage-buffer range. Lower AdvancedGeometryPagePoolMiB.";
        return false;
    }

    private void RetirePool(string owner)
    {
        if (_poolBuffer.Handle != 0 || _poolMemory.Handle != 0)
            _resources.Buffers.Retire(_poolBuffer, _poolMemory, owner);
        _poolBuffer = default;
        _poolMemory = default;
        _poolBytes = 0UL;
        _poolIdentity = 0UL;
        _poolSlotCount = 0;
        _poolSource = null;
        _writtenImages = [];
        _capturedImages = [];
        _poolWritten = false;
    }

    private void RetireTable(ref TableEntry entry, string owner)
    {
        if (entry.IsOccupied)
            _resources.Buffers.Retire(entry.Buffer, entry.Memory, owner);
        entry = default;
    }

    private ulong NextIdentity()
    {
        _nextIdentity = _nextIdentity >= (1UL << 62) - 1UL ? 1UL : _nextIdentity + 1UL;
        return IdentityMask | _nextIdentity;
    }

    private static VulkanFrameDataSlice CreateSlice(Buffer buffer, DeviceMemory memory, ulong identity, ulong generation, uint length)
        => new(
            identity,
            buffer.Handle,
            memory.Handle,
            EVulkanFrameDataLane.AdvancedSceneStorage,
            0,
            0,
            0UL,
            length,
            16u,
            generation,
            buffer,
            memory);

    private struct TableEntry
    {
        internal Buffer Buffer;
        internal DeviceMemory Memory;
        internal ulong Capacity;
        internal ulong Identity;
        internal ulong Generation;
        internal ulong DatabaseEpoch;
        internal AdvancedGeometryPagePool? Source;
        internal ulong Version;
        internal int Length;
        internal ulong FrameSlotPins;

        internal readonly bool IsOccupied => Buffer.Handle != 0 || Memory.Handle != 0;

        internal readonly VulkanFrameDataSlice CreateSlice(ulong bytes)
            => VulkanAdvancedGeometryPageResources.CreateSlice(Buffer, Memory, Identity, Generation, checked((uint)bytes));
    }
}

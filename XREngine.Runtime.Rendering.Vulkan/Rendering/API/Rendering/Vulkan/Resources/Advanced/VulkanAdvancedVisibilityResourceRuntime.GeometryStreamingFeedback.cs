using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Meshlets;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the geometry page streaming feedback buffers of set 1 (bindings 75 and 76).
/// </summary>
/// <remarks>
/// <para>
/// With geometry streaming on, each frame slot has two mapped buffers. The request buffer holds a
/// 16-byte header (the request count and the overflow count) and the request entries
/// (<see cref="MeshletPageRequest"/>). The feedback-bit buffer holds one touch bit for each pool
/// slot, rounded up to whole words, and then one request bit for each page table entry. The
/// meshlet task stage sets the touch bit of each resident slot that the cut uses, and writes one
/// request for each missing page of the cut (the request bit removes duplicates in one frame).
/// </para>
/// <para>
/// The primary command buffer ends with a shader-write to host-read barrier. When
/// <see cref="TryPrepare"/> runs for a frame slot again, the slot's earlier submission is complete,
/// so this runtime reads the requests and the touch words of that frame, adds them to the
/// streaming feedback queue of the scene, and clears the words that the frame could write. The
/// publisher drains the queue before its next residency update.
/// </para>
/// <para>
/// With streaming off, both bindings use one 16-byte sentinel buffer. A request buffer without
/// entries turns the feedback writes of the task stage off.
/// </para>
/// </remarks>
internal sealed partial class VulkanAdvancedVisibilityResourceRuntime
{
    private const ulong GeometryFeedbackSentinelBytes = 16UL;
    private const int GeometryPageRequestHeaderBytes = 16;

    /// <summary>
    /// The largest page table that the request bits cover: 2^20 pages, 128 GiB of page images. A
    /// page past this limit is never requested; the cut draws its coarser resident level.
    /// </summary>
    internal const uint MaxStreamedPageTableEntries = 1u << 20;

    private const uint MaxGeometryFeedbackTouchWords =
        (AdvancedGeometryPagePool.MaximumSlotCount + 31u) / 32u;
    private const uint MaxGeometryFeedbackRequestWords = (MaxStreamedPageTableEntries + 31u) / 32u;

    private GeometryFeedbackSlot[] _geometryFeedbackSlots = [];
    private VkBufferHandle _geometryFeedbackSentinelBuffer;
    private DeviceMemory _geometryFeedbackSentinelMemory;
    private int _geometryPageRequestCapacity;
    private bool _geometryFeedbackTableLimitWarned;
    // The Stopwatch timestamp after which the next [GeometryStreamingFeedback] line is written.
    private long _nextGeometryFeedbackLogTimestamp;
    private long _geometryFeedbackReadFailures;

    private string GeometryFeedbackOwner => $"AdvancedVisibility.OutputBank{_outputBankId}.GeometryPageFeedback";

    /// <summary>The feedback buffers of one frame slot and the state of the frame that last bound them.</summary>
    private struct GeometryFeedbackSlot
    {
        public VkBufferHandle RequestBuffer;
        public DeviceMemory RequestMemory;
        public ulong RequestBytes;
        public VkBufferHandle BitsBuffer;
        public DeviceMemory BitsMemory;
        public ulong BitsBytes;
        // True when a prepared frame bound the buffers and its feedback was not read yet.
        public bool Pending;
        public int PendingTouchWords;
        public int PendingBitWords;
        public long PendingFrame;
        public MeshletPageFeedbackQueue? PendingQueue;
    }

    /// <summary>
    /// Creates the sentinel buffer and, with geometry streaming on, the per-slot request and
    /// feedback-bit buffers. The buffers prefer device-local host-visible memory, so the task stage
    /// atomics stay on the device, and fall back to host memory.
    /// </summary>
    private bool TryCreateGeometryFeedbackStorage(out string reason)
    {
        VulkanBackendObjectContext? context = _resources.BackendObjectContext;
        if (context is null || !context.IsDeviceOperational)
        {
            reason = "The Vulkan backend context is unavailable for geometry page feedback allocation.";
            return false;
        }

        try
        {
            (_geometryFeedbackSentinelBuffer, _geometryFeedbackSentinelMemory) = _resources.Buffers.CreateDedicatedRaw(
                context,
                GeometryFeedbackSentinelBytes,
                BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.DeviceLocalBit,
                owner: GeometryFeedbackOwner);
        }
        catch (Exception exception)
        {
            reason = $"The geometry page feedback sentinel buffer could not be created: {exception.Message}";
            return false;
        }

        _geometryFeedbackSlots = new GeometryFeedbackSlot[_states.Length];
        _geometryPageRequestCapacity = AdvancedVirtualGeometryPolicy.GeometryPageRequestCapacity;
        if (_geometryPageRequestCapacity <= 0)
        {
            reason = "Ready";
            return true;
        }

        ulong requestBytes = (ulong)GeometryPageRequestHeaderBytes + (ulong)_geometryPageRequestCapacity * (ulong)MeshletPageRequest.SizeInBytes;
        ulong bitsBytes = (ulong)(MaxGeometryFeedbackTouchWords + MaxGeometryFeedbackRequestWords) * sizeof(uint);
        for (int index = 0; index < _geometryFeedbackSlots.Length; ++index)
        {
            ref GeometryFeedbackSlot slot = ref _geometryFeedbackSlots[index];
            if (!TryCreateGeometryFeedbackBuffer(context, requestBytes, out slot.RequestBuffer, out slot.RequestMemory, out reason) ||
                !TryCreateGeometryFeedbackBuffer(context, bitsBytes, out slot.BitsBuffer, out slot.BitsMemory, out reason))
                return false;
            slot.RequestBytes = requestBytes;
            slot.BitsBytes = bitsBytes;
        }

        reason = "Ready";
        return true;
    }

    private bool TryCreateGeometryFeedbackBuffer(
        VulkanBackendObjectContext context,
        ulong bytes,
        out VkBufferHandle buffer,
        out DeviceMemory memory,
        out string reason)
    {
        const BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit;
        try
        {
            (buffer, memory) = _resources.Buffers.CreateDedicatedRaw(
                context,
                bytes,
                usage,
                MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                owner: GeometryFeedbackOwner);
        }
        catch (InvalidOperationException)
        {
            try
            {
                (buffer, memory) = _resources.Buffers.CreateDedicatedRaw(
                    context,
                    bytes,
                    usage,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    owner: GeometryFeedbackOwner);
            }
            catch (Exception exception)
            {
                buffer = default;
                memory = default;
                reason = $"A geometry page feedback buffer could not be created: {exception.Message}";
                return false;
            }
        }

        if (!TryClearGeometryFeedbackBytes(context, buffer, memory, 0UL, bytes))
        {
            reason = "A geometry page feedback buffer could not be mapped for initialization.";
            return false;
        }
        reason = "Ready";
        return true;
    }

    /// <summary>
    /// Reads the feedback of the frame that last used a frame slot's buffers, adds it to that
    /// frame's scene queue, and clears the request count and the bit words that the frame could
    /// write. Call it only when the slot's earlier submission is complete, before the new frame
    /// binds the buffers. A slot without pending feedback does nothing.
    /// </summary>
    private void CollectGeometryFeedbackNoLock(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)_geometryFeedbackSlots.Length)
            return;
        ref GeometryFeedbackSlot slot = ref _geometryFeedbackSlots[frameSlot];
        if (!slot.Pending)
            return;
        slot.Pending = false;
        MeshletPageFeedbackQueue? queue = slot.PendingQueue;
        slot.PendingQueue = null;
        VulkanBackendObjectContext? context = _resources.BackendObjectContext;
        if (context is null || !context.IsDeviceOperational)
            return;

        int touchWordCount = slot.PendingTouchWords;
        int bitWordCount = slot.PendingBitWords;
        if (queue is not null &&
            _resources.Buffers.TryCreateMappedSlice(context, slot.RequestBuffer, slot.RequestMemory, 0UL, slot.RequestBytes, out VulkanMappedMemorySlice requestSlice) &&
            _resources.Buffers.TryCreateMappedSlice(context, slot.BitsBuffer, slot.BitsMemory, 0UL, (ulong)touchWordCount * sizeof(uint), out VulkanMappedMemorySlice touchSlice) &&
            _resources.Buffers.TryAcquireRead(context, in requestSlice, out VulkanMappedMemoryReadLease requestLease))
        {
            using (requestLease)
            {
                ReadOnlySpan<uint> header = MemoryMarshal.Cast<byte, uint>(requestLease.Bytes[..GeometryPageRequestHeaderBytes]);
                // The count includes the requests that overflowed; only the first capacity entries were written.
                int written = (int)Math.Min(header[0], (uint)_geometryPageRequestCapacity);
                long overflow = header[1];
                ReadOnlySpan<MeshletPageRequest> requests = MemoryMarshal.Cast<byte, MeshletPageRequest>(
                    requestLease.Bytes.Slice(GeometryPageRequestHeaderBytes, written * MeshletPageRequest.SizeInBytes));
                if (_resources.Buffers.TryAcquireRead(context, in touchSlice, out VulkanMappedMemoryReadLease touchLease))
                {
                    using (touchLease)
                    {
                        ReadOnlySpan<uint> touchWords = MemoryMarshal.Cast<byte, uint>(touchLease.Bytes)[..touchWordCount];
                        queue.Enqueue(requests, overflow, touchWords, slot.PendingFrame);
                        LogGeometryFeedbackDiagnostics(frameSlot, header, touchWords, bitWordCount);
                    }
                }
                else
                    ++_geometryFeedbackReadFailures;
            }
        }
        else if (queue is not null)
            ++_geometryFeedbackReadFailures;

        TryClearGeometryFeedbackBytes(context, slot.RequestBuffer, slot.RequestMemory, 0UL, (ulong)GeometryPageRequestHeaderBytes);
        if (bitWordCount > 0)
            TryClearGeometryFeedbackBytes(context, slot.BitsBuffer, slot.BitsMemory, 0UL, (ulong)bitWordCount * sizeof(uint));
    }

    /// <summary>
    /// Writes one <c>[GeometryStreamingFeedback]</c> rendering log line every two seconds with the
    /// raw feedback of one frame: the request count and overflow from the request header, the set
    /// touch bits, the bit words that the frame could write, and the feedback reads that failed.
    /// The time test runs first, so the frames between two lines build no string.
    /// </summary>
    private void LogGeometryFeedbackDiagnostics(
        int frameSlot,
        ReadOnlySpan<uint> header,
        ReadOnlySpan<uint> touchWords,
        int bitWordCount)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextGeometryFeedbackLogTimestamp)
            return;
        _nextGeometryFeedbackLogTimestamp = now + 2L * System.Diagnostics.Stopwatch.Frequency;

        int touchedSlots = 0;
        foreach (uint word in touchWords)
            touchedSlots += System.Numerics.BitOperations.PopCount(word);
        Debug.Rendering(
            $"[GeometryStreamingFeedback] slot={frameSlot} requestCount={header[0]} overflow={header[1]} " +
            $"touchedSlots={touchedSlots} " +
            $"touchWords={touchWords.Length} bitWords={bitWordCount} capacity={_geometryPageRequestCapacity} readFailures={_geometryFeedbackReadFailures}");
    }

    /// <summary>
    /// Returns the request and feedback-bit ranges that a new frame of <paramref name="frameSlot"/>
    /// binds, and the word counts that the frame can write. The bit range covers the touch words of
    /// the bound pool and the request words of the bound page table. With streaming off, or for
    /// geometry without a scene queue, both ranges use the sentinel buffer.
    /// </summary>
    private void ResolveGeometryFeedbackBinding(
        int frameSlot,
        in VulkanAdvancedVisibilityGeometrySlices geometry,
        out VkBufferHandle requests,
        out ulong requestBytes,
        out VkBufferHandle bits,
        out ulong bitBytes,
        out int touchWordCount,
        out int bitWordCount)
    {
        requests = _geometryFeedbackSentinelBuffer;
        requestBytes = GeometryFeedbackSentinelBytes;
        bits = _geometryFeedbackSentinelBuffer;
        bitBytes = GeometryFeedbackSentinelBytes;
        touchWordCount = 0;
        bitWordCount = 0;
        if (_geometryPageRequestCapacity <= 0 || geometry.StreamingFeedback is null ||
            (uint)frameSlot >= (uint)_geometryFeedbackSlots.Length)
            return;

        ref GeometryFeedbackSlot slot = ref _geometryFeedbackSlots[frameSlot];
        if (slot.RequestBuffer.Handle == 0 || slot.BitsBuffer.Handle == 0)
            return;

        // The pool slice is the sentinel until the first paged geometry configures the pool, and the
        // table slice is the sentinel while the table is empty. Both then give no words.
        ulong slotCount = geometry.GeometryPagePool.Length / AdvancedGeometryPagePool.SlotBytes;
        ulong tableEntries = geometry.GeometryPageTable.Length / sizeof(uint);
        if (slotCount == 0UL || tableEntries == 0UL)
            return;
        if (tableEntries > MaxStreamedPageTableEntries)
        {
            if (!_geometryFeedbackTableLimitWarned)
            {
                _geometryFeedbackTableLimitWarned = true;
                Debug.RenderingWarning(
                    $"The geometry page table has {tableEntries} entries; page requests cover only the first {MaxStreamedPageTableEntries}.");
            }
            tableEntries = MaxStreamedPageTableEntries;
        }

        touchWordCount = (int)Math.Min((slotCount + 31UL) / 32UL, MaxGeometryFeedbackTouchWords);
        bitWordCount = touchWordCount + (int)((tableEntries + 31UL) / 32UL);
        requests = slot.RequestBuffer;
        requestBytes = slot.RequestBytes;
        bits = slot.BitsBuffer;
        bitBytes = (ulong)bitWordCount * sizeof(uint);
    }

    /// <summary>
    /// Records that the frame prepared for <paramref name="frameSlot"/> binds the slot's feedback
    /// buffers, so the next preparation of the slot reads them. A frame that binds the sentinel
    /// records nothing.
    /// </summary>
    private void MarkGeometryFeedbackPendingNoLock(
        int frameSlot,
        in VulkanAdvancedVisibilityGeometrySlices geometry,
        int touchWordCount,
        int bitWordCount)
    {
        if (bitWordCount <= 0 || (uint)frameSlot >= (uint)_geometryFeedbackSlots.Length)
            return;
        ref GeometryFeedbackSlot slot = ref _geometryFeedbackSlots[frameSlot];
        slot.Pending = true;
        slot.PendingTouchWords = touchWordCount;
        slot.PendingBitWords = bitWordCount;
        slot.PendingFrame = (long)RuntimeEngine.Rendering.State.RenderFrameId;
        slot.PendingQueue = geometry.StreamingFeedback;
    }

    /// <summary>
    /// Drops the pending feedback of every slot and clears the buffers, for a new output owner.
    /// </summary>
    private void ResetGeometryFeedbackNoLock()
    {
        VulkanBackendObjectContext? context = _resources.BackendObjectContext;
        for (int index = 0; index < _geometryFeedbackSlots.Length; ++index)
        {
            ref GeometryFeedbackSlot slot = ref _geometryFeedbackSlots[index];
            slot.Pending = false;
            slot.PendingQueue = null;
            if (context is null || !context.IsDeviceOperational || slot.RequestBuffer.Handle == 0)
                continue;
            TryClearGeometryFeedbackBytes(context, slot.RequestBuffer, slot.RequestMemory, 0UL, slot.RequestBytes);
            TryClearGeometryFeedbackBytes(context, slot.BitsBuffer, slot.BitsMemory, 0UL, slot.BitsBytes);
        }
    }

    private void RetireGeometryFeedbackStorageNoLock()
    {
        if (_resources.BackendObjectContext is { } context)
        {
            for (int index = 0; index < _geometryFeedbackSlots.Length; ++index)
            {
                ref GeometryFeedbackSlot slot = ref _geometryFeedbackSlots[index];
                if (slot.RequestBuffer.Handle != 0)
                    _resources.Buffers.Destroy(context, slot.RequestBuffer, slot.RequestMemory, GeometryFeedbackOwner);
                if (slot.BitsBuffer.Handle != 0)
                    _resources.Buffers.Destroy(context, slot.BitsBuffer, slot.BitsMemory, GeometryFeedbackOwner);
            }
            if (_geometryFeedbackSentinelBuffer.Handle != 0)
                _resources.Buffers.Destroy(context, _geometryFeedbackSentinelBuffer, _geometryFeedbackSentinelMemory, GeometryFeedbackOwner);
        }
        _geometryFeedbackSlots = [];
        _geometryFeedbackSentinelBuffer = default;
        _geometryFeedbackSentinelMemory = default;
        _geometryPageRequestCapacity = 0;
    }

    private bool TryClearGeometryFeedbackBytes(
        VulkanBackendObjectContext context,
        VkBufferHandle buffer,
        DeviceMemory memory,
        ulong offset,
        ulong length)
    {
        if (length == 0UL ||
            !_resources.Buffers.TryCreateMappedSlice(context, buffer, memory, offset, length, out VulkanMappedMemorySlice slice) ||
            !_resources.Buffers.TryAcquireWrite(context, in slice, out VulkanMappedMemoryWriteLease lease))
            return false;
        using (lease)
            lease.Bytes.Clear();
        return true;
    }
}

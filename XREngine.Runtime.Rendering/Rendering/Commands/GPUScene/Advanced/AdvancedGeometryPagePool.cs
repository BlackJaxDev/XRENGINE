using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Commands;

/// <summary>
/// The backend-neutral state of the geometry page pool: a fixed number of pool slots of
/// <see cref="MeshletPageFormat.PageBytes"/> each, and the page table that maps each registered
/// page to its slot. A geometry that draws from geometry pages reserves a contiguous range of
/// page table entries (its first page is in the page block of its triangle-word stream), and
/// shaders read <c>slot = pageTable[firstPage + page]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The publisher thread registers and retires geometries, and captures the page table into each
/// sealed publication (<see cref="CapturePageTable"/>). A backend compares the slot images
/// (<see cref="CaptureSlotImages"/>) with the images that its pool buffer holds when
/// <see cref="Version"/> changes, and writes the changed slots before it binds a page table that
/// names them. A page table entry therefore never names a slot whose image is not written.
/// </para>
/// <para>
/// A retired geometry keeps its slots and page table entries until every publication that can
/// still draw it is reclaimed (<see cref="ReclaimAcknowledged"/>). Its entries read
/// <see cref="NotResident"/> from the publication that tombstones it. A slot is written again only
/// after it returns to the free list, so a frame in flight never reads a slot that is being
/// overwritten.
/// </para>
/// <para>
/// A geometry can register with only some of its pages resident (for example only its root
/// pages). The streaming manager (<see cref="XREngine.Rendering.Meshlets.MeshletPageStreamingManager"/>)
/// then loads pages into free slots (<see cref="TryLoadPage"/>) and evicts pages
/// (<see cref="EvictPage"/>). An entry changes between a slot and <see cref="NotResident"/> only
/// in the next captured page table, so each publication reads one consistent table. An evicted
/// slot follows the same retirement rule as the slots of a retired geometry.
/// </para>
/// </remarks>
public sealed class AdvancedGeometryPagePool
{
    /// <summary>The page table value of a page that has no slot.</summary>
    public const uint NotResident = 0xFFFFFFFFu;

    /// <summary>The bytes of one pool slot.</summary>
    public const int SlotBytes = MeshletPageFormat.PageBytes;

    /// <summary>
    /// The largest slot count: 2 GiB of pool. The pool is one storage buffer, so its size must stay
    /// below the storage buffer range of common devices.
    /// </summary>
    public const int MaximumSlotCount = 16384;

    private readonly object _gate = new();
    private readonly Dictionary<AdvancedGpuHandle, PageRange> _geometryRanges = [];
    private readonly List<PageRange> _freeRanges = [];
    private readonly List<RetiredRange> _retiredRanges = [];
    private readonly List<RetiredSlot> _retiredSlots = [];
    private int[] _freeSlots = [];
    private int _freeSlotCount;
    private byte[]?[] _slotImages = [];
    private uint[] _pageTable = [];
    private int _pageHighWater;
    private ulong _version;
    private int _slotCount;

    /// <summary>A reserved range of page table entries.</summary>
    /// <param name="FirstPage">The first page table entry of the range.</param>
    /// <param name="PageCount">The number of entries.</param>
    public readonly record struct PageRange(uint FirstPage, int PageCount);

    private readonly record struct RetiredRange(PageRange Range, ulong PublicationGeneration);

    private readonly record struct RetiredSlot(int Slot, ulong PublicationGeneration);

    /// <summary>The configured slot count, or zero before <see cref="EnsureConfigured"/>.</summary>
    public int SlotCount
    {
        get { lock (_gate) return _slotCount; }
    }

    /// <summary>The bytes of the pool buffer that a backend allocates.</summary>
    public long PoolBytes
    {
        get { lock (_gate) return (long)_slotCount * SlotBytes; }
    }

    /// <summary>The slots that hold a page, including slots of retired geometries that wait for reclamation.</summary>
    public int UsedSlotCount
    {
        get { lock (_gate) return _slotCount - _freeSlotCount; }
    }

    /// <summary>The slots that are free now and can take a page.</summary>
    public int FreeSlotCount
    {
        get { lock (_gate) return _freeSlotCount; }
    }

    /// <summary>
    /// The slots that were evicted or retired and wait for <see cref="ReclaimAcknowledged"/>.
    /// They return to the free list after the publications that name them are reclaimed.
    /// </summary>
    public int RetiringSlotCount
    {
        get { lock (_gate) return _retiredSlots.Count; }
    }

    /// <summary>The number of page table entries in use or retired: the page table length.</summary>
    public int PageTableLength
    {
        get { lock (_gate) return _pageHighWater; }
    }

    /// <summary>Changes each time a page table entry changes.</summary>
    public ulong Version
    {
        get { lock (_gate) return _version; }
    }

    /// <summary>
    /// Sets the slot count before the first registration. A later call with another count has no
    /// effect, because a resident slot cannot move.
    /// </summary>
    public void EnsureConfigured(int slotCount)
    {
        slotCount = Math.Clamp(slotCount, 0, MaximumSlotCount);
        lock (_gate)
        {
            if (_slotCount != 0 || slotCount == 0)
                return;

            _slotCount = slotCount;
            _freeSlots = new int[slotCount];
            _slotImages = new byte[]?[slotCount];
            // Pop from the end, so the lowest slots are used first.
            for (int index = 0; index < slotCount; ++index)
                _freeSlots[index] = slotCount - 1 - index;
            _freeSlotCount = slotCount;
        }
    }

    /// <summary>
    /// Reserves page table entries and a slot for every page of <paramref name="pages"/>, and
    /// stores the page images as the slot images. Returns false, and changes nothing, when the pool does not have
    /// enough free slots. Call <see cref="Bind"/> with the geometry handle after the geometry is
    /// registered, or <see cref="Release"/> when the registration fails.
    /// </summary>
    public bool TryReserve(MeshletPageSet pages, out PageRange range)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return TryReserve(pages, pages.PageImages.Length, out range);
    }

    /// <summary>
    /// Reserves a page table entry for every page of <paramref name="pages"/>, and a slot for each
    /// of the first <paramref name="residentPageCount"/> pages. The other entries are
    /// <see cref="NotResident"/> until <see cref="TryLoadPage"/> loads them. The root pages are the
    /// first pages of a page set, so a streamed geometry passes its root page count. Returns false,
    /// and changes nothing, when the pool does not have enough free slots or a resident image is
    /// empty or larger than a slot.
    /// </summary>
    public bool TryReserve(MeshletPageSet pages, int residentPageCount, out PageRange range)
    {
        ArgumentNullException.ThrowIfNull(pages);
        range = default;
        int pageCount = pages.PageImages.Length;
        if (pageCount == 0 || residentPageCount < 0 || residentPageCount > pageCount)
            return false;

        lock (_gate)
        {
            if (residentPageCount > _freeSlotCount)
                return false;
            for (int page = 0; page < residentPageCount; ++page)
                if (!IsValidImage(pages.PageImages[page]))
                    return false;

            uint firstPage = AllocatePageRangeNoLock(pageCount);
            for (int page = 0; page < residentPageCount; ++page)
            {
                int slot = _freeSlots[--_freeSlotCount];
                _slotImages[slot] = pages.PageImages[page];
                _pageTable[firstPage + page] = (uint)slot;
            }
            ++_version;
            range = new PageRange(firstPage, pageCount);
            return true;
        }
    }

    /// <summary>Returns the slot of a page table entry, or <see cref="NotResident"/>.</summary>
    public uint GetSlot(uint globalPage)
    {
        lock (_gate)
            return globalPage < (uint)_pageHighWater ? _pageTable[globalPage] : NotResident;
    }

    /// <summary>
    /// Puts <paramref name="image"/> in a free slot and points the page table entry at it. The
    /// next captured page table names the slot, and a backend writes the slot image before it binds
    /// that table. Returns false, and changes nothing, when no slot is free, the entry is outside
    /// the table or already resident, or the image is empty or larger than a slot.
    /// </summary>
    public bool TryLoadPage(uint globalPage, byte[]? image, out int slot)
    {
        slot = -1;
        if (!IsValidImage(image))
            return false;

        lock (_gate)
        {
            if (_freeSlotCount == 0 || globalPage >= (uint)_pageHighWater || _pageTable[globalPage] != NotResident)
                return false;

            slot = _freeSlots[--_freeSlotCount];
            _slotImages[slot] = image;
            _pageTable[globalPage] = (uint)slot;
            ++_version;
            return true;
        }
    }

    /// <summary>
    /// Sets a resident page table entry to <see cref="NotResident"/> in the next captured table,
    /// and retires its slot. The slot keeps its image and returns to the free list when
    /// <see cref="ReclaimAcknowledged"/> reaches <paramref name="publicationGeneration"/>: the
    /// generation of the publication that captures the eviction. Returns false when the entry is
    /// not resident.
    /// </summary>
    public bool EvictPage(uint globalPage, ulong publicationGeneration)
    {
        lock (_gate)
        {
            if (globalPage >= (uint)_pageHighWater)
                return false;
            ref uint entry = ref _pageTable[globalPage];
            if (entry == NotResident)
                return false;

            _retiredSlots.Add(new RetiredSlot((int)entry, publicationGeneration));
            entry = NotResident;
            ++_version;
            return true;
        }
    }

    /// <summary>Associates a reserved range with the geometry that reads it.</summary>
    public void Bind(AdvancedGpuHandle geometry, PageRange range)
    {
        lock (_gate)
            _geometryRanges[geometry] = range;
    }

    /// <summary>
    /// Frees a range at once. Use this only for a range that no sealed publication names: a
    /// failed registration, or a geometry removed before its first publication.
    /// </summary>
    public void Release(PageRange range)
    {
        lock (_gate)
            FreeRangeNoLock(range);
    }

    /// <summary>Frees the range of a geometry that no sealed publication names, if it has one.</summary>
    public void ReleaseGeometry(AdvancedGpuHandle geometry)
    {
        lock (_gate)
            if (_geometryRanges.Remove(geometry, out PageRange range))
                FreeRangeNoLock(range);
    }

    /// <summary>
    /// Retires the range of a tombstoned geometry. Its page table entries read
    /// <see cref="NotResident"/> from the next capture. Its slots keep their images, and its
    /// slots and entries return to the free lists when <see cref="ReclaimAcknowledged"/> reaches
    /// <paramref name="publicationGeneration"/>: the generation of the publication that tombstones
    /// the geometry. Older publications still name the slots until every consumer acknowledges
    /// that generation.
    /// </summary>
    public void RetireGeometry(AdvancedGpuHandle geometry, ulong publicationGeneration)
    {
        lock (_gate)
        {
            if (!_geometryRanges.Remove(geometry, out PageRange range))
                return;

            for (int page = 0; page < range.PageCount; ++page)
            {
                ref uint entry = ref _pageTable[range.FirstPage + page];
                if (entry == NotResident)
                    continue;
                _retiredSlots.Add(new RetiredSlot((int)entry, publicationGeneration));
                entry = NotResident;
            }
            _retiredRanges.Add(new RetiredRange(range, publicationGeneration));
            ++_version;
        }
    }

    /// <summary>
    /// Frees the slots and page table entries that were retired at or before
    /// <paramref name="safePublicationGeneration"/>. The caller passes the newest publication
    /// generation that every consumer has acknowledged and that no lease holds, so no frame can
    /// still read a publication that names the freed slots. Returns the number of freed ranges.
    /// </summary>
    public int ReclaimAcknowledged(ulong safePublicationGeneration)
    {
        lock (_gate)
        {
            for (int index = _retiredSlots.Count - 1; index >= 0; --index)
            {
                RetiredSlot retired = _retiredSlots[index];
                if (retired.PublicationGeneration > safePublicationGeneration)
                    continue;
                FreeSlotNoLock(retired.Slot);
                _retiredSlots.RemoveAt(index);
            }

            int reclaimed = 0;
            for (int index = _retiredRanges.Count - 1; index >= 0; --index)
            {
                RetiredRange retired = _retiredRanges[index];
                if (retired.PublicationGeneration > safePublicationGeneration)
                    continue;
                AddFreeRangeNoLock(retired.Range);
                _retiredRanges.RemoveAt(index);
                ++reclaimed;
            }
            // A freed range at the end of the table shortens the captured table.
            if (reclaimed != 0)
                ++_version;
            return reclaimed;
        }
    }

    /// <summary>
    /// Copies the page table into <paramref name="destination"/>, growing it when it is too small.
    /// Returns the table length. A backend binds exactly this table with the publication.
    /// </summary>
    public int CapturePageTable(ref uint[] destination, out ulong version)
    {
        lock (_gate)
        {
            version = _version;
            if (destination.Length < _pageHighWater)
                destination = new uint[Math.Max(_pageHighWater, destination.Length * 2)];
            _pageTable.AsSpan(0, _pageHighWater).CopyTo(destination);
            return _pageHighWater;
        }
    }

    /// <summary>
    /// Copies the image of every slot into <paramref name="destination"/> (null for a free slot),
    /// and returns the version of that state. <paramref name="destination"/> must hold
    /// <see cref="SlotCount"/> entries. A backend writes each slot whose image is not the image
    /// that its pool buffer already holds.
    /// </summary>
    public ulong CaptureSlotImages(Span<byte[]?> destination)
    {
        lock (_gate)
        {
            _slotImages.AsSpan(0, _slotCount).CopyTo(destination);
            return _version;
        }
    }

    private static bool IsValidImage(byte[]? image)
        => image is { Length: > 0 and <= SlotBytes };

    private uint AllocatePageRangeNoLock(int pageCount)
    {
        for (int index = 0; index < _freeRanges.Count; ++index)
        {
            PageRange free = _freeRanges[index];
            if (free.PageCount < pageCount)
                continue;
            if (free.PageCount == pageCount)
                _freeRanges.RemoveAt(index);
            else
                _freeRanges[index] = new PageRange(free.FirstPage + (uint)pageCount, free.PageCount - pageCount);
            return free.FirstPage;
        }

        uint firstPage = checked((uint)_pageHighWater);
        int required = checked(_pageHighWater + pageCount);
        if (required > _pageTable.Length)
        {
            uint[] grown = new uint[Math.Max(required, Math.Max(1024, _pageTable.Length * 2))];
            _pageTable.AsSpan(0, _pageHighWater).CopyTo(grown);
            grown.AsSpan(_pageHighWater).Fill(NotResident);
            _pageTable = grown;
        }
        _pageHighWater = required;
        return firstPage;
    }

    private void FreeRangeNoLock(PageRange range)
    {
        for (int page = 0; page < range.PageCount; ++page)
        {
            ref uint entry = ref _pageTable[range.FirstPage + page];
            if (entry == NotResident)
                continue;
            FreeSlotNoLock((int)entry);
            entry = NotResident;
        }
        AddFreeRangeNoLock(range);
        ++_version;
    }

    /// <summary>
    /// Returns a range of page table entries to the free list. The list stays sorted by first
    /// page, and adjacent ranges merge, so freed entries do not fragment the table. A free range at
    /// the end of the table lowers the table length instead.
    /// </summary>
    private void AddFreeRangeNoLock(PageRange range)
    {
        int index = 0;
        while (index < _freeRanges.Count && _freeRanges[index].FirstPage < range.FirstPage)
            ++index;

        uint first = range.FirstPage;
        int count = range.PageCount;
        if (index > 0)
        {
            PageRange previous = _freeRanges[index - 1];
            if (previous.FirstPage + (uint)previous.PageCount == first)
            {
                first = previous.FirstPage;
                count += previous.PageCount;
                _freeRanges.RemoveAt(--index);
            }
        }
        if (index < _freeRanges.Count)
        {
            PageRange next = _freeRanges[index];
            if (first + (uint)count == next.FirstPage)
            {
                count += next.PageCount;
                _freeRanges.RemoveAt(index);
            }
        }

        if (first + (uint)count == (uint)_pageHighWater)
        {
            _pageHighWater = (int)first;
            return;
        }
        _freeRanges.Insert(index, new PageRange(first, count));
    }

    private void FreeSlotNoLock(int slot)
    {
        _slotImages[slot] = null;
        _freeSlots[_freeSlotCount++] = slot;
    }
}

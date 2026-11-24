using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The residency record of every page of the streamed geometries, indexed by the global page (the
/// page table entry of the page). The <see cref="MeshletPageStreamingManager"/> reads and changes
/// the records. The registry also maps each pool slot back to the page that it holds, so the
/// manager can apply the GPU touch bits, which are indexed by slot.
/// </summary>
/// <remarks>
/// The arrays grow only when a geometry registers past their end. The frame path reads and writes
/// them in place and does not allocate. A global page that no streamed geometry owns has
/// <see cref="NoGeometry"/> as its geometry index, and the manager ignores it. That includes the
/// pages of geometries that register with every page resident.
/// </remarks>
public sealed class MeshletPageRegistry
{
    /// <summary>The geometry index of a page that no streamed geometry owns.</summary>
    public const int NoGeometry = -1;

    /// <summary>The page index of a slot that holds no streamed page.</summary>
    public const int NoPage = -1;

    private readonly List<Geometry?> _geometries = [];
    private readonly Stack<int> _freeGeometryIndices = new();
    private readonly Dictionary<AdvancedGpuHandle, int> _geometryIndices = [];
    private Page[] _pages = [];
    private int[] _slotPages = [];

    /// <summary>The residency record of one global page.</summary>
    public struct Page
    {
        /// <summary>The residency state.</summary>
        public EMeshletPageResidencyState State;
        /// <summary>The index of the owning geometry, or <see cref="NoGeometry"/>.</summary>
        public int GeometryIndex;
        /// <summary>The geometry-local page index.</summary>
        public int LocalPage;
        /// <summary>The pool slot while the page is resident, or -1.</summary>
        public int Slot;
        /// <summary>The resident pages that list this page as a dependency. A page with resident children is not evicted.</summary>
        public int ResidentChildCount;
        /// <summary>The newest frame that drew the page or that had it in the ideal cut.</summary>
        public long LastTouchFrame;
        /// <summary>The largest simplification error of the groups of the page, in mesh-space units.</summary>
        public float MaxGroupError;
        /// <summary>The band of the load that made the page resident.</summary>
        public EMeshletPageRequestBand ResidentBand;
        /// <summary>The update that last requested the page. The request fields below are valid only for that update.</summary>
        public long RequestUpdate;
        /// <summary>The highest band of the requests of <see cref="RequestUpdate"/>.</summary>
        public EMeshletPageRequestBand RequestBand;
        /// <summary>The highest priority of the requests of <see cref="RequestUpdate"/>.</summary>
        public ushort RequestPriority;
        /// <summary>The largest pixel scale of the requests of <see cref="RequestUpdate"/>.</summary>
        public float RequestPixelScale;
    }

    /// <summary>One streamed geometry: its handle, its page table range, and its page set.</summary>
    /// <param name="Handle">The geometry handle.</param>
    /// <param name="FirstPage">The first page table entry of the geometry.</param>
    /// <param name="Pages">The page set. The manager loads the page images from it.</param>
    public sealed record Geometry(AdvancedGpuHandle Handle, uint FirstPage, MeshletPageSet Pages)
    {
        /// <summary>The number of pages.</summary>
        public int PageCount => Pages.PageImages.Length;

        /// <summary>The number of root pages. They stay resident while the geometry is registered.</summary>
        public int RootPageCount => Pages.Table.RootPageCount;
    }

    /// <summary>The number of registered streamed geometries.</summary>
    public int GeometryCount => _geometryIndices.Count;

    /// <summary>The length of the page record array (the highest registered page + 1, or more).</summary>
    public int PageCapacity => _pages.Length;

    /// <summary>The length of the slot map (the pool slot count).</summary>
    public int SlotCapacity => _slotPages.Length;

    /// <summary>Returns the record of a global page. The page must be below <see cref="PageCapacity"/>.</summary>
    public ref Page GetPage(uint globalPage)
        => ref _pages[globalPage];

    /// <summary>True when the global page is in the registry and a streamed geometry owns it.</summary>
    public bool IsRegistered(uint globalPage)
        => globalPage < (uint)_pages.Length && _pages[globalPage].GeometryIndex != NoGeometry;

    /// <summary>
    /// The length of the geometry list, including free entries. Use it with
    /// <see cref="TryGetGeometry"/> to visit every registered geometry.
    /// </summary>
    public int GeometryListLength => _geometries.Count;

    /// <summary>Returns the geometry at an index of the geometry list, or false when the entry is free.</summary>
    public bool TryGetGeometry(int geometryIndex, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Geometry? geometry)
    {
        geometry = (uint)geometryIndex < (uint)_geometries.Count ? _geometries[geometryIndex] : null;
        return geometry is not null;
    }

    /// <summary>Returns a registered geometry by its index.</summary>
    public Geometry GetGeometry(int geometryIndex)
        => _geometries[geometryIndex] ?? throw new InvalidOperationException($"Geometry index {geometryIndex} is not registered.");

    /// <summary>Returns the global page that a pool slot holds, or <see cref="NoPage"/>.</summary>
    public int GetSlotPage(int slot)
        => (uint)slot < (uint)_slotPages.Length ? _slotPages[slot] : NoPage;

    /// <summary>
    /// Adds a geometry and makes a record for each of its pages. The records start
    /// <see cref="EMeshletPageResidencyState.NotResident"/>. The caller then marks the pages that
    /// the pool made resident with <see cref="MarkResident"/>. Returns the geometry index.
    /// </summary>
    /// <exception cref="InvalidOperationException">The geometry is already registered.</exception>
    public int AddGeometry(AdvancedGpuHandle handle, uint firstPage, MeshletPageSet pages, int slotCount)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (_geometryIndices.ContainsKey(handle))
            throw new InvalidOperationException($"Geometry {handle} is already registered for page streaming.");

        int pageCount = pages.PageImages.Length;
        EnsurePageCapacity(checked((int)firstPage + pageCount));
        EnsureSlotCapacity(slotCount);

        Geometry geometry = new(handle, firstPage, pages);
        int index;
        if (_freeGeometryIndices.Count > 0)
        {
            index = _freeGeometryIndices.Pop();
            _geometries[index] = geometry;
        }
        else
        {
            index = _geometries.Count;
            _geometries.Add(geometry);
        }
        _geometryIndices.Add(handle, index);

        Span<Page> records = _pages.AsSpan((int)firstPage, pageCount);
        for (int page = 0; page < pageCount; ++page)
            records[page] = new Page
            {
                State = EMeshletPageResidencyState.NotResident,
                GeometryIndex = index,
                LocalPage = page,
                Slot = -1,
                RequestUpdate = -1,
            };

        // Each group's page takes the largest error of its groups. The manager multiplies it by a
        // request's pixel scale to decide whether a dependency is a foreground load.
        MeshletPageTable table = pages.Table;
        for (int group = 0; group < table.Groups.Length; ++group)
        {
            int page = table.GetGroupPage(group);
            if ((uint)page >= (uint)pageCount)
                continue;
            ref Page record = ref records[page];
            record.MaxGroupError = MathF.Max(record.MaxGroupError, table.Groups[group].SimplifiedError);
        }
        return index;
    }

    /// <summary>
    /// Removes a geometry and clears the records of its pages and the slot map entries of its
    /// resident pages. The pool retires the slots. Returns the removed geometry, or null when the
    /// handle is not registered.
    /// </summary>
    public Geometry? RemoveGeometry(AdvancedGpuHandle handle)
    {
        if (!_geometryIndices.Remove(handle, out int index))
            return null;

        Geometry geometry = _geometries[index]!;
        Span<Page> records = _pages.AsSpan((int)geometry.FirstPage, geometry.PageCount);
        for (int page = 0; page < records.Length; ++page)
        {
            int slot = records[page].Slot;
            if ((uint)slot < (uint)_slotPages.Length)
                _slotPages[slot] = NoPage;
            records[page] = new Page { GeometryIndex = NoGeometry, Slot = -1, RequestUpdate = -1 };
        }
        _geometries[index] = null;
        _freeGeometryIndices.Push(index);
        return geometry;
    }

    /// <summary>Returns the index of a registered geometry, or <see cref="NoGeometry"/>.</summary>
    public int FindGeometry(AdvancedGpuHandle handle)
        => _geometryIndices.TryGetValue(handle, out int index) ? index : NoGeometry;

    /// <summary>
    /// Marks a page resident in <paramref name="slot"/>, maps the slot to the page, and adds the
    /// page as a resident child of each of its dependencies.
    /// </summary>
    public void MarkResident(uint globalPage, int slot, EMeshletPageRequestBand band, long frame)
    {
        ref Page record = ref _pages[globalPage];
        record.State = EMeshletPageResidencyState.Resident;
        record.Slot = slot;
        record.ResidentBand = band;
        record.LastTouchFrame = Math.Max(record.LastTouchFrame, frame);
        if ((uint)slot < (uint)_slotPages.Length)
            _slotPages[slot] = (int)globalPage;

        Geometry geometry = _geometries[record.GeometryIndex]!;
        foreach (uint dependency in geometry.Pages.Table.GetDependencies(record.LocalPage))
            ++_pages[geometry.FirstPage + dependency].ResidentChildCount;
    }

    /// <summary>
    /// Marks a resident page not resident and clears its slot map entry. Each dependency loses a
    /// resident child and takes the newer of its own last-touch frame and the page's, so a parent
    /// does not become the oldest eviction candidate at the moment its last child leaves.
    /// </summary>
    public void MarkEvicted(uint globalPage)
    {
        ref Page record = ref _pages[globalPage];
        if ((uint)record.Slot < (uint)_slotPages.Length)
            _slotPages[record.Slot] = NoPage;
        record.State = EMeshletPageResidencyState.NotResident;
        record.Slot = -1;

        Geometry geometry = _geometries[record.GeometryIndex]!;
        long lastTouch = record.LastTouchFrame;
        foreach (uint dependency in geometry.Pages.Table.GetDependencies(record.LocalPage))
        {
            ref Page parent = ref _pages[geometry.FirstPage + dependency];
            parent.ResidentChildCount = Math.Max(0, parent.ResidentChildCount - 1);
            parent.LastTouchFrame = Math.Max(parent.LastTouchFrame, lastTouch);
        }
    }

    private void EnsurePageCapacity(int required)
    {
        if (required <= _pages.Length)
            return;

        int oldLength = _pages.Length;
        Array.Resize(ref _pages, Math.Max(required, Math.Max(1024, oldLength * 2)));
        for (int page = oldLength; page < _pages.Length; ++page)
            _pages[page] = new Page { GeometryIndex = NoGeometry, Slot = -1, RequestUpdate = -1 };
    }

    private void EnsureSlotCapacity(int slotCount)
    {
        if (slotCount <= _slotPages.Length)
            return;

        int oldLength = _slotPages.Length;
        Array.Resize(ref _slotPages, slotCount);
        _slotPages.AsSpan(oldLength).Fill(NoPage);
    }
}

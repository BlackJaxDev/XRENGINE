using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Shadows;

public sealed class ShadowAtlasFrameData
{
    private ShadowAtlasAllocation[] _allocations = [];
    private ShadowAtlasGroupedDirectionalCascadeAllocation[] _directionalCascadeGroups = [];
    private ShadowAtlasPageDescriptor[] _pages = [];
    private int _allocationCount;
    private int _directionalCascadeGroupCount;
    private int _pageCount;

    public ulong FrameId { get; private set; }
    public ulong Generation { get; private set; }
    public ShadowAtlasMetrics Metrics { get; private set; }
    public int AllocationCount => _allocationCount;
    public int DirectionalCascadeGroupCount => _directionalCascadeGroupCount;
    public int PageCount => _pageCount;
    public ReadOnlySpan<ShadowAtlasAllocation> Allocations => _allocations.AsSpan(0, _allocationCount);
    public ReadOnlySpan<ShadowAtlasGroupedDirectionalCascadeAllocation> DirectionalCascadeGroups => _directionalCascadeGroups.AsSpan(0, _directionalCascadeGroupCount);
    public ReadOnlySpan<ShadowAtlasPageDescriptor> Pages => _pages.AsSpan(0, _pageCount);

    public ShadowAtlasAllocation GetAllocation(int index)
    {
        if ((uint)index >= (uint)_allocationCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _allocations[index];
    }

    public ShadowAtlasPageDescriptor GetPage(int index)
    {
        if ((uint)index >= (uint)_pageCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _pages[index];
    }

    public ShadowAtlasGroupedDirectionalCascadeAllocation GetDirectionalCascadeGroup(int index)
    {
        if ((uint)index >= (uint)_directionalCascadeGroupCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _directionalCascadeGroups[index];
    }

    public bool TryGetAllocation(ShadowRequestKey key, out ShadowAtlasAllocation allocation)
    {
        for (int i = 0; i < _allocationCount; i++)
        {
            ShadowAtlasAllocation candidate = _allocations[i];
            if (candidate.Key == key)
            {
                allocation = candidate;
                return true;
            }
        }

        allocation = default;
        return false;
    }

    public bool TryGetAllocationIndex(ShadowRequestKey key, out int index, out ShadowAtlasAllocation allocation)
    {
        for (int i = 0; i < _allocationCount; i++)
        {
            ShadowAtlasAllocation candidate = _allocations[i];
            if (candidate.Key == key)
            {
                index = i;
                allocation = candidate;
                return true;
            }
        }

        index = -1;
        allocation = default;
        return false;
    }

    public bool TryGetDirectionalCascadeGroup(Guid lightId, out ShadowAtlasGroupedDirectionalCascadeAllocation group)
    {
        for (int i = 0; i < _directionalCascadeGroupCount; i++)
        {
            ShadowAtlasGroupedDirectionalCascadeAllocation candidate = _directionalCascadeGroups[i];
            if (candidate.LightId == lightId)
            {
                group = candidate;
                return true;
            }
        }

        group = default;
        return false;
    }

    internal void SetData(
        ulong frameId,
        ulong generation,
        IReadOnlyList<ShadowAtlasAllocation> allocations,
        IReadOnlyList<ShadowAtlasGroupedDirectionalCascadeAllocation> directionalCascadeGroups,
        IReadOnlyList<ShadowAtlasPageDescriptor> pages,
        ShadowAtlasMetrics metrics)
    {
        EnsureAllocationCapacity(allocations.Count);
        EnsureDirectionalCascadeGroupCapacity(directionalCascadeGroups.Count);
        EnsurePageCapacity(pages.Count);

        for (int i = 0; i < allocations.Count; i++)
            _allocations[i] = allocations[i];
        for (int i = allocations.Count; i < _allocationCount; i++)
            _allocations[i] = default;

        for (int i = 0; i < directionalCascadeGroups.Count; i++)
            _directionalCascadeGroups[i] = directionalCascadeGroups[i];
        for (int i = directionalCascadeGroups.Count; i < _directionalCascadeGroupCount; i++)
            _directionalCascadeGroups[i] = default;

        for (int i = 0; i < pages.Count; i++)
            _pages[i] = pages[i];
        for (int i = pages.Count; i < _pageCount; i++)
            _pages[i] = default;

        _allocationCount = allocations.Count;
        _directionalCascadeGroupCount = directionalCascadeGroups.Count;
        _pageCount = pages.Count;
        FrameId = frameId;
        Generation = generation;
        Metrics = metrics;
    }

    private void EnsureAllocationCapacity(int count)
    {
        if (_allocations.Length >= count)
            return;

        int next = Math.Max(4, _allocations.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _allocations, next);
    }

    private void EnsureDirectionalCascadeGroupCapacity(int count)
    {
        if (_directionalCascadeGroups.Length >= count)
            return;

        int next = Math.Max(4, _directionalCascadeGroups.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _directionalCascadeGroups, next);
    }

    private void EnsurePageCapacity(int count)
    {
        if (_pages.Length >= count)
            return;

        int next = Math.Max(4, _pages.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _pages, next);
    }
}

public sealed class ShadowAtlasPageResource
{
    internal ShadowAtlasPageResource(
        ShadowAtlasPageDescriptor descriptor,
        XRTexture2DArray texture,
        XRTexture2DArray rasterDepthTexture)
    {
        Descriptor = descriptor;
        Texture = texture;
        RasterDepthTexture = rasterDepthTexture;
        FrameBuffer = new XRFrameBuffer(
            (Texture, EFrameBufferAttachment.ColorAttachment0, 0, descriptor.PageIndex),
            (RasterDepthTexture, EFrameBufferAttachment.DepthAttachment, 0, descriptor.PageIndex));
    }

    public ShadowAtlasPageDescriptor Descriptor { get; }
    public XRTexture2DArray Texture { get; }
    public XRTexture2DArray RasterDepthTexture { get; }
    public XRFrameBuffer FrameBuffer { get; }
}

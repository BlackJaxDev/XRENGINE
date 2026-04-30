using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Shadows;

public sealed class ShadowAtlasFrameData
{
    private ShadowAtlasAllocation[] _allocations = [];
    private ShadowAtlasPageDescriptor[] _pages = [];
    private int _allocationCount;
    private int _pageCount;

    public ulong FrameId { get; private set; }
    public ulong Generation { get; private set; }
    public ShadowAtlasMetrics Metrics { get; private set; }
    public int AllocationCount => _allocationCount;
    public int PageCount => _pageCount;
    public ReadOnlySpan<ShadowAtlasAllocation> Allocations => _allocations.AsSpan(0, _allocationCount);
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

    internal void SetData(
        ulong frameId,
        ulong generation,
        IReadOnlyList<ShadowAtlasAllocation> allocations,
        IReadOnlyList<ShadowAtlasPageDescriptor> pages,
        ShadowAtlasMetrics metrics)
    {
        EnsureAllocationCapacity(allocations.Count);
        EnsurePageCapacity(pages.Count);

        for (int i = 0; i < allocations.Count; i++)
            _allocations[i] = allocations[i];
        for (int i = allocations.Count; i < _allocationCount; i++)
            _allocations[i] = default;

        for (int i = 0; i < pages.Count; i++)
            _pages[i] = pages[i];
        for (int i = pages.Count; i < _pageCount; i++)
            _pages[i] = default;

        _allocationCount = allocations.Count;
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
    internal ShadowAtlasPageResource(ShadowAtlasPageDescriptor descriptor)
    {
        Descriptor = descriptor;
        Texture = new XRTexture2D(
            descriptor.PageSize,
            descriptor.PageSize,
            descriptor.InternalFormat,
            descriptor.PixelFormat,
            descriptor.PixelType,
            allocateData: false)
        {
            SamplerName = $"ShadowAtlas_{descriptor.Encoding}_{descriptor.PageIndex}",
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            MinFilter = ETexMinFilter.Nearest,
            MagFilter = ETexMagFilter.Nearest,
        };
        RasterDepthTexture = new XRTexture2D(
            descriptor.PageSize,
            descriptor.PageSize,
            EPixelInternalFormat.DepthComponent24,
            EPixelFormat.DepthComponent,
            EPixelType.UnsignedInt,
            allocateData: false)
        {
            SamplerName = $"ShadowAtlasDepth_{descriptor.Encoding}_{descriptor.PageIndex}",
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            MinFilter = ETexMinFilter.Nearest,
            MagFilter = ETexMagFilter.Nearest,
            FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
        };
        FrameBuffer = new XRFrameBuffer(
            (Texture, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (RasterDepthTexture, EFrameBufferAttachment.DepthAttachment, 0, -1));
    }

    public ShadowAtlasPageDescriptor Descriptor { get; }
    public XRTexture2D Texture { get; }
    public XRTexture2D RasterDepthTexture { get; }
    public XRFrameBuffer FrameBuffer { get; }
}

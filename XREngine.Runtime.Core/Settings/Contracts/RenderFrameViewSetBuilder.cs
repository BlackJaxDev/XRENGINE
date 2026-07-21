namespace XREngine;

/// <summary>
/// Allocation-free, fixed-capacity builder for the canonical logical views captured for a frame.
/// </summary>
public ref struct RenderFrameViewSetBuilder
{
    private readonly Span<RenderFrameViewDescriptor> _views;
    private int _count;

    public RenderFrameViewSetBuilder(Span<RenderFrameViewDescriptor> storage)
    {
        if (storage.Length < RenderFrameViewSet.MaxViewCount)
            throw new ArgumentException($"View storage must hold {RenderFrameViewSet.MaxViewCount} descriptors.", nameof(storage));

        _views = storage[..RenderFrameViewSet.MaxViewCount];
        _count = 0;
    }

    public int Count => _count;

    public uint Add(in RenderFrameViewDescriptor descriptor)
    {
        if (_count >= _views.Length)
            throw new InvalidOperationException($"A frame cannot contain more than {RenderFrameViewSet.MaxViewCount} logical views.");

        uint viewId = (uint)_count;
        _views[_count++] = descriptor with { ViewId = viewId };
        return viewId;
    }

    public RenderFrameViewSet Build(
        EVrViewRenderMode renderMode,
        EVrVisibilityPolicy visibilityPolicy,
        int visibilityGroupCount,
        string? debugName = null)
        => RenderFrameViewSet.Create(renderMode, visibilityPolicy, visibilityGroupCount, _views[.._count], debugName);
}
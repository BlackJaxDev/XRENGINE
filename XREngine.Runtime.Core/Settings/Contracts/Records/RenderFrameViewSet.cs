namespace XREngine;

public readonly record struct RenderFrameViewSet
{
    public const int MaxViewCount = 8;

    private readonly RenderFrameViewDescriptor _view0;
    private readonly RenderFrameViewDescriptor _view1;
    private readonly RenderFrameViewDescriptor _view2;
    private readonly RenderFrameViewDescriptor _view3;
    private readonly RenderFrameViewDescriptor _view4;
    private readonly RenderFrameViewDescriptor _view5;
    private readonly RenderFrameViewDescriptor _view6;
    private readonly RenderFrameViewDescriptor _view7;

    private RenderFrameViewSet(
        EVrViewRenderMode renderMode,
        EVrVisibilityPolicy visibilityPolicy,
        int visibilityGroupCount,
        int viewCount,
        RenderFrameViewDescriptor view0,
        RenderFrameViewDescriptor view1,
        RenderFrameViewDescriptor view2,
        RenderFrameViewDescriptor view3,
        RenderFrameViewDescriptor view4,
        RenderFrameViewDescriptor view5,
        RenderFrameViewDescriptor view6,
        RenderFrameViewDescriptor view7,
        string? debugName)
    {
        RenderMode = renderMode;
        VisibilityPolicy = visibilityPolicy;
        VisibilityGroupCount = visibilityGroupCount;
        ViewCount = viewCount;
        _view0 = view0;
        _view1 = view1;
        _view2 = view2;
        _view3 = view3;
        _view4 = view4;
        _view5 = view5;
        _view6 = view6;
        _view7 = view7;
        DebugName = debugName;
    }

    public EVrViewRenderMode RenderMode { get; }
    public EVrVisibilityPolicy VisibilityPolicy { get; }
    public int VisibilityGroupCount { get; }
    public int ViewCount { get; }
    public string? DebugName { get; }
    public bool IsQuadViewSet => CountQuadViews() == 4;

    public static RenderFrameViewSet Create(
        EVrViewRenderMode renderMode,
        EVrVisibilityPolicy visibilityPolicy,
        int visibilityGroupCount,
        ReadOnlySpan<RenderFrameViewDescriptor> views,
        string? debugName = null)
    {
        if (views.Length < 1 || views.Length > MaxViewCount)
            throw new ArgumentOutOfRangeException(nameof(views), $"A frame view set supports 1 to {MaxViewCount} views.");
        if (visibilityGroupCount < 1)
            throw new ArgumentOutOfRangeException(nameof(visibilityGroupCount));

        RenderFrameViewDescriptor view0 = default;
        RenderFrameViewDescriptor view1 = default;
        RenderFrameViewDescriptor view2 = default;
        RenderFrameViewDescriptor view3 = default;
        RenderFrameViewDescriptor view4 = default;
        RenderFrameViewDescriptor view5 = default;
        RenderFrameViewDescriptor view6 = default;
        RenderFrameViewDescriptor view7 = default;

        for (int i = 0; i < views.Length; i++)
        {
            RenderFrameViewDescriptor view = views[i];
            ValidateView(i, view, views.Length, visibilityGroupCount);
            switch (i)
            {
                case 0: view0 = view; break;
                case 1: view1 = view; break;
                case 2: view2 = view; break;
                case 3: view3 = view; break;
                case 4: view4 = view; break;
                case 5: view5 = view; break;
                case 6: view6 = view; break;
                case 7: view7 = view; break;
            }
        }

        return new(
            renderMode,
            visibilityPolicy,
            visibilityGroupCount,
            views.Length,
            view0,
            view1,
            view2,
            view3,
            view4,
            view5,
            view6,
            view7,
            debugName);
    }

    private static void ValidateView(
        int index,
        in RenderFrameViewDescriptor view,
        int viewCount,
        int visibilityGroupCount)
    {
        if (view.ViewId != (uint)index)
            throw new ArgumentException("Frame view IDs must be dense and match their view-set slot.", nameof(view));
        if (!view.ViewRect.IsValid)
            throw new ArgumentException("Frame views require a non-zero render rectangle.", nameof(view));
        if (view.VisibilityGroupIndex < 0 || view.VisibilityGroupIndex >= visibilityGroupCount)
            throw new ArgumentOutOfRangeException(nameof(view.VisibilityGroupIndex));
        if (view.HasParent && view.ParentViewId >= (uint)viewCount)
            throw new ArgumentOutOfRangeException(nameof(view.ParentViewId));
    }

    public RenderFrameViewDescriptor GetView(int index)
        => index switch
        {
            0 when ViewCount > 0 => _view0,
            1 when ViewCount > 1 => _view1,
            2 when ViewCount > 2 => _view2,
            3 when ViewCount > 3 => _view3,
            4 when ViewCount > 4 => _view4,
            5 when ViewCount > 5 => _view5,
            6 when ViewCount > 6 => _view6,
            7 when ViewCount > 7 => _view7,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    public int CountViewsInVisibilityGroup(int visibilityGroupIndex)
    {
        if (visibilityGroupIndex < 0 || visibilityGroupIndex >= VisibilityGroupCount)
            throw new ArgumentOutOfRangeException(nameof(visibilityGroupIndex));

        int count = 0;
        for (int i = 0; i < ViewCount; i++)
        {
            if (GetView(i).VisibilityGroupIndex == visibilityGroupIndex)
                count++;
        }

        return count;
    }

    public int CountQuadViews()
    {
        int count = 0;
        for (int i = 0; i < ViewCount; i++)
        {
            if (GetView(i).IsQuadView)
                count++;
        }

        return count;
    }

    public int FindFirstView(EVrOutputViewKind kind)
    {
        for (int i = 0; i < ViewCount; i++)
        {
            if (GetView(i).Kind == kind)
                return i;
        }

        return -1;
    }
}

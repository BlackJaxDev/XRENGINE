using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;

public class UIScrollingTransform : UIBoundableTransform
{
    private Vector2 _scrollPosition;
    public Vector2 ScrollPosition
    {
        get => _scrollPosition;
        set => SetField(ref _scrollPosition, value);
    }

    private Vector2 _scrollSize;
    public Vector2 ScrollSize
    {
        get => _scrollSize;
        set => SetField(ref _scrollSize, value);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(ScrollPosition):
                OnScrollPositionChanged();
                break;
            case nameof(ScrollSize):
                OnScrollSizeChanged();
                break;
        }
    }

    private void OnScrollSizeChanged()
    {

    }

    private void OnScrollPositionChanged()
    {

    }

    protected override void GetActualBounds(BoundingRectangleF parentBounds, out Vector2 trans, out Vector2 size)
    {
        base.GetActualBounds(parentBounds, out trans, out size);
    }

    protected override void OnResizeChildComponents(BoundingRectangleF parentRegion)
    {
        //TODO: clip children to scroll region
        base.OnResizeChildComponents(parentRegion);
    }
}

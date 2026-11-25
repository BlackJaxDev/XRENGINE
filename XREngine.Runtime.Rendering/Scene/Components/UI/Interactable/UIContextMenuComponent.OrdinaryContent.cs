using System.Numerics;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

public partial class UIContextMenuComponent
{
    /// <summary>Builds the built-in scroll and list leaves before their child rows.</summary>
    private SceneNode? BuildOrdinaryMenuList(ulong showRevision)
    {
        UIScrollableTransform scrollTransform = new();
        SceneNode? scrollNode = BuildMenuLeaf(SceneNode, showRevision, leaf =>
        {
            UIScrollRegionComponent input = leaf.Add(() => new UIScrollRegionComponent());
            leaf.Write(() => scrollTransform.MinAnchor = Vector2.Zero);
            leaf.Write(() => scrollTransform.MaxAnchor = Vector2.One);
            leaf.Write(() => scrollTransform.Width = null);
            leaf.Write(() => scrollTransform.Height = null);
            leaf.Write(() => scrollTransform.ScrollableX = false);
            leaf.Write(() => input.IsTabStop = false);
            leaf.Write(() => input.ConsumeWheelAtBoundary = true);
        }, transform: scrollTransform, name: "ContextMenuScroll", contentRoot: true);
        if (scrollNode is null || !CanContinueConstruction(showRevision))
            return null;

        UIListTransform list = new();
        return BuildMenuLeaf(scrollNode, showRevision, leaf =>
        {
            leaf.Write(() => list.DisplayHorizontal = false);
            leaf.Write(() => list.ItemSpacing = 0);
            leaf.Write(() => list.Padding = Vector4.Zero);
            leaf.Write(() => list.Margins = Vector4.Zero);
            leaf.Write(() => list.MinAnchor = new Vector2(0, 1));
            leaf.Write(() => list.MaxAnchor = Vector2.One);
            leaf.Write(() => list.NormalizedPivot = new Vector2(0, 1));
            leaf.Write(() => list.Translation = Vector2.Zero);
            leaf.Write(() => list.Width = null);
            leaf.Write(() => list.Height = _requestedHeight);
            leaf.Write(() => list.ItemAlignment = EListAlignment.TopOrLeft);
        }, transform: list, name: "ContextMenuList");
    }
}

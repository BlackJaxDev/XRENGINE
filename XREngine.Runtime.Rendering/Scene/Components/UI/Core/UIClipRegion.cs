using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI;

/// <summary>
/// Resolves the axis-aligned scissor shared by a UI element and its clipping ancestors.
/// </summary>
public static class UIClipRegion
{
    /// <summary>
    /// Returns the effective crop in bottom-left canvas coordinates. A null result means no crop.
    /// </summary>
    public static BoundingRectangle? ResolveCrop(UIBoundableTransform transform, bool clipSelf)
    {
        UICanvasTransform? canvas = transform.ParentCanvas;
        BoundingRectangle? crop = clipSelf || HasActiveClip(transform)
            ? transform.GetCanvasRegion(canvas).AsBoundingRectangle()
            : null;
        for (var parent = transform.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not UIBoundableTransform bounds || !HasActiveClip(bounds))
                continue;

            BoundingRectangle parentCrop = bounds.GetCanvasRegion(canvas).AsBoundingRectangle();
            crop = crop is { } current ? Intersect(current, parentCrop) : parentCrop;
        }

        return crop;
    }

    /// <summary>
    /// Applies the same rectangular scissor as rendering after testing the element's transformed bounds.
    /// </summary>
    public static bool ContainsHit(UIComponent component, Vector2 canvasPoint)
    {
        if (!component.IsActiveInHierarchy || !component.UITransform.IsVisibleInHierarchy ||
            component.UITransform is not UIBoundableTransform bounds)
            return false;

        if (!float.IsFinite(canvasPoint.X) || !float.IsFinite(canvasPoint.Y))
            return false;

        Vector2 local = bounds.CanvasToLocal(canvasPoint);
        Vector2 size = bounds.ActualSize;
        if (!float.IsFinite(local.X) || !float.IsFinite(local.Y) ||
            !float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0 ||
            local.X < 0 || local.Y < 0 || local.X > size.X || local.Y > size.Y)
            return false;

        BoundingRectangle? crop = ResolveCrop(bounds, false);
        if (crop is not { } region)
            return true;

        // Rendering and input share canvas-local pixels, including offscreen canvases.
        // Only the backend boundary converts the bottom-left scissor to its raster convention.
        return canvasPoint.X >= region.MinX && canvasPoint.X < region.MaxX &&
               canvasPoint.Y >= region.MinY && canvasPoint.Y < region.MaxY;
    }

    private static bool HasActiveClip(UIBoundableTransform transform)
    {
        var components = transform.SceneNode?.Components;
        if (components is null)
            return false;

        for (int i = 0; i < components.Count; i++)
            if (components[i] is UIRenderableComponent { IsActiveInHierarchy: true, ClipToBounds: true })
                return true;

        return false;
    }

    private static BoundingRectangle Intersect(BoundingRectangle first, BoundingRectangle second)
    {
        int minX = Math.Max(first.MinX, second.MinX);
        int minY = Math.Max(first.MinY, second.MinY);
        int maxX = Math.Max(minX, Math.Min(first.MaxX, second.MaxX));
        int maxY = Math.Max(minY, Math.Min(first.MaxY, second.MaxY));
        return BoundingRectangle.FromMinMaxSides(minX, maxX, minY, maxY, 0.0f, 0.0f);
    }
}

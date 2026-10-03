using System.Numerics;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Browser;

/// <summary>Projects an engine interactable into the canvas' top-left normalized DOM convention.</summary>
internal static class BrowserUiBoundsProjection
{
    /// <summary>Projects the visible rectangular part of a control after ancestor scissoring.</summary>
    public static bool TryProjectVisible(UIInteractableComponent target, XRViewport? viewport,
        out float x, out float y, out float width, out float height)
    {
        if (viewport is not null && target.UserInterfaceCanvas is { } spatialCanvas &&
            spatialCanvas.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen)
            return TryProjectSpatial(target.BoundableTransform, spatialCanvas, viewport, true,
                out x, out y, out width, out height);

        if (!TryProject(target, viewport, out x, out y, out width, out height))
            return false;
        if (target.UserInterfaceCanvas is not { } canvas ||
            UIClipRegion.ResolveCrop(target.BoundableTransform, false) is not { } crop)
            return true;
        if (canvas.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen)
            return true;

        Vector2 canvasSize = canvas.CanvasTransform.ActualSize;
        if (canvasSize.X <= 0 || canvasSize.Y <= 0)
            return false;
        Matrix4x4 inverse = canvas.CanvasTransform.InverseWorldMatrix;
        Vector2 a = Vector2.Transform(new Vector2(crop.MinX, crop.MinY), inverse) / canvasSize;
        Vector2 b = Vector2.Transform(new Vector2(crop.MinX, crop.MaxY), inverse) / canvasSize;
        Vector2 c = Vector2.Transform(new Vector2(crop.MaxX, crop.MinY), inverse) / canvasSize;
        Vector2 d = Vector2.Transform(new Vector2(crop.MaxX, crop.MaxY), inverse) / canvasSize;
        float clipLeft = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X));
        float clipRight = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
        float clipTop = 1 - MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y));
        float clipBottom = 1 - MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y));
        float right = MathF.Min(x + width, clipRight);
        float bottom = MathF.Min(y + height, clipBottom);
        x = MathF.Max(x, clipLeft);
        y = MathF.Max(y, clipTop);
        width = right - x;
        height = bottom - y;
        return float.IsFinite(x) && float.IsFinite(y) && width > 0 && height > 0;
    }

    public static bool TryProject(UIInteractableComponent target, XRViewport? viewport,
        out float x, out float y, out float width, out float height)
    {
        x = y = -1;
        width = height = 0;
        if (viewport is null || target.UserInterfaceCanvas is not { } canvas)
            return false;

        UIBoundableTransform transform = target.BoundableTransform;
        Vector2 size = transform.ActualSize;
        if (size.X <= 0 || size.Y <= 0)
            return false;

        if (canvas.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen)
            return TryProjectSpatial(transform, canvas, viewport, false,
                out x, out y, out width, out height);

        Vector2 canvasSize = canvas.CanvasTransform.ActualSize;
        if (canvasSize.X <= 0 || canvasSize.Y <= 0)
            return false;
        Vector2 p0 = transform.LocalToCanvas(Vector2.Zero) / canvasSize;
        Vector2 p1 = transform.LocalToCanvas(new Vector2(size.X, 0)) / canvasSize;
        Vector2 p2 = transform.LocalToCanvas(new Vector2(0, size.Y)) / canvasSize;
        Vector2 p3 = transform.LocalToCanvas(size) / canvasSize;

        float left = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float right = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float bottom = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float top = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        if (!float.IsFinite(left) || !float.IsFinite(right) || !float.IsFinite(bottom) || !float.IsFinite(top) ||
            right <= left || top <= bottom)
            return false;
        x = left;
        y = 1 - top;
        width = right - left;
        height = top - bottom;
        return true;
    }

    private static bool TryProjectSpatial(UIBoundableTransform transform, UICanvasComponent canvas,
        XRViewport viewport, bool clipVisible, out float x, out float y, out float width, out float height)
    {
        x = y = -1;
        width = height = 0;
        Vector2 size = transform.ActualSize;
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0 ||
            viewport.ActiveCamera is not { } camera ||
            !canvas.TryGetWorldToCanvasMatrix(out Matrix4x4 worldToCanvas, out Vector2 canvasSize) ||
            !Matrix4x4.Invert(worldToCanvas, out Matrix4x4 canvasToWorld))
            return false;

        // The offscreen texture flattens the UI layout into canvas coordinates.
        // Its displayed placement can differ from the authored transform, and its
        // anchor camera need not be the camera observing this viewport.
        Matrix4x4 localToCanvas = transform.WorldMatrix * canvas.CanvasTransform.InverseWorldMatrix;
        // A convex quad gains at most one vertex per clipping plane: four crop
        // planes plus six frustum planes and the positive-W guard fit in 16.
        Span<Vector2> polygon = stackalloc Vector2[16];
        Span<Vector2> scratch = stackalloc Vector2[16];
        polygon[0] = Vector2.Transform(Vector2.Zero, localToCanvas);
        polygon[1] = Vector2.Transform(new Vector2(size.X, 0), localToCanvas);
        polygon[2] = Vector2.Transform(size, localToCanvas);
        polygon[3] = Vector2.Transform(new Vector2(0, size.Y), localToCanvas);
        int count = 4;
        for (int i = 0; i < count; i++)
            if (!float.IsFinite(polygon[i].X) || !float.IsFinite(polygon[i].Y))
                return false;

        if (clipVisible)
        {
            float left = 0;
            float bottom = 0;
            float right = canvasSize.X;
            float top = canvasSize.Y;
            if (UIClipRegion.ResolveCrop(transform, false) is { } crop)
            {
                left = MathF.Max(left, crop.MinX);
                bottom = MathF.Max(bottom, crop.MinY);
                right = MathF.Min(right, crop.MaxX);
                top = MathF.Min(top, crop.MaxY);
            }
            if (!float.IsFinite(right) || !float.IsFinite(top) || right <= left || top <= bottom ||
                !ClipPolygon(polygon, scratch, ref count, new Vector3(1, 0, -left)) ||
                !ClipPolygon(polygon, scratch, ref count, new Vector3(-1, 0, right)) ||
                !ClipPolygon(polygon, scratch, ref count, new Vector3(0, 1, -bottom)) ||
                !ClipPolygon(polygon, scratch, ref count, new Vector3(0, -1, top)))
                return false;
        }

        // Match the camera coordinate helper's view/projection and clip-depth
        // convention. Clip before the divide so a near-plane intersection keeps
        // its visible polygon instead of rejecting the entire control.
        Matrix4x4 canvasToClip = canvasToWorld * camera.Transform.InverseWorldMatrix * camera.ProjectionMatrixUnjittered;
        Vector3 clipX = new(canvasToClip.M11, canvasToClip.M21, canvasToClip.M41);
        Vector3 clipY = new(canvasToClip.M12, canvasToClip.M22, canvasToClip.M42);
        Vector3 clipZ = new(canvasToClip.M13, canvasToClip.M23, canvasToClip.M43);
        Vector3 clipW = new(canvasToClip.M14, canvasToClip.M24, canvasToClip.M44);
        Vector3 near = RuntimeEngine.Rendering.EffectiveClipDepthRange == ERenderClipDepthRange.NegativeOneToOne
            ? clipZ + clipW : clipZ;
        if (!ClipPolygon(polygon, scratch, ref count, near) ||
            !ClipPolygon(polygon, scratch, ref count, clipW - clipZ) ||
            !ClipPolygon(polygon, scratch, ref count, clipW - new Vector3(0, 0, 0.000001f)))
            return false;
        if (clipVisible &&
            (!ClipPolygon(polygon, scratch, ref count, clipW + clipX) ||
             !ClipPolygon(polygon, scratch, ref count, clipW - clipX) ||
             !ClipPolygon(polygon, scratch, ref count, clipW + clipY) ||
             !ClipPolygon(polygon, scratch, ref count, clipW - clipY)))
            return false;

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        // Retain the shared lens mapping. These polygon bounds do not resolve
        // nonlinear edge extrema between vertices or scene-depth occlusion.
        for (int i = 0; i < count; i++)
        {
            Vector3 world = Vector3.Transform(new Vector3(polygon[i], 0), canvasToWorld);
            Vector3 projected = camera.WorldToNormalizedViewportCoordinate(world, true);
            if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y) || !float.IsFinite(projected.Z))
                return false;
            minX = MathF.Min(minX, projected.X);
            minY = MathF.Min(minY, projected.Y);
            maxX = MathF.Max(maxX, projected.X);
            maxY = MathF.Max(maxY, projected.Y);
        }
        if (clipVisible)
        {
            minX = MathF.Max(0, minX);
            minY = MathF.Max(0, minY);
            maxX = MathF.Min(1, maxX);
            maxY = MathF.Min(1, maxY);
        }
        if (maxX <= minX || maxY <= minY)
            return false;
        x = minX;
        y = 1 - maxY;
        width = maxX - minX;
        height = maxY - minY;
        return true;
    }

    private static bool ClipPolygon(Span<Vector2> polygon, Span<Vector2> scratch, ref int count, Vector3 plane)
    {
        int outputCount = 0;
        Vector2 previous = polygon[count - 1];
        float previousDistance = Vector2.Dot(previous, new Vector2(plane.X, plane.Y)) + plane.Z;
        if (!float.IsFinite(previousDistance))
            return false;
        for (int i = 0; i < count; i++)
        {
            Vector2 current = polygon[i];
            float distance = Vector2.Dot(current, new Vector2(plane.X, plane.Y)) + plane.Z;
            if (!float.IsFinite(distance))
                return false;
            if ((distance > 0 && previousDistance < 0) || (distance < 0 && previousDistance > 0))
            {
                if (outputCount == scratch.Length)
                    return false;
                float fraction = (float)(previousDistance / ((double)previousDistance - distance));
                scratch[outputCount++] = Vector2.Lerp(previous, current, fraction);
            }
            if (distance >= 0)
            {
                if (outputCount == scratch.Length)
                    return false;
                scratch[outputCount++] = current;
            }
            previous = current;
            previousDistance = distance;
        }
        count = outputCount;
        if (count < 3)
            return false;
        scratch[..count].CopyTo(polygon);
        return true;
    }
}

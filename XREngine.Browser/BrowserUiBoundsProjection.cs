using System.Numerics;
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

        Vector2 p0, p1, p2, p3;
        if (canvas.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
        {
            Vector2 canvasSize = canvas.CanvasTransform.ActualSize;
            if (canvasSize.X <= 0 || canvasSize.Y <= 0)
                return false;
            p0 = transform.LocalToCanvas(Vector2.Zero) / canvasSize;
            p1 = transform.LocalToCanvas(new Vector2(size.X, 0)) / canvasSize;
            p2 = transform.LocalToCanvas(new Vector2(0, size.Y)) / canvasSize;
            p3 = transform.LocalToCanvas(size) / canvasSize;
        }
        else
        {
            XRCamera? camera = canvas.CanvasTransform.DrawSpace == ECanvasDrawSpace.Camera
                ? canvas.CanvasTransform.CameraSpaceCamera ?? viewport.ActiveCamera
                : viewport.ActiveCamera;
            if (camera is null)
                return false;
            Vector3 v0 = camera.WorldToNormalizedViewportCoordinate(transform.LocalToWorld(new Vector3(0, 0, 0)), true);
            Vector3 v1 = camera.WorldToNormalizedViewportCoordinate(transform.LocalToWorld(new Vector3(size.X, 0, 0)), true);
            Vector3 v2 = camera.WorldToNormalizedViewportCoordinate(transform.LocalToWorld(new Vector3(0, size.Y, 0)), true);
            Vector3 v3 = camera.WorldToNormalizedViewportCoordinate(transform.LocalToWorld(new Vector3(size.X, size.Y, 0)), true);
            if (v0.Z is < 0 or > 1 || v1.Z is < 0 or > 1 || v2.Z is < 0 or > 1 || v3.Z is < 0 or > 1)
                return false;
            p0 = new Vector2(v0.X, v0.Y);
            p1 = new Vector2(v1.X, v1.Y);
            p2 = new Vector2(v2.X, v2.Y);
            p3 = new Vector2(v3.X, v3.Y);
        }

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
}

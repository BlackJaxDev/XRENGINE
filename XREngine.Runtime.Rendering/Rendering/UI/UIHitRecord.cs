using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>A frozen input record for one render record in a canvas generation.</summary>
public readonly record struct UIHitRecord(
    RenderInfo2D RenderInfo,
    long MembershipId,
    UIElementIdentity Identity,
    long InteractionRevision,
    Matrix4x4 CanvasToLocal,
    Vector2 Size,
    BoundingRectangleF Bounds,
    BoundingRectangle? Clip,
    int NodeIndex,
    int ParentIndex,
    int SubtreeEndIndex,
    int Depth,
    int LayerIndex,
    int IndexWithinLayer,
    bool IsInteractable,
    bool BlocksInputBehind,
    bool IsTabStop = true,
    int TabIndex = 0,
    UIElementIdentity FocusScopeIdentity = default,
    UIElementIdentity VirtualListIdentity = default,
    UIElementIdentity VirtualRowIdentity = default,
    UIVirtualRowLease VirtualRowLease = default,
    UIPointerGeometry PointerGeometry = default)
{
    /// <summary>Tests the frozen shape and clip in canvas coordinates.</summary>
    public bool Contains(Vector2 canvasPoint)
    {
        if (!float.IsFinite(canvasPoint.X) || !float.IsFinite(canvasPoint.Y) ||
            !float.IsFinite(Size.X) || !float.IsFinite(Size.Y) || Size.X <= 0 || Size.Y <= 0 ||
            !Bounds.Contains(canvasPoint))
            return false;
        Vector2 local = Vector2.Transform(canvasPoint, CanvasToLocal);
        if (!float.IsFinite(local.X) || !float.IsFinite(local.Y) ||
            local.X < 0 || local.Y < 0 || local.X > Size.X || local.Y > Size.Y)
            return false;
        return Clip is not { } crop ||
            canvasPoint.X >= crop.MinX && canvasPoint.X < crop.MaxX &&
            canvasPoint.Y >= crop.MinY && canvasPoint.Y < crop.MaxY;
    }

    /// <summary>Converts a local point using this presented record's frozen size.</summary>
    public bool TryNormalizeLocal(Vector2 localPoint, out Vector2 normalizedPoint)
    {
        normalizedPoint = default;
        if (!float.IsFinite(localPoint.X) || !float.IsFinite(localPoint.Y) ||
            !float.IsFinite(Size.X) || !float.IsFinite(Size.Y) || Size.X <= 0f || Size.Y <= 0f ||
            localPoint.X < 0f || localPoint.Y < 0f ||
            localPoint.X > Size.X || localPoint.Y > Size.Y)
            return false;
        normalizedPoint = localPoint / Size;
        return true;
    }

    /// <summary>Checks the frozen layout ancestry used by input blocking.</summary>
    public bool IsInSubtreeOf(in UIHitRecord ancestor)
        => NodeIndex >= ancestor.NodeIndex && NodeIndex < ancestor.SubtreeEndIndex;
}

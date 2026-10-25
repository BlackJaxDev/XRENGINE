using System.Numerics;
using XREngine.Data.Core;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    /// <summary>
    /// Tests a drop region against displayed geometry and the current input scope.
    /// A disabled region can supply geometry without receiving ordinary pointer input.
    /// </summary>
    public bool TryGetPresentedDropRegion(UIInteractableComponent region, Vector2 outputPosition,
        out Vector2 canvasPosition)
        => TryGetPresentedDropRegionCore(region, outputPosition, null, out canvasPosition);

    /// <summary>Also requires a full-client presentation with the sampled native client extent.</summary>
    public bool TryGetPresentedDropRegion(UIInteractableComponent region, Vector2 outputPosition,
        Vector2 expectedOutputSize, out Vector2 canvasPosition)
        => TryGetPresentedDropRegionCore(region, outputPosition, expectedOutputSize, out canvasPosition);

    private bool TryGetPresentedDropRegionCore(UIInteractableComponent region, Vector2 outputPosition,
        Vector2? expectedOutputSize, out Vector2 canvasPosition)
    {
        canvasPosition = default;
        if (region is null || !IsActiveInHierarchy || IsDestroyed || IsDestroyQueued ||
            _inputDeactivating || _inputTerminal ||
            region.SceneNode is not { IsTransformNull: false } originalNode ||
            originalNode.Transform is not UITransform originalTransform ||
            (Canvas ?? GetSiblingComponent<UICanvasComponent>(false)) is not { } originalCanvas)
            return false;
        ObjectConstructionIdentity inputIdentity = ObjectConstructionIdentity.Capture(this);
        ObjectConstructionIdentity canvasIdentity = ObjectConstructionIdentity.Capture(originalCanvas);
        ObjectConstructionIdentity regionIdentity = ObjectConstructionIdentity.Capture(region);
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(originalNode);
        ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(originalTransform);
        if (RefreshPresentedInput() is not { } publication)
            return false;
        try
        {
            if (!inputIdentity.IsCurrent || !canvasIdentity.IsCurrent || !regionIdentity.IsCurrent ||
                !nodeIdentity.IsCurrent || !transformIdentity.IsCurrent ||
                !IsActiveInHierarchy || IsDestroyed || IsDestroyQueued || _inputDeactivating ||
                _inputTerminal || !ReferenceEquals(publication, _inputPublication) ||
                region.IsDestroyed || region.IsDestroyQueued || !region.IsActiveInHierarchy ||
                region.SceneNode is not { IsDestroyed: false, IsDestroyQueued: false, IsTransformNull: false } node ||
                node.Transform is not UITransform transform ||
                !ReferenceEquals(node, originalNode) || !ReferenceEquals(transform, originalTransform) ||
                transform.IsDestroyed || transform.IsDestroyQueued || !transform.IsVisibleInHierarchy ||
                transform.GetCanvasComponent() is not { IsActiveInHierarchy: true } canvas ||
                !ReferenceEquals(canvas, originalCanvas) ||
                canvas.IsDestroyed || canvas.IsDestroyQueued || canvas.SceneNode.IsDestroyQueued ||
                !canvas.CanvasTransform.IsVisibleInHierarchy ||
                !ReferenceEquals(canvas, GetCameraCanvas()) ||
                !IsCurrentScrollRead(publication, _inputPublicationRevision, canvas, _inputOutput) ||
                !_inputRecords.TryGetValue(region.RenderInfo2D.MembershipId, out UIHitRecord record) ||
                !ReferenceEquals(record.RenderInfo.Owner, region) ||
                record.Identity != transform.ElementIdentity ||
                record.InteractionRevision != region.InteractionRevision ||
                record.FocusScopeIdentity != publication.ActiveFocusScopeIdentity)
                return false;
            if (expectedOutputSize is { } expected &&
                (!float.IsFinite(expected.X) || !float.IsFinite(expected.Y) ||
                 expected.X <= 0 || expected.Y <= 0 ||
                 publication.OutputRegion.MinX != 0 || publication.OutputRegion.MinY != 0 ||
                 publication.OutputRegion.Width != expected.X || publication.OutputRegion.Height != expected.Y))
                return false;
            if (!publication.TryMapPointer(outputPosition, out Vector2 point) ||
                !publication.CanvasBounds.Contains(point) || !record.Contains(point))
                return false;

            UIHitRecord? selected = FindPresentedPointerHitAndBlocker(publication, point, out UIHitRecord? blocker);
            if (blocker is { } front && !record.IsInSubtreeOf(front))
                return false;
            if (selected is { } top && !top.IsInSubtreeOf(record))
                return false;
            canvasPosition = point;
            return true;
        }
        finally { publication.Release(); }
    }
}

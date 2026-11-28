using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// The dock drop overlay of one stack. While a panel drag is active, the stack shows five drop targets in its
/// center, like ImGui docking: the center target adds the panel as a tab, and the four side targets split the stack
/// and put the panel on that side. A translucent preview shows the area that the panel gets.
/// </summary>
internal sealed partial class NativeDockTreeHost
{
    private const float DropZoneSize = 34.0f;
    private const float DropZoneSpacing = 40.0f;
    /// <summary>Draws the overlay over the panel content and under popups (see <see cref="UITransform.LayerZIndex"/>).</summary>
    private const int DropOverlayLayerZIndex = -3;

    private static ColorF4 DropPreviewFill => EditorGlassTheme.Accent with { A = 0.22f };
    private static ColorF4 DropZoneMark => EditorGlassTheme.Accent with { A = 0.9f };

    /// <summary>Returns the anchors of the area that a panel dropped on <paramref name="zone"/> gets in the stack.</summary>
    private static (Vector2 Min, Vector2 Max) DropZoneArea(ENativeDockDropZone zone) => zone switch
    {
        ENativeDockDropZone.Left => (Vector2.Zero, new Vector2(0.5f, 1.0f)),
        ENativeDockDropZone.Right => (new Vector2(0.5f, 0.0f), Vector2.One),
        ENativeDockDropZone.Top => (new Vector2(0.0f, 0.5f), Vector2.One),
        ENativeDockDropZone.Bottom => (Vector2.Zero, new Vector2(1.0f, 0.5f)),
        _ => (Vector2.Zero, Vector2.One),
    };

    private static Vector2 DropZoneOffset(ENativeDockDropZone zone) => zone switch
    {
        ENativeDockDropZone.Left => new Vector2(-DropZoneSpacing, 0.0f),
        ENativeDockDropZone.Right => new Vector2(DropZoneSpacing, 0.0f),
        ENativeDockDropZone.Top => new Vector2(0.0f, DropZoneSpacing),
        ENativeDockDropZone.Bottom => new Vector2(0.0f, -DropZoneSpacing),
        _ => Vector2.Zero,
    };

    /// <summary>
    /// Creates the drop overlay over <paramref name="cardNode"/> and registers its five targets. The overlay stays
    /// collapsed, so it takes no input, until <see cref="NativeDockDropOverlayComponent"/> sees a panel drag.
    /// </summary>
    private void CreateDropOverlay(SceneNode cardNode, NativeDockNode stack, ulong revision)
    {
        SceneNode overlayNode = cardNode.NewChildWithTransform(out UIBoundableTransform overlay, "DockDropOverlay");
        overlay.MinAnchor = Vector2.Zero;
        overlay.MaxAnchor = Vector2.One;
        overlay.Margins = new Vector4(4.0f);
        overlay.LayerZIndex = DropOverlayLayerZIndex;
        overlay.Visibility = EVisibility.Collapsed;
        overlayNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;
        overlayNode.AddComponent<NativeDockDropOverlayComponent>();

        SceneNode previewNode = overlayNode.NewChildWithTransform(out UIBoundableTransform preview, "DockDropPreview");
        preview.MinAnchor = Vector2.Zero;
        preview.MaxAnchor = Vector2.One;
        preview.Visibility = EVisibility.Hidden;
        preview.BlocksInputBehind = false;
        UIMaterialComponent previewQuad = previewNode.AddComponent<UIMaterialComponent>()!;
        EditorGlassTheme.ApplyStatic(previewQuad, new UIStyleProperties
        {
            BackgroundColor = DropPreviewFill,
            BorderWidth = 1.0f,
            BorderColor = DropZoneMark,
            CornerRadii = new Vector4(EditorGlassTheme.ControlRadius),
        });

        // The five targets sit in a cross at the stack center.
        SceneNode clusterNode = overlayNode.NewChildWithTransform(out UIBoundableTransform cluster, "DockDropTargets");
        cluster.MinAnchor = new Vector2(0.5f);
        cluster.MaxAnchor = new Vector2(0.5f);
        cluster.NormalizedPivot = new Vector2(0.5f);
        cluster.Width = DropZoneSpacing * 2.0f + DropZoneSize;
        cluster.Height = DropZoneSpacing * 2.0f + DropZoneSize;
        cluster.BlocksInputBehind = false;
        foreach (ENativeDockDropZone zone in Enum.GetValues<ENativeDockDropZone>())
            CreateDropZone(clusterNode, preview, stack, revision, zone);
    }

    private void CreateDropZone(SceneNode clusterNode, UIBoundableTransform preview, NativeDockNode stack,
        ulong revision, ENativeDockDropZone zone)
    {
        SceneNode zoneNode = clusterNode.NewChildWithTransform(out UIBoundableTransform zoneBounds, $"DockDrop{zone}");
        zoneBounds.MinAnchor = new Vector2(0.5f);
        zoneBounds.MaxAnchor = new Vector2(0.5f);
        zoneBounds.NormalizedPivot = new Vector2(0.5f);
        zoneBounds.Width = DropZoneSize;
        zoneBounds.Height = DropZoneSize;
        zoneBounds.Translation = DropZoneOffset(zone);
        UIMaterialComponent zoneQuad = zoneNode.AddComponent<UIMaterialComponent>()!;
        EditorGlassTheme.ApplyStatic(zoneQuad, EditorGlassTheme.MenuSurface);

        // Each target shows a small window outline with the side that the panel takes filled, like the ImGui
        // dock icons. The center target fills the whole outline.
        SceneNode outlineNode = zoneNode.NewChildWithTransform(out UIBoundableTransform outline, "DockDropOutline");
        outline.MinAnchor = Vector2.Zero;
        outline.MaxAnchor = Vector2.One;
        outline.Margins = new Vector4(6.0f);
        outline.BlocksInputBehind = false;
        UIMaterialComponent outlineQuad = outlineNode.AddComponent<UIMaterialComponent>()!;
        EditorGlassTheme.ApplyStatic(outlineQuad, new UIStyleProperties
        {
            BackgroundColor = DropPreviewFill,
            BorderWidth = 1.0f,
            BorderColor = DropZoneMark,
            CornerRadii = new Vector4(3.0f),
        });
        (Vector2 markMin, Vector2 markMax) = DropZoneArea(zone);
        SceneNode markNode = outlineNode.NewChildWithTransform(out UIBoundableTransform mark, "DockDropMark");
        mark.MinAnchor = markMin;
        mark.MaxAnchor = markMax;
        mark.Margins = new Vector4(2.0f);
        mark.BlocksInputBehind = false;
        UIMaterialComponent markQuad = markNode.AddComponent<UIMaterialComponent>()!;
        EditorGlassTheme.ApplyStatic(markQuad, new UIStyleProperties
        {
            BackgroundColor = DropZoneMark,
            CornerRadii = new Vector4(2.0f),
        });

        // A disabled button is the hit region that the native dock drag tests against the presented input.
        UIButtonComponent probe = zoneNode.AddComponent<UIButtonComponent>()!;
        probe.IsEnabled = false;
        probe.IsTabStop = false;
        probe.AccessibilityHidden = true;
        ObjectConstructionIdentity probeIdentity = ObjectConstructionIdentity.Capture(probe);
        ObjectConstructionIdentity probeNodeIdentity = ObjectConstructionIdentity.Capture(zoneNode);
        ObjectConstructionIdentity probeTransformIdentity = ObjectConstructionIdentity.Capture(zoneBounds);

        void Hover(bool hovering)
        {
            if (!hovering)
            {
                preview.Visibility = EVisibility.Hidden;
                return;
            }
            (Vector2 min, Vector2 max) = DropZoneArea(zone);
            preview.MinAnchor = min;
            preview.MaxAnchor = max;
            preview.Visibility = EVisibility.Visible;
        }

        _dropRegistrations.Add(EditorDragDropUtility.RegisterNativeDockTarget(zoneBounds,
            payload => TryDropZone(payload, stack, revision, zone),
            payload => CanDropZone(payload, stack, revision, zone),
            (payload, point) => CanDropZone(payload, stack, revision, zone) &&
                TryHitDockTarget(payload, stack, revision, probe, zoneBounds,
                    probeIdentity, probeNodeIdentity, probeTransformIdentity, point),
            Hover));
        _dropRegistrations.Add(EditorDragDropUtility.RegisterDropTarget(zoneBounds,
            payload => TryDropZone(payload, stack, revision, zone),
            payload => CanDropZone(payload, stack, revision, zone),
            Hover));
    }

    /// <summary>
    /// Gets whether the dragged panel can go to <paramref name="zone"/> of <paramref name="stack"/>. The center
    /// accepts a panel from this dock or another editor window. The sides accept only a panel of this dock, and only
    /// when the split leaves a panel on the other side.
    /// </summary>
    private bool CanDropZone(EditorDragDropUtility.DragPayload payload, NativeDockNode stack, ulong revision,
        ENativeDockDropZone zone)
    {
        string? panel;
        if (NativeDockTabPayload.Read(payload) is { } tab)
        {
            if (!CanDropTab(payload, stack, revision))
                return false;
            if (!ReferenceEquals(tab.Source, this))
                return zone == ENativeDockDropZone.Center;
            panel = tab.Panel;
        }
        else if (NativeDockLocalTabPayload.Read(payload) is { } local)
        {
            if (!CanDropLocalTab(payload, stack, revision))
                return false;
            panel = local.Panel;
        }
        else
            return false;
        return zone == ENativeDockDropZone.Center
            ? !stack.Tabs.Contains(panel)
            : stack.Tabs.Count > 1 || !stack.Tabs.Contains(panel);
    }

    private bool TryDropZone(EditorDragDropUtility.DragPayload payload, NativeDockNode stack, ulong revision,
        ENativeDockDropZone zone)
    {
        if (!CanDropZone(payload, stack, revision, zone))
            return false;
        if (zone == ENativeDockDropZone.Center)
            return NativeDockTabPayload.Read(payload) is not null
                ? TryDropTab(payload, stack, revision)
                : TryDropLocalTab(payload, stack, revision);
        string? panel = NativeDockTabPayload.Read(payload)?.Panel ?? NativeDockLocalTabPayload.Read(payload)?.Panel;
        bool vertical = zone is ENativeDockDropZone.Top or ENativeDockDropZone.Bottom;
        bool before = zone is ENativeDockDropZone.Left or ENativeDockDropZone.Top;
        if (panel is null || !_tree.SplitPanel(panel, stack.Id, vertical, before))
            return false;
        CommitStructure();
        return true;
    }
}

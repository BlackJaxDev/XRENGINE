using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>Reuses tab buttons while a host updates stable tab IDs and order.</summary>
[RequiresTransform(typeof(UIListTransform))]
public sealed partial class UITabStripComponent : UIComponent
{
    private sealed class Slot
    {
        public required UIBoundableTransform Bounds;
        public required UITabButtonComponent Button;
        public required UITextComponent Label;
        public required UITextComponent Icon;
        public ulong Id;
    }

    private readonly List<Slot> _slots = [];
    private readonly HashSet<ulong> _ids = [];
    private int _count;
    private ulong? _selectedId;
    private bool _settingTabs;
    private UITabItem[]? _pendingTabs;
    private ulong? _pendingSelectedId;
    private ulong _acceptedTabsRevision;

    public float TabWidth { get; set; } = 100;
    public float TabHeight { get; set; } = 28;
    /// <summary>Text size of the tab labels.</summary>
    public float LabelFontSize { get; set; } = 12;
    /// <summary>
    /// The icon font of the tab icons (<see cref="UITabItem.Icon"/>). Set it before the first
    /// <see cref="SetTabs"/> call. When null, tabs show no icons.
    /// </summary>
    public FontGlyphSet? IconFont { get; set; }
    private const float IconSize = 13.0f;
    private const float IconInset = 10.0f;
    /// <summary>
    /// Style of a tab that is not selected. When null, tabs use a flat gray fill. Set the styles before the
    /// first <see cref="SetTabs"/> call; a later change applies at the next call.
    /// </summary>
    public UIStyle? TabStyle { get; set; }
    /// <summary>Style of the selected tab. When null, the selected tab uses <see cref="TabStyle"/>.</summary>
    public UIStyle? SelectedTabStyle { get; set; }
    public int Count => _count;
    public ulong? SelectedId => _selectedId;

    /// <summary>Fires when a user activates an open or closed tab.</summary>
    public event Action<ulong>? TabActivated;
    public event Action<ulong>? CloseRequested;
    public event Action<ulong, int>? MoveRequested;

    /// <summary>Updates tab order and state. The caller owns panel visibility and persistence.</summary>
    public void SetTabs(ReadOnlySpan<UITabItem> tabs, ulong? selectedId)
    {
        if (IsDestroyed || IsDestroyQueued)
            return;
        if (_settingTabs)
        {
            ValidateTabRequest(tabs, selectedId);
            SetField(ref _pendingTabs, tabs.ToArray(), publishNotifications: false);
            SetField(ref _pendingSelectedId, selectedId, publishNotifications: false);
            return;
        }
        SetField(ref _settingTabs, true, publishNotifications: false);
        try
        {
            ReadOnlySpan<UITabItem> next = tabs;
            ulong? nextSelected = selectedId;
            for (int transition = 0; transition < 32; transition++)
            {
                SetTabsCore(next, nextSelected);
                if (IsDestroyed || IsDestroyQueued || _pendingTabs is not { } queued)
                    return;
                next = queued;
                nextSelected = _pendingSelectedId;
                SetField(ref _pendingTabs, null, publishNotifications: false);
                SetField(ref _pendingSelectedId, null, publishNotifications: false);
            }
            throw new InvalidOperationException("Tab callbacks requested too many updates.");
        }
        finally
        {
            SetField(ref _pendingTabs, null, publishNotifications: false);
            SetField(ref _pendingSelectedId, null, publishNotifications: false);
            SetField(ref _settingTabs, false, publishNotifications: false);
        }
    }

    private static void ValidateTabRequest(ReadOnlySpan<UITabItem> tabs, ulong? selectedId)
    {
        HashSet<ulong> ids = [];
        for (int index = 0; index < tabs.Length; index++)
            if (tabs[index].Id == 0 || !ids.Add(tabs[index].Id))
                throw new ArgumentException("Tab IDs must be unique and nonzero.", nameof(tabs));
        if (selectedId is { } selected && !ids.Contains(selected))
            throw new ArgumentException("The selected tab must exist.", nameof(selectedId));
    }

    private void SetTabsCore(ReadOnlySpan<UITabItem> tabs, ulong? selectedId)
    {
        UIListTransform list = TransformAs<UIListTransform>(true) ??
            throw new InvalidOperationException("A tab strip needs a UIListTransform.");
        if (!float.IsFinite(TabWidth) || TabWidth <= 0 || !float.IsFinite(TabHeight) || TabHeight <= 0)
            throw new InvalidOperationException("Tab dimensions must be positive and finite.");
        _ids.Clear();
        for (int index = 0; index < tabs.Length; index++)
            if (tabs[index].Id == 0 || !_ids.Add(tabs[index].Id))
                throw new ArgumentException("Tab IDs must be unique and nonzero.", nameof(tabs));
        if (selectedId is { } selected && !_ids.Contains(selected))
            throw new ArgumentException("The selected tab must exist.", nameof(selectedId));

        var focusRequest = CaptureTabFocusRequest();

        list.DisplayHorizontal = true;
        list.ItemAlignment = EListAlignment.TopOrLeft;
        list.ItemSize = TabWidth;
        while (_slots.Count < tabs.Length)
        {
            Slot created = CreateSlot(list);
            _slots.Add(created);
            if (IsDestroyed || IsDestroyQueued)
                return;
        }
        for (int index = 0; index < _slots.Count; index++)
        {
            Slot slot = _slots[index];
            if (index >= tabs.Length)
            {
                if (slot.Id != 0)
                    slot.Button.CancelForRecycle();
                if (IsDestroyed || IsDestroyQueued)
                    return;
                slot.Id = 0;
                slot.Bounds.Visibility = EVisibility.Collapsed;
                if (IsDestroyed || IsDestroyQueued)
                    return;
                continue;
            }
            UITabItem tab = tabs[index];
            if (slot.Id != tab.Id)
                slot.Button.CancelForRecycle();
            if (IsDestroyed || IsDestroyQueued)
                return;
            slot.Id = tab.Id;
            slot.Label.Text = (tab.IsOpen ? string.Empty : "+ ") + (tab.Title ?? string.Empty);
            if (IsDestroyed || IsDestroyQueued)
                return;
            // An icon sits at the left of the tab, and the title moves right of it.
            string icon = IconFont is null ? string.Empty : tab.Icon ?? string.Empty;
            if (slot.Icon.Text != icon)
                slot.Icon.Text = icon;
            float labelLeft = icon.Length > 0 ? IconInset + IconSize + 2.0f : 6.0f;
            Vector4 labelMargins = slot.Label.BoundableTransform.Margins;
            if (labelMargins.X != labelLeft)
                slot.Label.BoundableTransform.Margins = labelMargins with { X = labelLeft };
            if (IsDestroyed || IsDestroyQueued)
                return;
            slot.Button.AccessibilityLabel = tab.Title;
            slot.Button.AccessibilityDescription = tab.IsOpen ? null : "Closed tab. Activate to reopen.";
            bool isSelected = tab.Id == selectedId;
            slot.Button.IsSelected = isSelected;
            UIStyle? style = isSelected ? SelectedTabStyle ?? TabStyle : TabStyle;
            if (style is not null && !ReferenceEquals(slot.Button.Style, style))
                slot.Button.Style = style;
            slot.Bounds.Visibility = EVisibility.Visible;
            slot.Bounds.Height = TabHeight;
            if (IsDestroyed || IsDestroyQueued)
                return;
        }
        _count = tabs.Length;
        _selectedId = selectedId;
        ulong nextRevision = unchecked(_acceptedTabsRevision + 1);
        SetField(ref _acceptedTabsRevision, nextRevision == 0 ? 1UL : nextRevision,
            publishNotifications: false);
        RestoreTabFocusAfterUpdate(focusRequest, _acceptedTabsRevision);
    }

    public bool RequestCloseSelected()
    {
        if (_selectedId is not { } id)
            return false;
        CloseRequested?.Invoke(id);
        return true;
    }

    public bool RequestMoveSelected(int direction)
    {
        if (_selectedId is not { } id || direction == 0)
            return false;
        int index = FindIndex(id);
        if (index < 0 || index + Math.Sign(direction) < 0 || index + Math.Sign(direction) >= _count)
            return false;
        MoveRequested?.Invoke(id, Math.Sign(direction));
        return true;
    }

    private int FindIndex(ulong id)
    {
        for (int index = 0; index < _count; index++)
            if (_slots[index].Id == id)
                return index;
        return -1;
    }

    private Slot CreateSlot(UIListTransform list)
    {
        SceneNode node = new(list.SceneNode!) { Name = "Tab" };
        try
        {
            UIBoundableTransform bounds = node.SetTransform<UIBoundableTransform>();
            bounds.MinAnchor = Vector2.Zero;
            bounds.MaxAnchor = Vector2.Zero;
            bounds.Width = TabWidth;
            bounds.Height = TabHeight;
            bounds.Margins = TabStyle is null ? Vector4.Zero : new Vector4(2, 2, 2, 2);
            node.AddComponent<UIMaterialComponent>()!.SetBatchSolidColor(TabStyle is null
                ? new ColorF4(0.18f, 0.18f, 0.18f, 1)
                : ColorF4.Transparent);
            UITabButtonComponent button = node.AddComponent<UITabButtonComponent>()!;
            button.DefaultTextColor = ColorF4.White;
            SceneNode textNode = new(node) { Name = "TabLabel" };
            UIBoundableTransform labelBounds = textNode.SetTransform<UIBoundableTransform>();
            labelBounds.MinAnchor = Vector2.Zero;
            labelBounds.MaxAnchor = Vector2.One;
            labelBounds.Margins = new Vector4(6, 0, 6, 0);
            UITextComponent label = textNode.AddComponent<UITextComponent>()!;
            label.FontSize = LabelFontSize;
            label.Color = ColorF4.White;
            label.VerticalAlignment = EVerticalAlignment.Center;
            if (TabStyle is not null)
                label.HorizontalAlignment = EHorizontalAlignment.Center;
            SceneNode iconNode = new(node) { Name = "TabIcon" };
            UIBoundableTransform iconBounds = iconNode.SetTransform<UIBoundableTransform>();
            iconBounds.MinAnchor = new Vector2(0.0f, 0.5f);
            iconBounds.MaxAnchor = new Vector2(0.0f, 0.5f);
            iconBounds.NormalizedPivot = new Vector2(0.0f, 0.5f);
            iconBounds.Translation = new Vector2(IconInset, 0.0f);
            iconBounds.Width = IconSize;
            iconBounds.Height = IconSize;
            iconBounds.BlocksInputBehind = false;
            UITextComponent iconText = iconNode.AddComponent<UITextComponent>()!;
            if (IconFont is { } iconFont)
                iconText.Font = iconFont;
            iconText.FontSize = IconSize;
            iconText.Color = new ColorF4(1.0f, 1.0f, 1.0f, 0.8f);
            iconText.HorizontalAlignment = EHorizontalAlignment.Center;
            iconText.VerticalAlignment = EVerticalAlignment.Center;
            Slot slot = new() { Bounds = bounds, Button = button, Label = label, Icon = iconText };
            button.InteractAction += _ =>
            {
                if (slot.Id != 0)
                    TabActivated?.Invoke(slot.Id);
            };
            button.KeyboardCloseRequested += sender =>
            {
                if (ReferenceEquals(sender, slot.Button) && slot.Id != 0 && FindIndex(slot.Id) >= 0)
                    CloseRequested?.Invoke(slot.Id);
            };
            button.KeyboardMoveRequested += (sender, direction) =>
            {
                if (!ReferenceEquals(sender, slot.Button) || slot.Id == 0 || direction == 0)
                    return;
                int index = FindIndex(slot.Id);
                int next = index + Math.Sign(direction);
                if (index >= 0 && next >= 0 && next < _count)
                    MoveRequested?.Invoke(slot.Id, Math.Sign(direction));
            };
            return slot;
        }
        catch (Exception error)
        {
            try { node.Destroy(true); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Tab slot creation and cleanup failed.", error, cleanupError);
            }
            throw;
        }
    }
}

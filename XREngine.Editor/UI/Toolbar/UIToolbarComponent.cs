using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Editor.UI.Toolbar;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI.Components;

/// <summary>
/// The root component for the desktop editor.
/// </summary>
[RequiresTransform(typeof(UIBoundableTransform))]
public partial class UIToolbarComponent : UIComponent
{
    public UIBoundableTransform BoundableTransform => TransformAs<UIBoundableTransform>(true)!;

    private List<ToolbarItemBase> _rootMenuOptions = [];
    public List<ToolbarItemBase> RootMenuOptions
    {
        get => _rootMenuOptions;
        set => SetField(ref _rootMenuOptions, value);
    }

    public List<ToolbarButton> ActiveSubmenus { get; } = [];
    private readonly List<(ToolbarItemBase Item, UIButtonComponent Button)> _bindings = [];
    private bool _releasingBindings;
    private bool _remaking;
    private bool _remakePending;
    private bool _remakeTickRegistered;
    private bool _focusCheckRegistered;
    private bool _bindingsStopped;
    private bool _destroying;
    private long _bindingGeneration;

    private float _menuHeight = 34.0f;
    public float SubmenuItemHeight
    {
        get => _menuHeight;
        set => SetField(ref _menuHeight, value);
    }

    private const float Margin = 4.0f;
    /// <summary>Height of one drop-down menu item, close to the ImGui menu item height.</summary>
    private const float DropdownItemHeight = 24.0f;
    /// <summary>Height of a separator row in a drop-down menu. The line sits at its middle.</summary>
    private const float DropdownSeparatorHeight = 9.0f;
    private const float DropdownFontSize = 13.0f;
    private const float DropdownShortcutFontSize = 12.0f;
    /// <summary>Left inset of a drop-down label. The check mark column sits in it.</summary>
    private const float DropdownLabelLeft = 26.0f;
    private const float DropdownRightInset = 12.0f;
    private const float DropdownChevronSpace = 16.0f;
    private const float DropdownShortcutGap = 28.0f;
    private const float DropdownMinWidth = 180.0f;
    /// <summary>Z index layer of drop-down menus. It is below zero, so menus draw over panels, and above the popup layers.</summary>
    private const int DropdownLayerZIndex = -5;
    /// <summary>One shared menu item style, so menu buttons do not each build their own copy.</summary>
    private static readonly UIStyle SharedMenuItemStyle = EditorGlassTheme.MenuItem;

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(RootMenuOptions):
                RemakeToolbarItems();
                break;
        }
    }

    protected override void OnComponentActivated()
    {
        if (_destroying)
            return;
        _bindingsStopped = false;
        base.OnComponentActivated();
        RemakeToolbarItems();
    }
    protected override void OnComponentDeactivated()
    {
        try { base.OnComponentDeactivated(); }
        finally { StopBindings(); }
    }

    protected override void OnDestroying()
    {
        _destroying = true;
        try { StopBindings(); }
        finally { base.OnDestroying(); }
    }

    private void StopBindings()
    {
        _bindingsStopped = true;
        _bindingGeneration++;
        CancelDeferredRemake();
        ReleaseBindings();
    }

    private bool BindItem(ToolbarItemBase item, UIButtonComponent button)
    {
        if (_releasingBindings || _bindingsStopped || _destroying || IsDestroyed)
            return false;
        long generation = _bindingGeneration;
        _bindings.Add((item, button));
        item.InteractableComponent = button;
        if (!BindingIsCurrent(generation, button) || !ReferenceEquals(item.InteractableComponent, button))
        {
            ReleaseStaleBinding(item, button);
            return false;
        }
        item.ParentToolbarComponent = this;
        if (!BindingIsCurrent(generation, button) || !ReferenceEquals(item.ParentToolbarComponent, this))
        {
            ReleaseStaleBinding(item, button);
            return false;
        }
        return true;
    }

    private bool BindingIsCurrent(long generation, UIButtonComponent button)
        => generation == _bindingGeneration && !_bindingsStopped && !_destroying && !IsDestroyed && !button.IsDestroyed;

    private void ReleaseStaleBinding(ToolbarItemBase item, UIButtonComponent button)
    {
        if (ReferenceEquals(item.InteractableComponent, button))
            item.InteractableComponent = null;
        if (ReferenceEquals(item.InteractableComponent, button))
            throw new InvalidOperationException("A toolbar item refused to release a stale control.");
        if (ReferenceEquals(item.ParentToolbarComponent, this))
            item.ParentToolbarComponent = null;
    }

    private void ReleaseBindings()
    {
        if (_releasingBindings)
            return;
        _releasingBindings = true;
        try
        {
            foreach (var (item, button) in _bindings)
            {
                if (!ReferenceEquals(item.InteractableComponent, button))
                    continue;
                if (item is ToolbarButton menu)
                    menu.ChildOptionsVisible = false;
                item.InteractableComponent = null;
                if (ReferenceEquals(item.InteractableComponent, button))
                    throw new InvalidOperationException("A toolbar item refused to release its retired control.");
                if (ReferenceEquals(item.ParentToolbarComponent, this))
                    item.ParentToolbarComponent = null;
            }
            _bindings.Clear();
            ActiveSubmenus.Clear();
        }
        finally
        {
            _releasingBindings = false;
            ScheduleDeferredRemake();
        }
    }

    public void RemakeToolbarItems()
    {
        if (_bindingsStopped || _destroying || IsDestroyed)
            return;
        if (_remaking || _releasingBindings)
        {
            _bindingGeneration++;
            _remakePending = true;
            return;
        }
        _remaking = true;
        long generation = ++_bindingGeneration;
        try
        {
            ReleaseBindings();
            foreach (var child in SceneNode.Transform.Children.ToArray())
                if (child.SceneNode is { } owned)
                {
                    owned.Destroy(true);
                    if (!owned.IsDestroyed)
                        throw new InvalidOperationException("A toolbar child refused teardown; the toolbar was not rebuilt.");
                }
            if (generation != _bindingGeneration || _bindingsStopped || IsDestroyed)
                return;
            CreateMenu(SceneNode, true, null, null, RootMenuOptions, false, SubmenuItemHeight, this);
        }
        finally
        {
            _remaking = false;
            ScheduleDeferredRemake();
        }
    }

    private void ScheduleDeferredRemake()
    {
        if (_remaking || _releasingBindings || !_remakePending || _bindingsStopped ||
            _destroying || IsDestroyed || _remakeTickRegistered)
            return;
        _remakeTickRegistered = true;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, CompleteDeferredRemake);
    }

    private void CompleteDeferredRemake()
    {
        bool pending = _remakePending;
        CancelDeferredRemake();
        if (pending)
            RemakeToolbarItems();
    }

    private void CancelDeferredRemake()
    {
        _remakePending = false;
        if (!_remakeTickRegistered)
            return;
        _remakeTickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompleteDeferredRemake);
    }

    /// <summary>
    /// Asks for a check, after the current focus change, that closes each open menu whose items and submenus have
    /// no focus. A menu control calls this when its focus changes.
    /// </summary>
    internal void RequestMenuFocusCheck()
    {
        if (_focusCheckRegistered || _destroying || IsDestroyed)
            return;
        _focusCheckRegistered = true;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, CompleteMenuFocusCheck);
    }

    private void CompleteMenuFocusCheck()
    {
        _focusCheckRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompleteMenuFocusCheck);
        // A menu stays open while the focus is anywhere in its menu bar menu, so a submenu that the pointer opened
        // stays open while the focus is on another item of the same menu tree, like ImGui. A closing menu closes
        // its submenus.
        for (int index = ActiveSubmenus.Count - 1; index >= 0; index--)
        {
            if (index >= ActiveSubmenus.Count)
                continue;
            ToolbarButton menu = ActiveSubmenus[index];
            ToolbarButton root = menu;
            while (root.Parent is { } parent)
                root = parent;
            if (!root.AnyOptionsFocused)
                menu.ChildOptionsVisible = false;
        }
    }

    public UIListTransform CreateMenu(
        SceneNode parentNode,
        bool horizontal,
        float? width,
        float? height,
        IList<ToolbarItemBase> options,
        bool alignSubmenuToSide,
        float? menuHeight,
        UIToolbarComponent toolbar)
    {
        var listNode = parentNode.NewChild<UIMaterialComponent>(out var menuMat);
        if (!horizontal)
            menuMat.BatchSolidRenderPass = (int)EDefaultRenderPass.OnTopForward;
        // The root list is the menu bar; a vertical list is a drop-down menu surface.
        EditorGlassTheme.ApplyStatic(menuMat, horizontal ? EditorGlassTheme.MenuBar : EditorGlassTheme.MenuSurface);
        var listTfm = listNode.SetTransform<UIListTransform>();
        listTfm.DisplayHorizontal = horizontal;
        listTfm.ItemSpacing = horizontal ? 2.0f : 0.0f;
        listTfm.Padding = horizontal ? new Vector4(6.0f, 0.0f, 6.0f, 0.0f) : new Vector4(4.0f);
        listTfm.ItemAlignment = EListAlignment.TopOrLeft;
        // For horizontal lists, ItemSize is the WIDTH per item. Use null to let each
        // item auto-size from its content (DesiredSize.X / text width).
        // For vertical submenus, ItemSize is the HEIGHT per item (menuHeight = 34px).
        // Drop-down rows set their own heights, so separators can be thinner than items.
        listTfm.ItemSize = null;
        // A drop-down draws over panel content that shares its pass, such as the scene panel overlay buttons.
        if (!horizontal)
            listTfm.LayerZIndex = DropdownLayerZIndex;
        listTfm.Width = width ?? (horizontal ? null : MeasureDropdownWidth(options));
        listTfm.Height = height;
        CreateChildMenu(options, listNode, !horizontal, toolbar);
        return listTfm;
    }

    /// <summary>
    /// Returns the width that fits the widest item of a drop-down menu: the label, the shortcut text, and the
    /// submenu chevron, with the check mark column and the side insets.
    /// </summary>
    private static float MeasureDropdownWidth(IList<ToolbarItemBase> options)
    {
        FontGlyphSet font = FontGlyphSet.LoadDefaultFont();
        float widest = 0.0f;
        foreach (ToolbarItemBase option in options)
        {
            if (option is not ToolbarButton item)
                continue;
            float row = UITextComponent.MeasureWidth(item.Text, font, DropdownFontSize);
            if (item.ShortcutText is { } shortcut)
                row += DropdownShortcutGap + UITextComponent.MeasureWidth(shortcut, font, DropdownShortcutFontSize);
            if (item.HasSubmenu)
                row += DropdownChevronSpace;
            widest = MathF.Max(widest, row);
        }
        return MathF.Max(DropdownMinWidth, MathF.Ceiling(widest + DropdownLabelLeft + DropdownRightInset + 8.0f + Margin * 2.0f));
    }

    //Works for both horizontal root menu and vertical submenus
    private void CreateChildMenu(
        IList<ToolbarItemBase> options,
        SceneNode listNode,
        bool alignSubmenuToSide,
        UIToolbarComponent toolbar)
    {
        //Create the buttons for each menu option.
        long generation = toolbar._bindingGeneration;
        foreach (ToolbarItemBase menuItem in options.ToArray())
        {
            if (generation != toolbar._bindingGeneration || toolbar._bindingsStopped || toolbar.IsDestroyed)
                break;
            switch (menuItem)
            {
                case ToolbarButton tbb:
                    CreateButton(listNode, alignSubmenuToSide, toolbar, tbb);
                    break;
                case ToolbarSeparator tbs:
                    CreateSeparator(listNode, alignSubmenuToSide, toolbar, tbs);
                    break;
                case ToolbarDropdown tbd:
                    CreateDropdown(listNode, alignSubmenuToSide, toolbar, tbd);
                    break;
            }
        }
    }

    private void CreateDropdown(SceneNode listNode, bool alignSubmenuToSide, UIToolbarComponent toolbar, ToolbarDropdown tbd)
    {
        var buttonNode = listNode.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);
        EditorUI.Styles.UpdateButton(button);
        if (!toolbar.BindItem(tbd, button))
            return;
        button.Name = tbd.Text;
        if (alignSubmenuToSide)
            background.BatchSolidRenderPass = (int)EDefaultRenderPass.OnTopForward;
        background.SetBatchSolidColor(ColorF4.Transparent);
        button.Style = SharedMenuItemStyle;
        var buttonTfm = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
        buttonTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
        buttonTfm.Margins = new Vector4(Margin);

        buttonNode.NewChild<UITextComponent>(out var text);
        var textTfm = text.BoundableTransform;
        textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
        text.FontSize = 14;
        text.Text = tbd.Text;
        if (alignSubmenuToSide)
            text.RenderPass = (int)EDefaultRenderPass.OnTopForward;

        if (tbd.Options.Length > 0)
        {
            UIListTransform submenuList = CreateMenu(buttonNode, false, null, null, [.. tbd.Options.Select(x => new ToolbarButton(x))], true, SubmenuItemHeight, toolbar);
            submenuList.BlocksInputBehind = true;
            submenuList.Visibility = EVisibility.Collapsed;
            submenuList.ExcludeFromParentAutoCalcHeight = true;
            submenuList.ExcludeFromParentAutoCalcWidth = true;
            //Undo margin from button
            submenuList.Translation = alignSubmenuToSide
                ? new Vector2(Margin, Margin)
                : new Vector2(-Margin, -Margin);
            //Align top left of submenu...
            submenuList.NormalizedPivot = new Vector2(0.0f, 1.0f);
            if (alignSubmenuToSide)
            {
                //...to top right of parent button
                submenuList.MaxAnchor = new Vector2(1.0f, 1.0f);
                submenuList.MinAnchor = new Vector2(1.0f, 1.0f);
            }
            else
            {
                //...to bottom left of parent button
                submenuList.MaxAnchor = new Vector2(0.0f, 0.0f);
                submenuList.MinAnchor = new Vector2(0.0f, 0.0f);
            }
        }
    }

    private void CreateSeparator(SceneNode listNode, bool alignSubmenuToSide, UIToolbarComponent toolbar, ToolbarSeparator tbs)
    {
        if (!alignSubmenuToSide)
        {
            var barSeparator = listNode.NewChild<UIMaterialComponent>(out var barMat);
            EditorGlassTheme.ApplyStatic(barMat, EditorGlassTheme.Divider);
            var barTfm = barSeparator.SetTransform<UIBoundableTransform>();
            barTfm.Padding = new Vector4(0.0f);
            barTfm.Width = 1.0f;
            barTfm.Height = SubmenuItemHeight;
            return;
        }
        // A drop-down separator is a short row with a thin line across its middle, inset from the sides, like an
        // ImGui separator. The row has no margins, because the list sums the raw row heights.
        SceneNode row = listNode.NewChild();
        var rowTfm = row.SetTransform<UIBoundableTransform>();
        rowTfm.MinAnchor = new Vector2(0.0f, 0.5f);
        rowTfm.MaxAnchor = new Vector2(1.0f, 0.5f);
        rowTfm.NormalizedPivot = new Vector2(0.0f, 0.5f);
        rowTfm.Height = DropdownSeparatorHeight;
        var lineNode = row.NewChild<UIMaterialComponent>(out var lineMat);
        lineMat.BatchSolidRenderPass = (int)EDefaultRenderPass.OnTopForward;
        EditorGlassTheme.ApplyStatic(lineMat, EditorGlassTheme.Divider);
        var lineTfm = lineNode.GetTransformAs<UIBoundableTransform>(true)!;
        lineTfm.MinAnchor = new Vector2(0.0f, 0.5f);
        lineTfm.MaxAnchor = new Vector2(1.0f, 0.5f);
        lineTfm.NormalizedPivot = new Vector2(0.0f, 0.5f);
        lineTfm.Height = 1.0f;
        lineTfm.Margins = new Vector4(8.0f, 0.0f, 8.0f, 0.0f);
    }

    private void CreateButton(SceneNode listNode, bool alignSubmenuToSide, UIToolbarComponent toolbar, ToolbarButton tbb)
    {
        var buttonNode = listNode.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);
        EditorUI.Styles.UpdateButton(button);
        if (!toolbar.BindItem(tbb, button))
            return;
        button.Name = tbb.Text;

        if (alignSubmenuToSide)
            background.BatchSolidRenderPass = (int)EDefaultRenderPass.OnTopForward;
        background.SetBatchSolidColor(ColorF4.Transparent);
        button.Style = SharedMenuItemStyle;

        var buttonTfm = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
        if (alignSubmenuToSide)
        {
            // A drop-down item fills the menu width, so its highlight is a full row like an ImGui menu item.
            buttonTfm.MinAnchor = new Vector2(0.0f, 0.5f);
            buttonTfm.MaxAnchor = new Vector2(1.0f, 0.5f);
            buttonTfm.NormalizedPivot = new Vector2(0.0f, 0.5f);
            buttonTfm.Height = DropdownItemHeight;
            buttonTfm.Margins = new Vector4(0.0f);
        }
        else
        {
            buttonTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
            buttonTfm.Margins = new Vector4(Margin);
        }

        if (alignSubmenuToSide)
        {
            UIMaterialComponent check = NativeUIElements.CreateIcon(buttonNode, EUIQuadIcon.Check, new Vector2(0.0f, 0.5f),
                new Vector2(DropdownLabelLeft * 0.5f, 0.0f), 11.0f, EditorGlassTheme.TextPrimary);
            // The pass applies to the next color write, so the color is written again in the drop-down pass.
            check.BatchSolidRenderPass = (int)EDefaultRenderPass.OnTopForward;
            check.SetBatchSolidColor(EditorGlassTheme.TextPrimary);
            check.BoundableTransform.Visibility = EVisibility.Hidden;
            tbb.CheckMark = check.BoundableTransform;
        }

        buttonNode.NewChild<UITextComponent>(out var text);
        var textTfm = text.BoundableTransform;
        text.Text = tbb.Text;
        text.Color = EditorGlassTheme.TextPrimary;
        text.VerticalAlignment = EVerticalAlignment.Center;
        if (alignSubmenuToSide)
        {
            textTfm.Margins = new Vector4(DropdownLabelLeft, 0.0f, DropdownRightInset, 0.0f);
            text.FontSize = DropdownFontSize;
            text.RenderPass = (int)EDefaultRenderPass.OnTopForward;
        }
        else
        {
            textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
            text.FontSize = 14;
        }
        tbb.Label = text;

        if (alignSubmenuToSide && tbb.ShortcutText is { } shortcut)
        {
            // The shortcut sits at the right of the row in a quieter color, like ImGui menu shortcuts.
            buttonNode.NewChild<UITextComponent>(out var shortcutText);
            shortcutText.Text = shortcut;
            shortcutText.FontSize = DropdownShortcutFontSize;
            shortcutText.Color = EditorGlassTheme.TextSecondary;
            shortcutText.HorizontalAlignment = EHorizontalAlignment.Right;
            shortcutText.VerticalAlignment = EVerticalAlignment.Center;
            shortcutText.BoundableTransform.Margins = new Vector4(DropdownLabelLeft, 0.0f, DropdownRightInset, 0.0f);
            shortcutText.RenderPass = (int)EDefaultRenderPass.OnTopForward;
        }

        if (alignSubmenuToSide && tbb.HasSubmenu)
        {
            UIMaterialComponent chevron = NativeUIElements.CreateIcon(buttonNode, EUIQuadIcon.ChevronRight, new Vector2(1.0f, 0.5f),
                new Vector2(-DropdownRightInset, 0.0f), 9.0f, EditorGlassTheme.TextSecondary);
            chevron.BatchSolidRenderPass = (int)EDefaultRenderPass.OnTopForward;
            chevron.SetBatchSolidColor(EditorGlassTheme.TextSecondary);
        }

        // The submenu is the last child of the item, so it draws over the other menu rows.
        if (tbb.HasSubmenu)
        {
            UIListTransform submenuList = CreateMenu(buttonNode, false, null, null, tbb.ChildOptions, true, SubmenuItemHeight, toolbar);
            submenuList.BlocksInputBehind = true;
            submenuList.Visibility = EVisibility.Collapsed;
            submenuList.ExcludeFromParentAutoCalcHeight = true;
            submenuList.ExcludeFromParentAutoCalcWidth = true;
            tbb.SubmenuTransform = submenuList;

            //Align top left of submenu...
            submenuList.NormalizedPivot = new Vector2(0.0f, 1.0f);
            if (alignSubmenuToSide)
            {
                //...to top right of parent item, with the menu padding, so the item rows line up
                submenuList.MaxAnchor = new Vector2(1.0f, 1.0f);
                submenuList.MinAnchor = new Vector2(1.0f, 1.0f);
                submenuList.Translation = new Vector2(2.0f, Margin);
            }
            else
            {
                //...to bottom left of parent button
                submenuList.MaxAnchor = new Vector2(0.0f, 0.0f);
                submenuList.MinAnchor = new Vector2(0.0f, 0.0f);
                //Undo margin from button
                submenuList.Translation = new Vector2(-Margin, -Margin);
            }
        }
        tbb.RefreshMenuState();
    }
}

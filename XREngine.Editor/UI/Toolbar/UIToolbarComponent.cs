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
            CreateMenu(SceneNode, true, null, SubmenuItemHeight, RootMenuOptions, false, SubmenuItemHeight, this);
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
        EditorPanel.ApplyBackground(menuMat);
        menuMat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
        var listTfm = listNode.SetTransform<UIListTransform>();
        listTfm.DisplayHorizontal = horizontal;
        listTfm.ItemSpacing = 0.0f;
        listTfm.Padding = new Vector4(0.0f);
        listTfm.ItemAlignment = EListAlignment.TopOrLeft;
        // For horizontal lists, ItemSize is the WIDTH per item. Use null to let each
        // item auto-size from its content (DesiredSize.X / text width).
        // For vertical submenus, ItemSize is the HEIGHT per item (menuHeight = 34px).
        listTfm.ItemSize = horizontal ? null : menuHeight;
        listTfm.Width = width;
        listTfm.Height = height;
        CreateChildMenu(options, listNode, alignSubmenuToSide, toolbar);
        return listTfm;
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
        background.SetBatchSolidColor(ColorF4.Transparent);
        background.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        var buttonTfm = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
        buttonTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
        buttonTfm.Margins = new Vector4(Margin);

        buttonNode.NewChild<UITextComponent>(out var text);
        var textTfm = text.BoundableTransform;
        textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
        text.FontSize = 14;
        text.Text = tbd.Text;

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
        var separatorNode = listNode.NewChild<UIMaterialComponent>(out var separatorMat);
        EditorPanel.ApplyBackground(separatorMat);
        var separatorTfm = separatorNode.SetTransform<UIBoundableTransform>();
        separatorTfm.Padding = new Vector4(0.0f);
        separatorTfm.Width = 1.0f;
        separatorTfm.Height = SubmenuItemHeight;
    }

    private void CreateButton(SceneNode listNode, bool alignSubmenuToSide, UIToolbarComponent toolbar, ToolbarButton tbb)
    {
        var buttonNode = listNode.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);
        EditorUI.Styles.UpdateButton(button);
        if (!toolbar.BindItem(tbb, button))
            return;
        button.Name = tbb.Text;

        background.SetBatchSolidColor(ColorF4.Transparent);
        background.RenderPass = (int)EDefaultRenderPass.TransparentForward;

        var buttonTfm = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
        buttonTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
        buttonTfm.Margins = new Vector4(Margin);

        buttonNode.NewChild<UITextComponent>(out var text);

        var textTfm = text.BoundableTransform;
        textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);

        text.FontSize = 14;
        text.Text = tbb.Text;
        text.Color = EditorUI.Styles.ButtonTextColor;

        if (tbb.ChildOptions.Count > 0)
        {
            UIListTransform submenuList = CreateMenu(buttonNode, false, null, null, tbb.ChildOptions, true, SubmenuItemHeight, toolbar);
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
}

using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
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
        base.OnComponentActivated();
        RemakeToolbarItems();
    }
    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        SceneNode.Transform.Clear();
    }

    public void RemakeToolbarItems()
    {
        SceneNode.Transform.Clear();
        //Create the root menu transform - this is a horizontal list of buttons.
        CreateMenu(SceneNode, true, null, SubmenuItemHeight, RootMenuOptions, false, SubmenuItemHeight, this);
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
        menuMat.Material = EditorPanel.MakeBackgroundMaterial();
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
        foreach (ToolbarItemBase menuItem in options)
        {
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
        tbd.InteractableComponent = button;
        tbd.ParentToolbarComponent = toolbar;
        button.Name = tbd.Text;
        var mat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Transparent);
        mat.EnableTransparency();
        background.Material = mat;
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
        separatorMat.Material = EditorPanel.MakeBackgroundMaterial();
        var separatorTfm = separatorNode.SetTransform<UIBoundableTransform>();
        separatorTfm.Padding = new Vector4(0.0f);
        separatorTfm.Width = 1.0f;
        separatorTfm.Height = SubmenuItemHeight;
    }

    private void CreateButton(SceneNode listNode, bool alignSubmenuToSide, UIToolbarComponent toolbar, ToolbarButton tbb)
    {
        var buttonNode = listNode.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);
        EditorUI.Styles.UpdateButton(button);
        tbb.InteractableComponent = button;
        tbb.ParentToolbarComponent = toolbar;
        button.Name = tbb.Text;

        var mat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Transparent);
        mat.EnableTransparency();
        background.Material = mat;

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

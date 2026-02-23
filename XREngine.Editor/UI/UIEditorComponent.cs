using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Core.Attributes;
using XREngine.Data.Rendering;
using XREngine.Editor.UI.Toolbar;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI.Components;

/// <summary>
/// The root component for the desktop editor.
/// </summary>
[RequiresTransform(typeof(UIBoundableTransform))]
public partial class UIEditorComponent : UIComponent
{
    private UIToolbarComponent? _toolbar;
    public UIToolbarComponent? Toolbar => _toolbar;

    private List<ToolbarItemBase> _rootMenuOptions = [];
    public List<ToolbarItemBase> RootMenuOptions
    {
        get => _rootMenuOptions;
        set => SetField(ref _rootMenuOptions, value);
    }

    private float _menuHeight = 34.0f;
    public float MenuHeight
    {
        get => _menuHeight;
        set => SetField(ref _menuHeight, value);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(MenuHeight):
                if (_toolbar is not null)
                    _toolbar.SubmenuItemHeight = MenuHeight;
                break;
            case nameof(RootMenuOptions):
                if (_toolbar is not null)
                    _toolbar.RootMenuOptions = RootMenuOptions;
                break;
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RemakeChildren();
    }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        SceneNode.Transform.Clear();
    }

    private InspectorPanel? _inspector;
    private HierarchyPanel? _hierarchy;

    public void RemakeChildren()
    {
        using var sample = Engine.Profiler.Start("UIEditorComponent.RemakeChildren");
        SceneNode.Transform.Clear();

        var splitChild = SceneNode.NewChildWithTransform(out UIDualSplitTransform splitTfm, "EditorRoot");
        splitTfm.VerticalSplit = true;
        splitTfm.FirstFixedSize = true;
        splitTfm.FixedSize = MenuHeight;
        splitTfm.SplitterSize = 0.0f;

        splitChild.NewChild(out UIToolbarComponent toolbarComp, "Menu");
        toolbarComp.RootMenuOptions = RootMenuOptions;
        toolbarComp.SubmenuItemHeight = MenuHeight;
        _toolbar = toolbarComp;

        if (EditorUnitTests.Toggles.VideoStreaming)
        {
            SceneNode videoRoot = splitChild.NewChildWithTransform<UIBoundableTransform>(out _, "VideoRoot");
            var videoComp = videoRoot.AddComponent<UIVideoComponent>();
            videoComp.StreamUrl = "https://twitch.tv/theava";
            if (EditorUnitTests.Toggles.VideoStreamingAudio)
                videoRoot.AddComponent<AudioSourceComponent>();

            return;
        }

        SceneNode dockNode = splitChild.NewChildWithTransform(out UIMultiSplitTransform dockTfm, "DockableRoot");
        dockTfm.Arrangement = UISplitArrangement.LeftMiddleRight;
        dockTfm.SplitterSize = 0.0f;
        dockTfm.CanUserResize = false;
        dockTfm.FixedSizeFirst = 300;
        dockTfm.FixedSizeSecond = 300;

        SceneNode hierarchyNode = dockNode.NewChildWithTransform<UIBoundableTransform>(out _, "Hierarchy");
        HierarchyPanel hierarchy;
        using (Engine.Profiler.Start("UIEditorComponent.RemakeChildren.HierarchyPanel"))
            hierarchyNode.NewChild<HierarchyPanel>(out hierarchy);
        if (World is not null)
            hierarchy.RootNodes = [.. World.RootNodes];
        _hierarchy = hierarchy;

        var middleNode = dockNode.NewChildWithTransform<UIBoundableTransform>(out _, "Scene");
        if (EditorUnitTests.Toggles.BackgroundShader)
        {
            XRShader bgShader = ShaderHelper.LoadEngineShader("UI/Backgrounds/Surf2.fs", EShaderType.Fragment);
            var mat = new XRMaterial(bgShader)
            {
                RenderPass = (int)EDefaultRenderPass.OnTopForward,
                RenderOptions = new RenderingParameters
                {
                    CullMode = ECullMode.None,
                    DepthTest = new DepthTest()
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        Function = EComparison.Always,
                        UpdateDepth = false
                    },
                    RequiredEngineUniforms = EUniformRequirements.RenderTime | EUniformRequirements.ViewportDimensions,
                }
            };
            var comp = middleNode.AddComponent<UIMaterialComponent>()!;
            comp.Material = mat;
        }

        //dockNode.NewChildWithTransform(out UIListTransform listTfm, out InspectorPanel inspector, "Inspector");
        //listTfm.DisplayHorizontal = false;
        //listTfm.ItemAlignment = EListAlignment.TopOrLeft;

        ////World.Name = "TestWorld";
        //_inspector = inspector;
        //inspector.InspectedObjects = [Engine.Rendering.Settings];

        ////Create the dockable windows transform for panels
        //var dockableNode = splitChild.NewChild<UIDockingRootComponent>(out var root);
        //var dock = root.DockingTransform;

        //var leftNode = dock.Left?.SceneNode;
        //if (leftNode is not null)
        //{
        //    leftNode.Transform.Clear();
        //    leftNode.NewChild<HierarchyPanel>(out var hierarchy);

        //    hierarchy.RootNodes.Clear();
        //    if (World is not null)
        //        hierarchy.RootNodes.AddRange(World.RootNodes);
        //}

        //var rightNode = dock.Right?.SceneNode;
        //if (rightNode is not null)
        //{
        //    rightNode.Transform.Clear();
        //    rightNode.NewChild<InspectorPanel>(out var inspector);
        //}

        //var bottomNode = dock.Bottom?.SceneNode;
        //if (bottomNode is not null)
        //{
        //    bottomNode.Transform.Clear();
        //    bottomNode.NewChild<ConsolePanel>(out var console);
        //}
    }

    /// <summary>
    /// The scene node that contains the menu options.
    /// </summary>
    public SceneNode ToolbarNode
    {
        get
        {
            var first = SceneNode.FirstChild;
            if (first is null)
                RemakeChildren();
            if (first!.Transform.Children.Count < 2)
                RemakeChildren();
            return first!.FirstChild!;
        }
    }
    /// <summary>
    /// The scene node that contains the dockable windows.
    /// </summary>
    public SceneNode DockableNode
    {
        get
        {
            var first = SceneNode.FirstChild;
            if (first is null)
                RemakeChildren();
            if (first!.Transform.Children.Count < 2)
                RemakeChildren();
            return first.LastChild!;
        }
    }
}

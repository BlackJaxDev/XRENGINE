using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class HierarchyPanel : EditorPanel
{
    private List<SceneNode> _rootNodes = [];
    public List<SceneNode> RootNodes
    {
        get => _rootNodes;
        set => SetField(ref _rootNodes, value);
    }

    private const float Margin = 4.0f;

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(RootNodes):
                RemakeChildren();
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

    public void RemakeChildren()
    {
        SceneNode.Transform.Clear();
        //Create the root menu transform - this is a horizontal list of buttons.
        CreateTree(SceneNode, this);
    }

    public float ItemHeight { get; set; } = 30.0f;

    public partial class NodeWrapper(SceneNode node) : UIComponent
    {
        private bool _highlighted;

        public SceneNode Node { get; set; } = node;
        public bool IsRoot => Node?.Parent is null;
        public bool IsLeaf => (Node?.Transform?.Children?.Count ?? 0) <= 0;
        public bool Highlighted
        {
            get => _highlighted;
            set => SetField(ref _highlighted, value);
        }
        public int Depth => Node?.Transform?.Depth ?? 0;
    }

    private void CreateTree(SceneNode parentNode, HierarchyPanel hierarchyPanel)
    {
        var listNode = parentNode.NewChild<UIMaterialComponent>(out var menuMat);
        menuMat.Material = MakeBackgroundMaterial();
        var listTfm = listNode.SetTransform<UIListTransform>();
        listTfm.DisplayHorizontal = false;
        listTfm.ItemSpacing = 0;
        listTfm.Padding = new Vector4(0.0f);
        listTfm.ItemAlignment = EListAlignment.TopOrLeft;
        listTfm.ItemSize = ItemHeight;
        listTfm.Width = 150;
        listTfm.Height = null;
        CreateNodes(listNode, RootNodes);
    }

    private static void CreateNodes(SceneNode listNode, IEnumerable<SceneNode> nodes)
    {
        var copy = nodes.Where(x => x is not null).ToList();
        foreach (SceneNode node in copy)
        {
            var buttonNode = listNode.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);
            button.Name = node.Name;

            var mat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Transparent);
            mat.EnableTransparency();
            background.Material = mat;

            var buttonTfm = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
            buttonTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
            buttonTfm.Margins = new Vector4(0.0f);

            buttonNode.NewChild<UITextComponent>(out var text);

            var textTfm = text.BoundableTransform;
            textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);

            text.FontSize = 14;
            text.Text = node.Name;
            textTfm.Translation = new Vector2(node.Transform.Depth * 15, 0.0f);
            textTfm.Width = null;
            textTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
            text.HorizontalAlignment = EHorizontalAlignment.Left;
            text.VerticalAlignment = EVerticalAlignment.Center;

            EditorUI.Styles.UpdateButton(button);
            CreateNodes(listNode, node.Transform.Children.Select(x => x.SceneNode!));
        }
    }
}

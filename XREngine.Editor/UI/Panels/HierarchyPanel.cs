using System.Collections.Generic;
using System.Numerics;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using static XREngine.Editor.UnitTestingWorld.UserInterface;

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
    private const int DepthIncrement = 10;
    private static readonly Vector4 DropTargetHighlight = new(0.25f, 0.55f, 0.95f, 0.35f);
    private static readonly Vector4 RootDropHighlight = new(0.18f, 0.18f, 0.18f, 0.30f);

    private readonly Dictionary<UIInteractableComponent, Vector2> _pendingDragStarts = new();
    private SceneNode? _lastClickedNode;
    private float _lastClickTime = float.NegativeInfinity;

    private bool _focusCameraOnDoubleClick = true;
    public bool FocusCameraOnDoubleClick
    {
        get => _focusCameraOnDoubleClick;
        set => SetField(ref _focusCameraOnDoubleClick, value);
    }

    private float _doubleClickThreshold = 0.45f;
    public float DoubleClickThreshold
    {
        get => _doubleClickThreshold;
        set => SetField(ref _doubleClickThreshold, MathF.Max(0.05f, value));
    }

    private float _doubleClickFocusDuration = 0.35f;
    public float DoubleClickFocusDuration
    {
        get => _doubleClickFocusDuration;
        set => SetField(ref _doubleClickFocusDuration, MathF.Max(0.01f, value));
    }

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
        EditorDragDropUtility.RegisterDropTarget(
            listTfm,
            HandleRootDrop,
            payload => EditorDragDropUtility.IsSceneNodePayload(payload),
            hovering => UpdateBackgroundHighlight(menuMat, hovering ? RootDropHighlight : Vector4.Zero));

        CreateNodes(listNode, RootNodes);
    }

    private void CreateNodes(SceneNode listNode, IEnumerable<SceneNode> nodes)
    {
        var copy = nodes.Where(x => x is not null).ToList();
        foreach (SceneNode node in copy)
        {
            string? nodeName = node.Name;
            if (string.IsNullOrWhiteSpace(nodeName))
                nodeName = FormatUnnamedNode(node);
            
            var buttonNode = listNode.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);
            button.Name = nodeName;
            button.UserData = node;
            button.InteractAction += HandleNodeButtonInteraction;

            var mat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Transparent);
            mat.EnableTransparency();
            background.Material = mat;

            var buttonTfm = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
            buttonTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
            buttonTfm.Margins = new Vector4(0.0f);

            buttonNode.NewChild<UITextComponent>(out var text);
            buttonNode.NewChild<UITextComponent>(out var dropPreview);

            var textTfm = text.BoundableTransform;
            textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);

            text.FontSize = 14;
            text.Text = nodeName;
            textTfm.Translation = new Vector2(node.Transform.Depth * DepthIncrement, 0.0f);
            textTfm.Width = null;
            textTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
            text.HorizontalAlignment = EHorizontalAlignment.Left;
            text.VerticalAlignment = EVerticalAlignment.Center;

            var previewTfm = dropPreview.BoundableTransform;
            previewTfm.MinAnchor = new Vector2(0.0f, 0.0f);
            previewTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
            previewTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
            dropPreview.FontSize = 12;
            dropPreview.HorizontalAlignment = EHorizontalAlignment.Right;
            dropPreview.VerticalAlignment = EVerticalAlignment.Center;
            dropPreview.Color = new ColorF4(1.0f, 1.0f, 1.0f, 0.75f);
            dropPreview.Text = string.Empty;

            EditorUI.Styles.UpdateButton(button);
            RegisterNodeDragHandlers(button, node);
            EditorDragDropUtility.RegisterDropTarget(
                buttonTfm,
                payload => HandleNodeDrop(node, payload),
                payload => CanAcceptNodeDrop(node, payload),
                hovering =>
                {
                    UpdateBackgroundHighlight(background, hovering ? DropTargetHighlight : Vector4.Zero);
                    UpdateDropPreview(dropPreview, hovering);
                });

            CreateNodes(listNode, node.Transform.Children.Select(x => x.SceneNode!));
        }
    }

    private void RegisterNodeDragHandlers(UIButtonComponent button, SceneNode node)
    {
        button.NeedsMouseMove = true;
        button.MouseMove += HandleMouseMove;
        button.MouseDirectOverlapLeave += HandleMouseLeave;

        void HandleMouseMove(float x, float y, UIInteractableComponent comp)
        {
            if (!EditorDragDropUtility.IsLeftMouseButtonHeld)
            {
                _pendingDragStarts.Remove(comp);
                return;
            }

            Vector2 canvasPoint = comp.BoundableTransform.LocalToCanvas(new Vector2(x, y));
            if (!_pendingDragStarts.TryGetValue(comp, out var start))
            {
                _pendingDragStarts[comp] = canvasPoint;
                return;
            }

            if (Vector2.Distance(start, canvasPoint) < EditorDragDropUtility.DefaultActivationDistance)
                return;

            var payload = EditorDragDropUtility.CreateSceneNodePayload(node);
            if (EditorDragDropUtility.BeginDrag(comp, payload, canvasPoint))
                _pendingDragStarts[comp] = canvasPoint;
        }

        void HandleMouseLeave(UIInteractableComponent comp)
            => _pendingDragStarts.Remove(comp);
    }

    private static bool HandleNodeDrop(SceneNode targetNode, EditorDragDropUtility.DragPayload payload)
    {
        if (!EditorDragDropUtility.TryGetSceneNode(payload, out var dragged) || dragged is null)
            return false;

        if (!CanAcceptNodeDrop(targetNode, dragged))
            return false;

        var destination = targetNode.Transform;
        EnqueueSceneEdit(() => dragged.Transform.Parent = destination);
        return true;
    }

    private static bool HandleRootDrop(EditorDragDropUtility.DragPayload payload)
    {
        if (!EditorDragDropUtility.TryGetSceneNode(payload, out var dragged) || dragged is null)
            return false;

        EnqueueSceneEdit(() => dragged.Transform.Parent = null);
        return true;
    }

    private static bool CanAcceptNodeDrop(SceneNode targetNode, EditorDragDropUtility.DragPayload payload)
    {
        if (!EditorDragDropUtility.TryGetSceneNode(payload, out var dragged) || dragged is null)
            return false;

        return CanAcceptNodeDrop(targetNode, dragged);
    }

    private static bool CanAcceptNodeDrop(SceneNode targetNode, SceneNode dragged)
    {
        if (targetNode == dragged)
            return false;

        return !IsDescendantOf(targetNode, dragged);
    }

    private static bool IsDescendantOf(SceneNode candidate, SceneNode potentialAncestor)
    {
        var parent = candidate.Transform.Parent;
        while (parent is not null)
        {
            if (parent.SceneNode == potentialAncestor)
                return true;
            parent = parent.Parent;
        }

        return false;
    }

    private static void UpdateBackgroundHighlight(UIMaterialComponent component, Vector4 color)
    {
        var mat = component.Material;
        mat?.SetVector4("MatColor", color);
    }

    private static void UpdateDropPreview(UITextComponent preview, bool hovering)
    {
        if (preview is null)
            return;

        if (!hovering)
        {
            preview.Text = string.Empty;
            return;
        }

        if (!EditorDragDropUtility.TryGetActiveSceneNode(out var dragged) || dragged is null)
        {
            preview.Text = "Drop";
            preview.Color = new ColorF4(1.0f, 1.0f, 1.0f, 0.45f);
            return;
        }

        SceneNode draggedNode = dragged!;

        string label = draggedNode.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(label))
            label = FormatUnnamedNode(draggedNode);

        preview.Text = $"→ {label}";
        preview.Color = new ColorF4(1.0f, 1.0f, 1.0f, 0.75f);
    }

    private static string FormatUnnamedNode(SceneNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Transform.Name))
            return node.Transform.Name;

        var comps = node.Components.Where(c => c is not null).ToList();
        if (comps.Count == 1)
            return comps[0].GetType().Name;

        if (comps.Count > 1)
            return $"({comps.Count} components)";

        return "Unnamed Node";
    }

    private void HandleNodeButtonInteraction(UIInteractableComponent obj)
    {
        if (obj.UserData is not SceneNode node)
            return;

        Selection.SceneNode = node;

        if (!FocusCameraOnDoubleClick)
            return;

        float now = Engine.Time.Timer.Time();
        bool doubleClicked = _lastClickedNode == node && now - _lastClickTime <= DoubleClickThreshold;

        if (doubleClicked)
        {
            if (TryFocusCameraOnNode(node))
            {
                _lastClickedNode = null;
                _lastClickTime = float.NegativeInfinity;
            }
            else
            {
                _lastClickedNode = node;
                _lastClickTime = now;
            }
        }
        else
        {
            _lastClickedNode = node;
            _lastClickTime = now;
        }
    }

    private bool TryFocusCameraOnNode(SceneNode? node)
    {
        if (node is null)
            return false;

        if (Engine.State.MainPlayer.ControlledPawn is not EditorFlyingCameraPawnComponent pawn)
            return false;

        pawn.FocusOnNode(node, DoubleClickFocusDuration);
        return true;
    }
}

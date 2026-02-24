using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor;

public partial class HierarchyPanel : EditorPanel, IUIScrollReceiver
{
    private List<SceneNode> _rootNodes = [];
    public List<SceneNode> RootNodes
    {
        get => _rootNodes;
        set => SetField(ref _rootNodes, value);
    }

    private const float Margin = 4.0f;
    private const int DepthIncrement = 10;
    private const int DefaultMaxNodesToRender = 2000;
    private static readonly Vector4 DropTargetHighlight = new(0.25f, 0.55f, 0.95f, 0.35f);
    private static readonly Vector4 RootDropHighlight = new(0.18f, 0.18f, 0.18f, 0.30f);

    private bool _truncateHierarchy = true;
    public bool TruncateHierarchy
    {
        get => _truncateHierarchy;
        set => SetField(ref _truncateHierarchy, value);
    }

    private int _maxNodesToRender = DefaultMaxNodesToRender;
    public int MaxNodesToRender
    {
        get => _maxNodesToRender;
        set => SetField(ref _maxNodesToRender, Math.Max(1, value));
    }

    private readonly Dictionary<UIInteractableComponent, Vector2> _pendingDragStarts = new();
    private SceneNode? _lastClickedNode;
    private float _lastClickTime = float.NegativeInfinity;
    private UIListTransform? _treeListTransform;
    private UIBoundableTransform? _scrollbarTrackTransform;
    private UIBoundableTransform? _scrollbarThumbTransform;
    private int _renderedNodeCount;
    private float _scrollOffset;

    private const float ScrollbarWidth = 8.0f;
    private const float ScrollbarPadding = 4.0f;
    private const float MinScrollbarThumbHeight = 24.0f;
    private const float ScrollStepPixels = 32.0f;

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
            case nameof(TruncateHierarchy):
            case nameof(MaxNodesToRender):
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
        using var sample = Engine.Profiler.Start("HierarchyPanel.RemakeChildren");
        SceneNode.Transform.Clear();
        _treeListTransform = null;
        _scrollbarTrackTransform = null;
        _scrollbarThumbTransform = null;
        _renderedNodeCount = 0;
        _scrollOffset = 0.0f;
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
        using var sample = Engine.Profiler.Start("HierarchyPanel.CreateTree");
        var listNode = parentNode.NewChild<UIMaterialComponent>(out var menuMat);
        menuMat.Material = MakeBackgroundMaterial();
        var listTfm = listNode.SetTransform<UIListTransform>();
        listTfm.DisplayHorizontal = false;
        listTfm.ItemSpacing = 0;
        listTfm.Padding = new Vector4(0.0f);
        listTfm.ItemAlignment = EListAlignment.TopOrLeft;
        listTfm.Virtual = true;
        listTfm.ItemSize = ItemHeight;
        _treeListTransform = listTfm;

        var scrollbarTrackNode = parentNode.NewChild<UIMaterialComponent>(out var scrollbarTrackMat);
        var scrollbarTrackTfm = scrollbarTrackNode.SetTransform<UIBoundableTransform>();
        scrollbarTrackTfm.MinAnchor = new Vector2(1.0f, 0.0f);
        scrollbarTrackTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        scrollbarTrackTfm.Width = ScrollbarWidth;
        scrollbarTrackTfm.Margins = new Vector4(0.0f, ScrollbarPadding, ScrollbarPadding, ScrollbarPadding);
        var trackMat = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.0f, 0.0f, 0.0f, 0.15f));
        trackMat.EnableTransparency();
        scrollbarTrackMat.Material = trackMat;
        scrollbarTrackMat.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        _scrollbarTrackTransform = scrollbarTrackTfm;

        var scrollbarThumbNode = scrollbarTrackNode.NewChild<UIMaterialComponent>(out var scrollbarThumbMat);
        var scrollbarThumbTfm = scrollbarThumbNode.SetTransform<UIBoundableTransform>();
        scrollbarThumbTfm.MinAnchor = new Vector2(0.0f, 1.0f);
        scrollbarThumbTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        scrollbarThumbTfm.Height = MinScrollbarThumbHeight;
        scrollbarThumbTfm.NormalizedPivot = new Vector2(0.0f, 1.0f);
        scrollbarThumbTfm.BlocksInputBehind = true;
        var thumbMat = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(1.0f, 1.0f, 1.0f, 0.35f));
        thumbMat.EnableTransparency();
        scrollbarThumbMat.Material = thumbMat;
        scrollbarThumbMat.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        _scrollbarThumbTransform = scrollbarThumbTfm;

        EditorDragDropUtility.RegisterDropTarget(
            listTfm,
            HandleRootDrop,
            payload => EditorDragDropUtility.IsSceneNodePayload(payload),
            hovering => UpdateBackgroundHighlight(menuMat, hovering ? RootDropHighlight : Vector4.Zero));

        int totalNodes = CountNodes(RootNodes);
        int maxToRender = TruncateHierarchy ? Math.Min(totalNodes, MaxNodesToRender) : totalNodes;
        int renderedCount = 0;

        CreateNodes(listNode, RootNodes, ref renderedCount, maxToRender);
        _renderedNodeCount = renderedCount;
        if (renderedCount < totalNodes)
            AddTruncationNotice(listNode, totalNodes - renderedCount);

        UpdateScrollMetrics();
    }

    public bool HandleMouseScroll(float diff)
    {
        if (_treeListTransform is null)
            return false;

        float maxOffset = GetMaxScrollOffset();
        if (maxOffset <= 0.0f)
            return false;

        _scrollOffset = Math.Clamp(_scrollOffset - (diff * ScrollStepPixels), 0.0f, maxOffset);
        UpdateScrollMetrics();
        return true;
    }

    private void UpdateScrollMetrics()
    {
        if (_treeListTransform is null)
            return;

        float viewportHeight = GetViewportHeight();
        float maxOffset = GetMaxScrollOffset(viewportHeight);
        _scrollOffset = Math.Clamp(_scrollOffset, 0.0f, maxOffset);

        // Shift content by scroll offset; virtual bounds cull to the viewport
        _treeListTransform.ContentScrollOffset = _scrollOffset;
        _treeListTransform.SetVirtualBounds(viewportHeight, 0.0f);

        UpdateScrollbarVisual(viewportHeight, maxOffset);
    }

    private float GetViewportHeight()
    {
        float panelHeight = BoundableTransform.ActualHeight;
        if (panelHeight <= 0.0f)
            panelHeight = BoundableTransform.Height ?? 300.0f;
        return MathF.Max(ItemHeight, panelHeight - (ScrollbarPadding * 2.0f));
    }

    private float GetContentHeight()
        => MathF.Max(0.0f, _renderedNodeCount * ItemHeight);

    private float GetMaxScrollOffset()
        => GetMaxScrollOffset(GetViewportHeight());

    private float GetMaxScrollOffset(float viewportHeight)
    {
        float contentHeight = GetContentHeight();
        return MathF.Max(0.0f, contentHeight - viewportHeight);
    }

    private void UpdateScrollbarVisual(float viewportHeight, float maxOffset)
    {
        if (_scrollbarTrackTransform is null || _scrollbarThumbTransform is null)
            return;

        float trackHeight = _scrollbarTrackTransform.ActualHeight;
        if (trackHeight <= 0.0f)
            trackHeight = viewportHeight;

        float contentHeight = GetContentHeight();
        bool canScroll = maxOffset > 0.0f && contentHeight > 0.0f;
        _scrollbarTrackTransform.Visibility = canScroll ? EVisibility.Visible : EVisibility.Hidden;
        _scrollbarThumbTransform.Visibility = canScroll ? EVisibility.Visible : EVisibility.Hidden;
        if (!canScroll)
            return;

        float thumbHeight = MathF.Max(MinScrollbarThumbHeight, (viewportHeight / contentHeight) * trackHeight);
        float travel = MathF.Max(0.0f, trackHeight - thumbHeight);
        float ratio = maxOffset <= 0.0f ? 0.0f : _scrollOffset / maxOffset;

        _scrollbarThumbTransform.Height = thumbHeight;
        _scrollbarThumbTransform.Translation = new Vector2(0.0f, -travel * ratio);
    }

    private void CreateNodes(SceneNode listNode, IEnumerable<SceneNode> nodes, ref int renderedCount, int maxToRender)
    {
        using var sample = Engine.Profiler.Start("HierarchyPanel.CreateNodes");
        var copy = nodes.Where(x => x is not null).ToList();
        foreach (SceneNode node in copy)
        {
            if (renderedCount >= maxToRender)
                return;

            using var nodeSample = Engine.Profiler.Start("HierarchyPanel.CreateNodes.Node");
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
            background.RenderPass = (int)EDefaultRenderPass.TransparentForward;

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

            renderedCount++;
            if (renderedCount >= maxToRender)
                return;

            CreateNodes(listNode, node.Transform.Children.Select(x => x.SceneNode!), ref renderedCount, maxToRender);
        }
    }

    private static int CountNodes(IEnumerable<SceneNode> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            if (node is null)
                continue;
            count++;
            if (node.Transform?.Children is null)
                continue;
            count += CountNodes(node.Transform.Children.Select(x => x.SceneNode!));
        }
        return count;
    }

    private void AddTruncationNotice(SceneNode listNode, int hiddenCount)
    {
        var noticeNode = listNode.NewChild<UIMaterialComponent>(out var background);
        background.Material = MakeBackgroundMaterial();
        var noticeTfm = noticeNode.SetTransform<UIBoundableTransform>();
        noticeTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        noticeTfm.Margins = new Vector4(0.0f);

        noticeNode.NewChild<UITextComponent>(out var text);
        var textTfm = text.BoundableTransform;
        textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
        text.FontSize = 12;
        text.Text = $"Hierarchy truncated ({hiddenCount} more nodes).";
        text.HorizontalAlignment = EHorizontalAlignment.Left;
        text.VerticalAlignment = EVerticalAlignment.Center;
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

        preview.Text = $"? {label}";
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

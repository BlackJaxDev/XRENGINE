using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Editor.Mcp;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
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
    private const float ArrowWidth = 16.0f;
    private const int DefaultMaxNodesToRender = 2000;
    private static readonly Vector4 DropTargetHighlight = new(0.25f, 0.55f, 0.95f, 0.35f);
    private static readonly Vector4 RootDropHighlight = new(0.18f, 0.18f, 0.18f, 0.30f);
    private static readonly ColorF4 SelectedNodeColor = new(0.22f, 0.50f, 0.85f, 0.45f);

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
    private readonly HashSet<Guid> _collapsedNodes = new();
    private UIContextMenuComponent? _contextMenu;
    private SceneNode? _nodePendingRename;
    private SceneNode? _contextMenuTargetNode;

    // Phase 7: Scene section state
    private readonly HashSet<Guid> _collapsedSections = new();
    private bool _showEditorScene;

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
        Selection.SelectionChanged += OnSelectionChanged;
        SubscribeRightClick();
        RemakeChildren();
    }
    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        Selection.SelectionChanged -= OnSelectionChanged;
        UnsubscribeRightClick();
        SceneNode.Transform.Clear();
    }

    private void SubscribeRightClick()
    {
        var input = GetCanvasInput();
        if (input is not null)
            input.RightClick += OnRightClickInteractable;
    }

    private void UnsubscribeRightClick()
    {
        var input = GetCanvasInput();
        if (input is not null)
            input.RightClick -= OnRightClickInteractable;
    }

    private void OnRightClickInteractable(UIInteractableComponent target)
    {
        // Only handle right-clicks on our node buttons
        if (target.UserData is not SceneNode node)
            return;

        _contextMenuTargetNode = node;

        // Ensure context menu component exists
        if (_contextMenu is null)
        {
            var menuNode = SceneNode.NewChild<UIContextMenuComponent>(out var menu);
            menuNode.Name = "HierarchyContextMenu";
            _contextMenu = menu;
        }

        var input = GetCanvasInput();
        var pos = input?.CursorPositionWorld2D ?? Vector2.Zero;

        _contextMenu.Show(pos,
            new ContextMenuItem("Rename", () => BeginRename(_contextMenuTargetNode)),
            new ContextMenuItem("Add Child", () => CreateChildSceneNode(_contextMenuTargetNode)),
            new ContextMenuItem("Focus Camera", () => TryFocusCameraOnNode(_contextMenuTargetNode)),
            ContextMenuItem.Separator(),
            new ContextMenuItem("Delete", () => DeleteNode(_contextMenuTargetNode)));
    }

    private void OnSelectionChanged(SceneNode[] _) => UpdateNodeHighlights();

    public void RemakeChildren()
    {
        using var sample = Engine.Profiler.Start("HierarchyPanel.RemakeChildren");
        float savedScroll = _scrollOffset;

        // Hide context menu before clearing children (it may be a child of this node).
        // Null the reference so it will be recreated on next right-click.
        if (_contextMenu is not null)
        {
            _contextMenu.Hide();
            _contextMenu = null;
        }

        SceneNode.Transform.Clear();
        _treeListTransform = null;
        _scrollbarTrackTransform = null;
        _scrollbarThumbTransform = null;
        _renderedNodeCount = 0;
        _scrollOffset = savedScroll;
        //Create the root menu transform - this is a horizontal list of buttons.
        CreateTree(SceneNode, this);

        // Force an immediate canvas layout pass so the new tree is arranged before the
        // next render. Without this, RemakeChildren called from ProcessQueuedSceneEdits
        // (which runs on UpdateFrame AFTER the canvas layout handler) would leave the
        // tree un-laid-out for one frame, causing all elements to render at the origin.
        var canvas = BoundableTransform?.ParentCanvas;
        if (canvas is not null && canvas.IsLayoutInvalidated)
            UILayoutSystem.UpdateCanvasLayout(canvas);
    }

    public float ItemHeight { get; set; } = 30.0f;

    public bool IsCollapsed(SceneNode node) => _collapsedNodes.Contains(node.ID);

    public void ToggleCollapse(SceneNode node)
    {
        if (!_collapsedNodes.Remove(node.ID))
            _collapsedNodes.Add(node.ID);
        RemakeChildren();
    }

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

        var worldInstance = McpWorldResolver.TryGetActiveWorldInstance();

        // --- Scrollable list ---
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

        // Register drop targets for both scene node and asset drops
        EditorDragDropUtility.RegisterDropTarget(
            listTfm,
            HandleRootDrop,
            payload => EditorDragDropUtility.IsSceneNodePayload(payload) || EditorDragDropUtility.IsAssetPathPayload(payload),
            hovering => UpdateBackgroundHighlight(menuMat, hovering ? RootDropHighlight : Vector4.Zero));

        // --- Phase 7A: World header info rows ---
        int renderedCount = 0;
        CreateWorldHeaderRows(listNode, worldInstance, ref renderedCount);

        // --- Flat node tree (using RootNodes, same approach as pre-Phase 7) ---
        int totalNodes = CountNodes(RootNodes);
        int maxToRender = TruncateHierarchy ? Math.Min(totalNodes, MaxNodesToRender) : totalNodes;

        CreateNodes(listNode, RootNodes, ref renderedCount, renderedCount + maxToRender);
        _renderedNodeCount = renderedCount;
        if (renderedCount - CountWorldHeaderRows() < totalNodes)
            AddTruncationNotice(listNode, totalNodes - (renderedCount - CountWorldHeaderRows()));

        UpdateScrollMetrics();
    }

    /// <summary>Number of fixed header rows added before actual tree nodes.</summary>
    private static int CountWorldHeaderRows() => 4;

    /// <summary>
    /// Adds world info as simple button-style rows at the top of the list.
    /// Uses the same child creation pattern as CreateNodes to guarantee layout compatibility.
    /// </summary>
    private void CreateWorldHeaderRows(SceneNode listNode, XRWorldInstance? worldInstance, ref int renderedCount)
    {
        var targetWorld = worldInstance?.TargetWorld;
        string worldName = targetWorld?.Name ?? "World";
        string displayPath = string.IsNullOrEmpty(targetWorld?.FilePath) ? "(unsaved)" : targetWorld!.FilePath!;
        string gameModeName = worldInstance?.GameMode?.GetType().Name ?? "<none>";

        AddHeaderRow(listNode, $"World: {worldName}", 14, EditorUI.Styles.SectionHeaderTextColor, EditorUI.Styles.WorldHeaderColor);
        renderedCount++;

        AddHeaderRow(listNode, displayPath, 11, EditorUI.Styles.DisabledTextColor, EditorUI.Styles.WorldHeaderColor);
        renderedCount++;

        AddHeaderRow(listNode, $"GameMode: {gameModeName}", 12, EditorUI.Styles.DisabledTextColor, EditorUI.Styles.WorldHeaderColor);
        renderedCount++;

        // "Show Editor Scene" toggle
        var toggleNode = listNode.NewChild<UIButtonComponent, UIMaterialComponent>(out var toggleBtn, out var toggleBg);
        var toggleMat = XRMaterial.CreateUnlitColorMaterialForward(EditorUI.Styles.WorldHeaderColor);
        toggleMat.EnableTransparency();
        toggleBg.Material = toggleMat;
        toggleBg.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        var toggleTfm = toggleNode.GetTransformAs<UIBoundableTransform>(true)!;
        toggleTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        toggleTfm.Margins = new Vector4(0.0f);

        var checkboxNode = toggleNode.NewChild<UIMaterialComponent>(out var checkboxBorderBg);
        var checkboxTfm = checkboxNode.SetTransform<UIBoundableTransform>();
        checkboxTfm.MinAnchor = new Vector2(0.0f, 0.5f);
        checkboxTfm.MaxAnchor = new Vector2(0.0f, 0.5f);
        checkboxTfm.Width = 14.0f;
        checkboxTfm.Height = 14.0f;
        checkboxTfm.Translation = new Vector2(8.0f, 0.0f);
        var checkboxBorderMat = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.85f, 0.85f, 0.85f, 1.0f));
        checkboxBorderMat.EnableTransparency();
        checkboxBorderBg.Material = checkboxBorderMat;
        checkboxBorderBg.RenderPass = (int)EDefaultRenderPass.TransparentForward;

        var checkboxFillNode = checkboxNode.NewChild<UIMaterialComponent>(out var checkboxFillBg);
        var checkboxFillTfm = checkboxFillNode.SetTransform<UIBoundableTransform>();
        checkboxFillTfm.MinAnchor = Vector2.Zero;
        checkboxFillTfm.MaxAnchor = Vector2.One;
        checkboxFillTfm.Margins = new Vector4(3.0f);
        var checkboxFillMat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.White);
        checkboxFillMat.EnableTransparency();
        checkboxFillBg.Material = checkboxFillMat;
        checkboxFillBg.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        checkboxFillTfm.Visibility = _showEditorScene ? EVisibility.Visible : EVisibility.Hidden;

        toggleNode.NewChild<UITextComponent>(out var toggleLabel);
        var toggleLabelTfm = toggleLabel.BoundableTransform;
        toggleLabelTfm.MinAnchor = Vector2.Zero;
        toggleLabelTfm.MaxAnchor = Vector2.One;
        toggleLabelTfm.Margins = new Vector4(28.0f, Margin, 8.0f, Margin);
        toggleLabel.FontSize = 12;
        toggleLabel.Text = "Show Editor Scene";
        toggleLabel.HorizontalAlignment = EHorizontalAlignment.Left;
        toggleLabel.VerticalAlignment = EVerticalAlignment.Center;
        toggleLabel.Color = ColorF4.White;
        EditorUI.Styles.UpdateButton(toggleBtn);
        toggleBtn.HighlightBackgroundColor = EditorUI.Styles.HoverRowColor;
        toggleBtn.HighlightTextColor = ColorF4.White;
        toggleBtn.InteractAction += _ =>
        {
            _showEditorScene = !_showEditorScene;
            checkboxFillTfm.Visibility = _showEditorScene ? EVisibility.Visible : EVisibility.Hidden;
        };
        renderedCount++;
    }

    /// <summary>
    /// Creates a simple non-interactive row in the list using the same UIButtonComponent pattern
    /// that CreateNodes uses, ensuring proper virtual list layout.
    /// </summary>
    private static void AddHeaderRow(SceneNode listNode, string text, float fontSize, ColorF4 textColor, ColorF4 bgColor)
    {
        var rowNode = listNode.NewChild<UIButtonComponent, UIMaterialComponent>(out var btn, out var bg);
        var rowMat = XRMaterial.CreateUnlitColorMaterialForward(bgColor);
        rowMat.EnableTransparency();
        bg.Material = rowMat;
        bg.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        var rowTfm = rowNode.GetTransformAs<UIBoundableTransform>(true)!;
        rowTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        rowTfm.Margins = new Vector4(0.0f);

        // Make button non-highlighting for info rows
        btn.DefaultBackgroundColor = ColorF4.Transparent;
        btn.HighlightBackgroundColor = ColorF4.Transparent;
        btn.DefaultTextColor = textColor;
        btn.HighlightTextColor = textColor;

        rowNode.NewChild<UITextComponent>(out var label);
        var labelTfm = label.BoundableTransform;
        labelTfm.MinAnchor = Vector2.Zero;
        labelTfm.MaxAnchor = Vector2.One;
        labelTfm.Margins = new Vector4(8.0f, Margin, 8.0f, Margin);
        label.FontSize = fontSize;
        label.Text = text;
        label.HorizontalAlignment = EHorizontalAlignment.Left;
        label.VerticalAlignment = EVerticalAlignment.Center;
        label.Color = textColor;
    }

    // --- Phase 7B: Scene section headers ---

    private void CreateSceneSectionHeader(SceneNode listNode, XRScene scene, XRWorldInstance worldInstance, bool collapsed)
    {
        string sceneName = string.IsNullOrWhiteSpace(scene.Name) ? "Untitled Scene" : scene.Name!;
        string dirtyMarker = scene.IsDirty ? " *" : string.Empty;

        var sectionNode = listNode.NewChild<UIMaterialComponent>(out var sectionBg);
        var sectionMat = XRMaterial.CreateUnlitColorMaterialForward(EditorUI.Styles.SectionHeaderColor);
        sectionMat.EnableTransparency();
        sectionBg.Material = sectionMat;
        sectionBg.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        var sectionTfm = sectionNode.SetTransform<UIBoundableTransform>();
        sectionTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        sectionTfm.Margins = new Vector4(0.0f);

        // Collapse arrow + scene name
        var headerBtnNode = sectionNode.NewChild<UIButtonComponent>(out var headerBtn);
        var headerBtnTfm = headerBtnNode.GetTransformAs<UIBoundableTransform>(true)!;
        headerBtnTfm.MinAnchor = new Vector2(0.0f, 0.0f);
        headerBtnTfm.MaxAnchor = new Vector2(0.75f, 1.0f);
        headerBtnTfm.Margins = new Vector4(0.0f);

        headerBtnNode.NewChild<UITextComponent>(out var headerText);
        var headerTextTfm = headerText.BoundableTransform;
        headerTextTfm.MinAnchor = Vector2.Zero;
        headerTextTfm.MaxAnchor = Vector2.One;
        headerTextTfm.Margins = new Vector4(6.0f, 2.0f, 6.0f, 2.0f);
        headerText.FontSize = EditorUI.Styles.SectionHeaderFontSize;
        string arrow = collapsed ? "\u25B6" : "\u25BC";
        headerText.Text = $"{arrow} {sceneName}{dirtyMarker}";
        headerText.HorizontalAlignment = EHorizontalAlignment.Left;
        headerText.VerticalAlignment = EVerticalAlignment.Center;
        headerText.Color = EditorUI.Styles.SectionHeaderTextColor;

        EditorUI.Styles.UpdateButton(headerBtn);
        var capturedScene = scene;
        headerBtn.InteractAction += _ =>
        {
            if (!_collapsedSections.Remove(capturedScene.ID))
                _collapsedSections.Add(capturedScene.ID);
            RemakeChildren();
        };

        if (!string.IsNullOrWhiteSpace(scene.FilePath))
        {
            // TODO: Tooltip with scene file path (when native tooltips are available)
        }

        // Controls area (right side): Visibility toggle + Unload button
        var controlsNode = sectionNode.NewChild<UIButtonComponent>(out _);
        var controlsTfm = controlsNode.GetTransformAs<UIBoundableTransform>(true)!;
        controlsTfm.MinAnchor = new Vector2(0.75f, 0.0f);
        controlsTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        controlsTfm.Margins = new Vector4(0.0f);

        // Visibility toggle
        var visNode = controlsNode.NewChild<UIButtonComponent>(out var visBtn);
        var visTfm = visNode.GetTransformAs<UIBoundableTransform>(true)!;
        visTfm.MinAnchor = new Vector2(0.0f, 0.0f);
        visTfm.MaxAnchor = new Vector2(0.5f, 1.0f);
        visTfm.Margins = new Vector4(2.0f);
        visTfm.BlocksInputBehind = true;

        visNode.NewChild<UITextComponent>(out var visGlyph);
        var visGlyphTfm = visGlyph.BoundableTransform;
        visGlyphTfm.MinAnchor = Vector2.Zero;
        visGlyphTfm.MaxAnchor = Vector2.One;
        visGlyphTfm.Margins = new Vector4(0.0f);
        visGlyph.FontSize = 11;
        visGlyph.Text = scene.IsVisible ? "\uD83D\uDC41" : "\u2014"; // eye icon or dash
        visGlyph.HorizontalAlignment = EHorizontalAlignment.Center;
        visGlyph.VerticalAlignment = EVerticalAlignment.Center;
        visGlyph.Color = scene.IsVisible ? ColorF4.White : EditorUI.Styles.DisabledTextColor;

        var capturedWorld = worldInstance;
        EditorUI.Styles.UpdateButton(visBtn);
        visBtn.InteractAction += _ =>
        {
            EnqueueSceneEdit(() =>
            {
                ToggleSceneVisibility(capturedScene, capturedWorld, !capturedScene.IsVisible);
                RemakeChildren();
            });
        };

        // Unload button
        var unloadNode = controlsNode.NewChild<UIButtonComponent>(out var unloadBtn);
        var unloadTfm = unloadNode.GetTransformAs<UIBoundableTransform>(true)!;
        unloadTfm.MinAnchor = new Vector2(0.5f, 0.0f);
        unloadTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        unloadTfm.Margins = new Vector4(2.0f);
        unloadTfm.BlocksInputBehind = true;

        unloadNode.NewChild<UITextComponent>(out var unloadGlyph);
        var unloadGlyphTfm = unloadGlyph.BoundableTransform;
        unloadGlyphTfm.MinAnchor = Vector2.Zero;
        unloadGlyphTfm.MaxAnchor = Vector2.One;
        unloadGlyphTfm.Margins = new Vector4(0.0f);
        unloadGlyph.FontSize = 10;
        unloadGlyph.Text = "\u2716"; // X icon
        unloadGlyph.HorizontalAlignment = EHorizontalAlignment.Center;
        unloadGlyph.VerticalAlignment = EVerticalAlignment.Center;

        EditorUI.Styles.UpdateButton(unloadBtn);
        unloadBtn.InteractAction += _ =>
        {
            EnqueueSceneEdit(() =>
            {
                UnloadSceneFromWorld(capturedScene, capturedWorld);
                RemakeChildren();
            });
        };
    }

    private void CreateSimpleSectionHeader(SceneNode listNode, string title, string id, bool collapsed = false, Action? toggleCollapse = null)
    {
        var sectionNode = listNode.NewChild<UIMaterialComponent>(out var sectionBg);
        var sectionMat = XRMaterial.CreateUnlitColorMaterialForward(EditorUI.Styles.SectionHeaderColor);
        sectionMat.EnableTransparency();
        sectionBg.Material = sectionMat;
        sectionBg.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        var sectionTfm = sectionNode.SetTransform<UIBoundableTransform>();
        sectionTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        sectionTfm.Margins = new Vector4(0.0f);

        if (toggleCollapse is not null)
        {
            var headerBtnNode = sectionNode.NewChild<UIButtonComponent>(out var headerBtn);
            var headerBtnTfm = headerBtnNode.GetTransformAs<UIBoundableTransform>(true)!;
            headerBtnTfm.MinAnchor = Vector2.Zero;
            headerBtnTfm.MaxAnchor = Vector2.One;
            headerBtnTfm.Margins = new Vector4(0.0f);

            headerBtnNode.NewChild<UITextComponent>(out var headerText);
            var headerTextTfm = headerText.BoundableTransform;
            headerTextTfm.MinAnchor = Vector2.Zero;
            headerTextTfm.MaxAnchor = Vector2.One;
            headerTextTfm.Margins = new Vector4(6.0f, 2.0f, 6.0f, 2.0f);
            headerText.FontSize = EditorUI.Styles.SectionHeaderFontSize;
            string arrow = collapsed ? "\u25B6" : "\u25BC";
            headerText.Text = $"{arrow} {title}";
            headerText.HorizontalAlignment = EHorizontalAlignment.Left;
            headerText.VerticalAlignment = EVerticalAlignment.Center;
            headerText.Color = EditorUI.Styles.SectionHeaderTextColor;

            EditorUI.Styles.UpdateButton(headerBtn);
            headerBtn.InteractAction += _ => toggleCollapse();
        }
        else
        {
            sectionNode.NewChild<UITextComponent>(out var headerText);
            var headerTextTfm = headerText.BoundableTransform;
            headerTextTfm.MinAnchor = Vector2.Zero;
            headerTextTfm.MaxAnchor = Vector2.One;
            headerTextTfm.Margins = new Vector4(6.0f, 2.0f, 6.0f, 2.0f);
            headerText.FontSize = EditorUI.Styles.SectionHeaderFontSize;
            headerText.Text = $"\u25BC {title}";
            headerText.HorizontalAlignment = EHorizontalAlignment.Left;
            headerText.VerticalAlignment = EVerticalAlignment.Center;
            headerText.Color = EditorUI.Styles.SectionHeaderTextColor;
        }
    }

    private static void CreateInfoRow(SceneNode listNode, string message, ColorF4 color)
    {
        var infoNode = listNode.NewChild<UIMaterialComponent>(out var bg);
        bg.Material = MakeBackgroundMaterial();
        var infoTfm = infoNode.SetTransform<UIBoundableTransform>();
        infoTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
        infoTfm.Margins = new Vector4(0.0f);

        infoNode.NewChild<UITextComponent>(out var text);
        var textTfm = text.BoundableTransform;
        textTfm.Margins = new Vector4(16.0f, Margin, 10.0f, Margin);
        text.FontSize = 12;
        text.Text = message;
        text.HorizontalAlignment = EHorizontalAlignment.Left;
        text.VerticalAlignment = EVerticalAlignment.Center;
        text.Color = color;
    }

    // --- Phase 7B: Scene management helpers ---

    private static List<SceneNode> CollectUnassignedRoots(XRWorldInstance world, IReadOnlyList<XRScene> scenes)
    {
        var assigned = new HashSet<SceneNode>();
        foreach (var scene in scenes)
        {
            foreach (var root in scene.RootNodes)
            {
                if (root is not null)
                    assigned.Add(root);
            }
        }

        // Also mark nodes from editor-only scenes as assigned
        var targetWorld = world.TargetWorld;
        if (targetWorld is not null)
        {
            foreach (var scene in targetWorld.Scenes.Where(s => s.IsEditorOnly))
            {
                foreach (var root in scene.RootNodes)
                {
                    if (root is not null)
                        assigned.Add(root);
                }
            }
        }

        var unassigned = new List<SceneNode>();
        foreach (var root in world.RootNodes)
        {
            if (root is null)
                continue;
            if (!assigned.Contains(root) && !world.IsInEditorScene(root))
                unassigned.Add(root);
        }

        return unassigned;
    }

    private static void ToggleSceneVisibility(XRScene scene, XRWorldInstance world, bool visible)
    {
        if (scene.IsVisible == visible)
            return;

        scene.IsVisible = visible;
        scene.MarkDirty();
    }

    private static void UnloadSceneFromWorld(XRScene scene, XRWorldInstance world)
    {
        var targetWorld = world.TargetWorld;
        if (targetWorld is null)
            return;

        scene.IsVisible = false;
        world.UnloadScene(scene);
        targetWorld.Scenes.Remove(scene);
        ClearSelectionForScene(scene, world);
        scene.MarkDirty();
    }

    private static void ClearSelectionForScene(XRScene scene, XRWorldInstance world)
    {
        if (Selection.SceneNodes.Length == 0)
            return;

        var filtered = Selection.SceneNodes
            .Where(node => FindSceneForNode(node, world) != scene)
            .ToArray();

        if (filtered.Length == Selection.SceneNodes.Length)
            return;

        Selection.SceneNodes = filtered;
    }

    private int CountTotalAvailableNodes(XRWorldInstance? worldInstance)
    {
        var targetWorld = worldInstance?.TargetWorld;
        if (targetWorld?.Scenes.Count > 0)
        {
            int total = 0;
            foreach (var scene in targetWorld.Scenes.Where(s => !s.IsEditorOnly))
            {
                if (!_collapsedSections.Contains(scene.ID))
                    total += CountNodes(scene.RootNodes.Where(r => r is not null));
            }
            if (worldInstance is not null)
                total += CollectUnassignedRoots(worldInstance, targetWorld.Scenes.Where(s => !s.IsEditorOnly).ToArray()).Count;
            return total;
        }
        if (worldInstance is not null)
            return CountNodes(worldInstance.RootNodes.Where(r => r is not null));
        return CountNodes(RootNodes);
    }

    public bool HandleMouseScroll(float diff)
    {
        if (_treeListTransform is null)
            return true; // still consume to prevent camera zoom

        float maxOffset = GetMaxScrollOffset();
        if (maxOffset > 0.0f)
        {
            _scrollOffset = Math.Clamp(_scrollOffset - (diff * ScrollStepPixels), 0.0f, maxOffset);
            UpdateScrollMetrics();
        }
        return true; // always consume scroll over hierarchy panel
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
        foreach (SceneNode node in nodes)
        {
            if (node is null)
                continue;
            if (renderedCount >= maxToRender)
                return;

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

            bool hasChildren = HasSceneNodeChildren(node);
            float depthOffset = (node.Transform?.Depth ?? 0) * DepthIncrement;

            // Expand/collapse arrow for branch nodes
            if (hasChildren)
            {
                bool collapsed = IsCollapsed(node);
                var arrowNode = buttonNode.NewChild<UIButtonComponent>(out var arrowBtn);
                var arrowTfm = arrowNode.GetTransformAs<UIBoundableTransform>(true)!;
                arrowTfm.MinAnchor = new Vector2(0.0f, 0.0f);
                arrowTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
                arrowTfm.Width = ArrowWidth;
                arrowTfm.Translation = new Vector2(depthOffset, 0.0f);
                arrowTfm.BlocksInputBehind = true;

                arrowNode.NewChild<UITextComponent>(out var arrowGlyph);
                var arrowGlyphTfm = arrowGlyph.BoundableTransform;
                arrowGlyphTfm.MinAnchor = Vector2.Zero;
                arrowGlyphTfm.MaxAnchor = Vector2.One;
                arrowGlyphTfm.Margins = new Vector4(0.0f);
                arrowGlyph.FontSize = 10;
                arrowGlyph.Text = collapsed ? "\u25B6" : "\u25BC";
                arrowGlyph.HorizontalAlignment = EHorizontalAlignment.Center;
                arrowGlyph.VerticalAlignment = EVerticalAlignment.Center;
                arrowGlyph.Color = EditorUI.Styles.ExpandArrowColor;

                var capturedNode = node;
                arrowBtn.InteractAction += _ => ToggleCollapse(capturedNode);
            }

            // --- Label or inline rename input ---
            bool isRenaming = _nodePendingRename == node;
            if (isRenaming)
            {
                // Highlight the row background to indicate rename mode
                background.Material?.SetVector4("MatColor", new Vector4(0.15f, 0.35f, 0.6f, 0.85f));

                // Inline rename: show text input instead of label
                var renameNode = buttonNode.NewChild<UITextInputComponent, UITextComponent>(out var renameInput, out var renameText);
                renameInput.SingleLineMode = true;
                renameInput.Text = node.Name ?? string.Empty;
                renameText.Text = node.Name ?? string.Empty;
                renameText.FontSize = 14;
                renameText.Color = ColorF4.White;
                renameText.HorizontalAlignment = EHorizontalAlignment.Left;
                renameText.VerticalAlignment = EVerticalAlignment.Center;

                var renameTfm = renameNode.GetTransformAs<UIBoundableTransform>(true)!;
                renameTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
                renameTfm.Translation = new Vector2(depthOffset + ArrowWidth, 0.0f);
                renameTfm.Width = null;
                renameTfm.MaxAnchor = new Vector2(0.0f, 1.0f);

                renameInput.Submitted += ApplyRename;
                renameInput.Cancelled += _ => CancelRename();

                // Auto-focus the text input so keyboard input works immediately
                var canvasInput = GetCanvasInput();
                if (canvasInput is not null)
                    canvasInput.FocusedComponent = renameInput;
            }
            else
            {
                buttonNode.NewChild<UITextComponent>(out var text);

                var textTfm = text.BoundableTransform;
                textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);

                text.FontSize = 14;
                text.Text = nodeName;
                text.Color = ColorF4.White;
                textTfm.Translation = new Vector2(depthOffset + ArrowWidth, 0.0f);
                textTfm.Width = null;
                textTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
                text.HorizontalAlignment = EHorizontalAlignment.Left;
                text.VerticalAlignment = EVerticalAlignment.Center;
            }

            // --- Phase 6: Active/enabled toggle ---
            var toggleNode = buttonNode.NewChild<UIButtonComponent>(out var toggleBtn);
            var toggleTfm = toggleNode.GetTransformAs<UIBoundableTransform>(true)!;
            toggleTfm.MinAnchor = new Vector2(1.0f, 0.0f);
            toggleTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
            toggleTfm.Width = 24.0f;
            toggleTfm.Margins = new Vector4(2.0f);
            toggleTfm.BlocksInputBehind = true;

            toggleNode.NewChild<UITextComponent>(out var toggleGlyph);
            var toggleGlyphTfm = toggleGlyph.BoundableTransform;
            toggleGlyphTfm.MinAnchor = Vector2.Zero;
            toggleGlyphTfm.MaxAnchor = Vector2.One;
            toggleGlyphTfm.Margins = new Vector4(0.0f);
            toggleGlyph.FontSize = 12;
            toggleGlyph.Text = node.IsActiveSelf ? "\u2713" : string.Empty;
            toggleGlyph.HorizontalAlignment = EHorizontalAlignment.Center;
            toggleGlyph.VerticalAlignment = EVerticalAlignment.Center;
            toggleGlyph.Color = node.IsActiveSelf ? ColorF4.White : new ColorF4(0.5f, 0.5f, 0.5f, 1.0f);

            var capturedToggleNode = node;
            var capturedToggleGlyph = toggleGlyph;
            toggleBtn.InteractAction += _ =>
            {
                EnqueueSceneEdit(() =>
                {
                    capturedToggleNode.IsActiveSelf = !capturedToggleNode.IsActiveSelf;
                    capturedToggleGlyph.Text = capturedToggleNode.IsActiveSelf ? "\u2713" : string.Empty;
                    capturedToggleGlyph.Color = capturedToggleNode.IsActiveSelf ? ColorF4.White : new ColorF4(0.5f, 0.5f, 0.5f, 1.0f);
                    MarkSceneHierarchyDirty(capturedToggleNode);
                });
            };

            EditorUI.Styles.UpdateButton(button);
            button.DefaultTextColor = ColorF4.White;
            button.HighlightBackgroundColor = EditorUI.Styles.HoverRowColor;
            button.HighlightTextColor = ColorF4.White;
            if (Selection.SceneNodes.Contains(node))
                button.DefaultBackgroundColor = SelectedNodeColor;
            RegisterNodeDragHandlers(button, node);
            EditorDragDropUtility.RegisterDropTarget(
                buttonTfm,
                payload => HandleNodeDrop(node, payload),
                payload => CanAcceptNodeDrop(node, payload),
                hovering =>
                {
                    UpdateBackgroundHighlight(background, hovering ? DropTargetHighlight : Vector4.Zero);
                });

            renderedCount++;
            if (renderedCount >= maxToRender)
                return;

            if (!IsCollapsed(node))
                CreateNodes(listNode, GetSceneNodeChildren(node), ref renderedCount, maxToRender);
        }
    }

    private static bool HasSceneNodeChildren(SceneNode node)
    {
        if (node.Transform?.Children is not { } children)
            return false;
        foreach (var child in children)
        {
            if (child.SceneNode is { } sn && sn != node)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Gets only the direct SceneNode children of a node, filtering out
    /// internal transform children that don't represent scene nodes.
    /// </summary>
    private static IEnumerable<SceneNode> GetSceneNodeChildren(SceneNode node)
    {
        if (node.Transform?.Children is not { } children)
            yield break;
        foreach (var child in children)
        {
            if (child.SceneNode is { } sn && sn != node)
                yield return sn;
        }
    }

    private int CountNodes(IEnumerable<SceneNode> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            if (node is null)
                continue;
            count++;
            if (IsCollapsed(node))
                continue;
            count += CountNodes(GetSceneNodeChildren(node));
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
        text.Color = EditorUI.Styles.DisabledTextColor;
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
        // Handle scene node re-parenting
        if (EditorDragDropUtility.TryGetSceneNode(payload, out var dragged) && dragged is not null)
        {
            EnqueueSceneEdit(() => dragged.Transform.Parent = null);
            return true;
        }

        // Handle asset path drops (Phase 8B)
        if (EditorDragDropUtility.TryGetAssetPath(payload, out var assetPath) && !string.IsNullOrWhiteSpace(assetPath))
        {
            // Asset loading/spawning is handled by the editor's asset pipeline.
            // Emit a debug message for now; full asset spawn logic requires integration
            // with the editor's prefab/model loading (TryLoadPrefabAsset, TryLoadModelAsset).
            Debug.Log(ELogCategory.General, $"Asset drop on hierarchy root: {assetPath}");
            return true;
        }

        return false;
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
        return "Node";
    }

    // --- Phase 4: Context menu actions ---

    private void DeleteNode(SceneNode? node)
    {
        if (node is null)
            return;

        if (node == _nodePendingRename)
            CancelRename();

        EnqueueSceneEdit(() =>
        {
            var parent = node.Transform.Parent;
            if (parent is not null)
                parent.RemoveChild(node.Transform, EParentAssignmentMode.Immediate);
            else
                RootNodes.Remove(node);

            node.IsActiveSelf = false;
            MarkSceneHierarchyDirty(node);
            RemakeChildren();
        });
    }

    private void CreateChildSceneNode(SceneNode? parent)
    {
        if (parent is null)
            return;

        EnqueueSceneEdit(() =>
        {
            var child = new SceneNode(parent);
            // Ensure the parent is expanded so the new child is visible
            _collapsedNodes.Remove(parent.ID);
            MarkSceneHierarchyDirty(child);
            // BeginRename calls RemakeChildren, so no need to call it here too
            BeginRename(child);
        });
    }

    // --- Phase 5: Inline rename ---

    private void BeginRename(SceneNode? node)
    {
        if (node is null)
            return;

        _nodePendingRename = node;
        RemakeChildren();
    }

    private void ApplyRename(UITextInputComponent input)
    {
        if (_nodePendingRename is null)
            return;

        string newName = input.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName))
            newName = SceneNode.DefaultName;

        var node = _nodePendingRename;
        _nodePendingRename = null;

        EnqueueSceneEdit(() =>
        {
            node.Name = newName;
            MarkSceneHierarchyDirty(node);
            RemakeChildren();
        });
    }

    private void CancelRename()
    {
        _nodePendingRename = null;
        RemakeChildren();
    }

    // --- Phase 8A: Dirty tracking helpers ---

    private static void MarkSceneHierarchyDirty(SceneNode? node)
    {
        if (node is null)
            return;

        var world = McpWorldResolver.TryGetActiveWorldInstance();
        var scene = FindSceneForNode(node, world);
        if (scene is not null)
            scene.MarkDirty();
        else
            world?.TargetWorld?.MarkDirty();
    }

    private static XRScene? FindSceneForNode(SceneNode? node, XRWorldInstance? world)
    {
        if (node is null)
            return null;

        var targetWorld = world?.TargetWorld;
        if (targetWorld is null)
            return null;

        SceneNode? root = GetHierarchyRoot(node);
        if (root is null)
            return null;

        foreach (var scene in targetWorld.Scenes)
        {
            if (scene.RootNodes.Contains(root))
                return scene;
        }

        return null;
    }

    private static SceneNode? GetHierarchyRoot(SceneNode node)
    {
        TransformBase? transform = node.Transform;
        while (transform?.Parent is TransformBase parent)
            transform = parent;
        return transform?.SceneNode;
    }

    private UICanvasInputComponent? GetCanvasInput()
        => BoundableTransform.GetCanvasComponent()?.GetInputComponent();

    private void HandleNodeButtonInteraction(UIInteractableComponent obj)
    {
        _contextMenu?.Hide();

        if (obj.UserData is not SceneNode node)
            return;

        var input = GetCanvasInput();
        bool ctrl = input?.IsCtrlHeld ?? false;
        bool shift = input?.IsShiftHeld ?? false;
        bool alt = input?.IsAltHeld ?? false;

        if (ctrl)
        {
            // Ctrl+Click: toggle node in selection
            var current = Selection.SceneNodes;
            if (current.Contains(node))
                Selection.SceneNodes = current.Where(n => n != node).ToArray();
            else
                Selection.SceneNodes = [.. current, node];
        }
        else if (shift)
        {
            // Shift+Click: add to selection
            var current = Selection.SceneNodes;
            if (!current.Contains(node))
                Selection.SceneNodes = [.. current, node];
        }
        else if (alt)
        {
            // Alt+Click: remove from selection
            var current = Selection.SceneNodes;
            Selection.SceneNodes = current.Where(n => n != node).ToArray();
        }
        else
        {
            // Plain click: single select
            Selection.SceneNodes = [node];
        }

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

    private void UpdateNodeHighlights()
    {
        if (_treeListTransform is null)
            return;

        var selected = Selection.SceneNodes;
        foreach (var child in _treeListTransform.SceneNode!.Transform.Children)
        {
            var btn = child.SceneNode?.GetComponent<UIButtonComponent>();
            if (btn is null)
                continue;

            if (btn.UserData is SceneNode node && selected.Contains(node))
                btn.DefaultBackgroundColor = SelectedNodeColor;
            else
                btn.DefaultBackgroundColor = EditorUI.Styles.ButtonBackgroundColor;
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

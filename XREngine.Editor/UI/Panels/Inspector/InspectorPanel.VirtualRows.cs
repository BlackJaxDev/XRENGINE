using System.ComponentModel;
using System.Numerics;
using System.Reflection;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Extensions;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public partial class InspectorPanel
{
    private sealed class PropertyRow
    {
        public required SceneNode ValueRoot;
        public required InspectorPropertyColumnLayout Columns;
        public required UIButtonComponent DetailButton;
        public required UIBoundableTransform DetailButtonTransform;
        public required UITextComponent DetailLabel;
        /// <summary>The chevron at the left of a section header row. Member rows hide it.</summary>
        public required UIMaterialComponent DisclosureIcon;
        public required UIBoundableTransform DisclosureTransform;
        /// <summary>Last disclosure icon drawn: -1 hidden, 0 collapsed, 1 expanded.</summary>
        public int DisclosureState = -1;
        /// <summary>
        /// The active checkbox of a component section header. It sits after the chevron and before the title,
        /// like the ImGui component header, so a long title cannot cover it.
        /// </summary>
        public required SceneNode HeaderToggle;
        public required UIBoundableTransform HeaderToggleTransform;
        public bool HeaderToggleShown;
        /// <summary>The object field of a component, scene node, transform, or asset reference row.</summary>
        public InspectorObjectField? ObjectField;
        /// <summary>The node and method pickers of a persistent event callback row.</summary>
        public InspectorEventCallbackEditor? CallbackEditor;
        /// <summary>The icon at the right of the link row: a chevron, or a plus sign on an event row.</summary>
        public required UIMaterialComponent LinkIcon;
        public bool LinkIconIsPlus;
        public readonly Dictionary<Type, SceneNode> PrimitiveEditors = [];
        public PropertyInfo? Property;
        public IDisposable? DropTarget;
        public object BindingToken = new();
        public InspectorPropertyDropBinding? DropBinding;
        public InspectorStringPreviewBinding? StringPreview;
    }

    private sealed class PropertyProvider : IUIVirtualTableSource<PropertyInfo>, IVirtualListItemIndex
    {
        private readonly List<PropertyInfo> _properties = [];
        private readonly Dictionary<PropertyInfo, ulong> _ids = [];
        private readonly Dictionary<ulong, int> _indices = [];
        private object?[] _targetIdentities = [];
        private readonly Dictionary<PropertyInfo, object?[]> _rowTargets = [];
        private readonly Dictionary<PropertyInfo, string> _rowNames = [];
        private readonly HashSet<PropertyInfo> _sectionHeaders = [];
        private readonly Dictionary<PropertyInfo, string> _sectionKeys = [];
        private ulong _nextId;
        public object?[]? Targets { get; private set; }

        /// <summary>
        /// Gets the objects that one row edits. A stacked scene node view edits the node, its transform, and its
        /// components in one list, so a row can edit another object than <see cref="Targets"/>.
        /// </summary>
        public object?[]? TargetsFor(PropertyInfo property)
            => _rowTargets.TryGetValue(property, out object?[]? targets) ? targets : Targets;

        /// <summary>Gets the display name that replaces the property name, such as a component name on its header row.</summary>
        public string? NameFor(PropertyInfo property)
            => _rowNames.TryGetValue(property, out string? name) ? name : null;

        /// <summary>Reports whether a row starts a section, such as the transform or one component.</summary>
        public bool IsSectionHeader(PropertyInfo property) => _sectionHeaders.Contains(property);

        /// <summary>Gets the key of the section that a header row starts, or null for a member row.</summary>
        public string? SectionKeyFor(PropertyInfo property)
            => _sectionKeys.TryGetValue(property, out string? key) ? key : null;
        public int Count => _properties.Count;
        public ulong Revision { get; private set; } = 1;
        public ulong GetItemId(int index) => _ids[_properties[index]];
        public PropertyInfo GetItem(int index) => _properties[index];
        public bool TryGetIndexOfItemId(ulong id, out int index) => _indices.TryGetValue(id, out index);

        public void Reset(List<PropertyInfo> properties, object?[]? targets, InspectorRowLayout? layout = null)
        {
            _rowTargets.Clear();
            _rowNames.Clear();
            _sectionHeaders.Clear();
            _sectionKeys.Clear();
            if (layout is not null)
            {
                foreach (var entry in layout.SectionKeys)
                    _sectionKeys.Add(entry.Key, entry.Value);
                foreach (var entry in layout.Targets)
                    _rowTargets.Add(entry.Key, entry.Value);
                foreach (var entry in layout.Names)
                    _rowNames.Add(entry.Key, entry.Value);
                _sectionHeaders.UnionWith(layout.Headers);
            }
            // A different target must get different row leases even when its type exposes
            // the same PropertyInfo objects. The virtual list cancels focused/captured
            // interactions when those leases are recycled.
            bool targetChanged = targets is null
                ? _targetIdentities.Length != 0
                : targets.Length != _targetIdentities.Length ||
                  targets.Where((target, index) => !ReferenceEquals(target, _targetIdentities[index])).Any();
            if (targetChanged)
            {
                _targetIdentities = targets?.ToArray() ?? [];
                _ids.Clear();
            }
            _properties.Clear();
            _properties.AddRange(properties);
            Targets = targets;
            HashSet<PropertyInfo> current = [.. properties];
            foreach (PropertyInfo stale in _ids.Keys.Where(property => !current.Contains(property)).ToArray())
                _ids.Remove(stale);
            foreach (PropertyInfo property in properties)
                if (!_ids.ContainsKey(property))
                    _ids.Add(property, ++_nextId);
            _indices.Clear();
            for (int index = 0; index < _properties.Count; index++)
                _indices[_ids[_properties[index]]] = index;
            Revision++;
        }
    }

    private PropertyProvider? _virtualPropertyProvider;
    private UIVirtualPropertyGridProvider<PropertyInfo>? _virtualPropertyGrid;
    private UIListTransform? _virtualPropertyList;
    private UIScrollableTransform? _propertyScroll;
    private readonly Dictionary<UIBoundableTransform, PropertyRow> _propertyRows = [];
    private readonly Dictionary<PropertyInfo, InspectorAssetDropRequest> _pendingAssetDrops = [];
    private UIBoundableTransform? _detailTransform;
    private SceneNode? _detailEditor;
    private PropertyInfo? _detailProperty;
    private object?[]? _detailTargets;
    private object?[]? _detailCollectionValues;
    private ulong _detailCleanupRevision;
    private bool _targetRefreshPending;
    private bool _targetRefreshTickRegistered;
    private int _targetRefreshWaitFrames;
    private bool _staleDetailSuspended;
    private bool _detailHideWaitsForFocus;
    private readonly List<InspectorDetailConstructionAttempt> _activeDetailConstructions = [];
    private readonly List<InspectorDetailConstructionAttempt> _retainedDetailConstructions = [];
    private object?[]? _savedViewTargets;
    private SceneNode[]? _savedSceneSelection;
    private object? _savedAssetSelection;
    private InspectorView _savedInspectorView;
    private PropertyInfo? _savedDetailProperty;
    private Vector2 _savedPropertyScroll;
    private Vector2 _savedDetailScroll;
    private Vector2 _savedComponentScroll;
    private Vector2 _savedPickerScroll;
    private PropertyInfo? _savedDetailFocusBinding;
    private IUICanvasInputSource? _savedDetailFocusInput;
    private UIInteractableComponent? _savedDetailFocusedControl;
    private ulong _savedDetailFocusIntentRevision;
    private ulong _savedDetailReleasedIntentRevision;
    private object?[]? _pendingFocusTargets;
    private IUICanvasInputSource? _pendingFocusInput;
    private SceneNode? _pendingFocusEditor;
    private ulong _pendingFocusIntentRevision;
    private ulong _pendingFocusTargetIntentRevision;
    private ulong _focusRestoreRevision;
    private readonly Stack<SceneNode> _pendingFocusNodes = new();
    private int _pendingFocusFrames;

    private void CaptureViewState()
    {
        SceneNode? focusEditor = _detailEditor;
        IUICanvasInputSource? focusInput = _detailTransform?.GetCanvasComponent()?.GetInputComponent();
        UIInteractableComponent? focused = focusInput?.FocusedComponent;
        ulong focusIntent = focusInput?.FocusIntentRevision ?? 0;
        ulong focusTargetIntent = _inspectedTargetIntentRevision;
        _savedViewTargets = InspectedObjects?.ToArray();
        _savedSceneSelection = (SceneNode[])Selection.SceneNodes.Clone();
        _savedAssetSelection = NativeAssetSelection.SelectedTarget;
        _savedInspectorView = _inspectorView;
        _savedDetailProperty = _detailProperty;
        _savedPropertyScroll = _propertyScroll?.ScrollOffset ?? Vector2.Zero;
        _savedDetailScroll = (_detailTransform as UIScrollableTransform)?.ScrollOffset ?? Vector2.Zero;
        _savedComponentScroll = _componentListView?.ScrollOffset ?? Vector2.Zero;
        _savedPickerScroll = _componentPickerView?.ScrollOffset ?? Vector2.Zero;
        _savedDetailFocusBinding = focusEditor is { } detail &&
            ReferenceEquals(_detailEditor, detail) &&
            _inspectedTargetIntentRevision == focusTargetIntent &&
            ReferenceEquals(_detailTransform?.GetCanvasComponent()?.GetInputComponent(), focusInput) &&
            focusInput is { HasPendingFocusRequest: false } && focusIntent != 0 &&
            focusInput.FocusIntentRevision == focusIntent &&
            ReferenceEquals(focusInput.FocusedComponent, focused) && focused is not null &&
            focused.SceneNode is { } focusedNode && IsDetailDescendant(focusedNode, detail) &&
            focused is UITextInputComponent { Binding: EditorTextPropertyBinding binding }
                ? binding.Property : null;
        _savedDetailFocusInput = _savedDetailFocusBinding is null ? null : focusInput;
        _savedDetailFocusedControl = _savedDetailFocusBinding is null ? null : focused;
        _savedDetailFocusIntentRevision = _savedDetailFocusBinding is null ? 0 : focusIntent;
        _savedDetailReleasedIntentRevision = 0;
    }

    private void RestoreViewState()
    {
        object?[]? saved = _savedViewTargets;
        _savedViewTargets = null;
        if (saved is null || InspectedObjects is not { } current || saved.Length != current.Length ||
            saved.Where((target, index) => !ReferenceEquals(target, current[index])).Any())
            return;
        ulong targetIntent = _inspectedTargetIntentRevision;
        UIScrollableTransform? propertyScroll = _propertyScroll;
        bool Current() => IsActiveInHierarchy && IsCurrentTargetNotification(targetIntent, current) &&
            ReferenceEquals(_propertyScroll, propertyScroll) && propertyScroll is not null;
        if (!Current())
            return;
        _inspectorView = _savedInspectorView;
        propertyScroll!.ScrollOffset = _savedPropertyScroll;
        if (!Current()) return;
        if (_componentListView is not null)
            _componentListView.ScrollOffset = _savedComponentScroll;
        if (!Current()) return;
        if (_componentPickerView is not null)
            _componentPickerView.ScrollOffset = _savedPickerScroll;
        if (!Current()) return;
        if (_savedDetailProperty is { } detail && Properties?.Contains(detail) == true)
        {
            ShowDetailEditor(detail);
            if (!Current()) return;
            if (_detailTransform is UIScrollableTransform scroll)
                scroll.ScrollOffset = _savedDetailScroll;
            if (!Current()) return;
            if (_savedDetailFocusBinding is not null)
            {
                if (_pendingFocusFrames > 0)
                    StopDetailFocusRestore(clearSavedBinding: false);
                IUICanvasInputSource? input = _detailTransform?.GetCanvasComponent()?.GetInputComponent();
                ulong releasedIntent = _savedDetailReleasedIntentRevision;
                if (Current() && ReferenceEquals(input, _savedDetailFocusInput) &&
                    releasedIntent != 0 && input is { HasPendingFocusRequest: false } &&
                    input.FocusIntentRevision == releasedIntent && input.FocusedComponent is null)
                {
                    _pendingFocusTargets = current.ToArray();
                    _pendingFocusInput = input;
                    _pendingFocusEditor = _detailEditor;
                    _pendingFocusIntentRevision = releasedIntent;
                    _pendingFocusTargetIntentRevision = targetIntent;
                    _focusRestoreRevision++;
                    _pendingFocusFrames = 30;
                    RegisterTick(ETickGroup.Late, ETickOrder.Scene, RestoreDetailFocusTick);
                }
            }
        }
        if (Current())
            ApplyInspectorView();
    }

    private void RestoreDetailFocusTick()
    {
        if (--_pendingFocusFrames <= 0 || _detailEditor is null || _savedDetailFocusBinding is null ||
            !Equals(_detailProperty, _savedDetailProperty) ||
            _pendingFocusTargets is not { } focusTargets ||
            InspectedObjects is not { } current || focusTargets.Length != current.Length)
        {
            StopDetailFocusRestore();
            return;
        }
        for (int index = 0; index < focusTargets.Length; index++)
            if (!ReferenceEquals(focusTargets[index], current[index]))
            {
                StopDetailFocusRestore();
                return;
            }
        IUICanvasInputSource? input = _detailTransform?.GetCanvasComponent()?.GetInputComponent();
        SceneNode detailEditor = _detailEditor;
        ulong intent = _pendingFocusIntentRevision;
        ulong restoreRevision = _focusRestoreRevision;
        bool OwnsFocusRestore() => intent != 0 && _focusRestoreRevision == restoreRevision &&
            ReferenceEquals(input, _pendingFocusInput) &&
            ReferenceEquals(detailEditor, _pendingFocusEditor) && ReferenceEquals(_detailEditor, detailEditor) &&
            ReferenceEquals(_detailTransform?.GetCanvasComponent()?.GetInputComponent(), input) &&
            input is not null &&
            _inspectedTargetIntentRevision == _pendingFocusTargetIntentRevision &&
            SameTargets(_pendingFocusTargets, InspectedObjects) &&
            Equals(_detailProperty, _savedDetailProperty) && IsActiveInHierarchy &&
            !_destroyingOwner && !IsDestroyed && !IsDestroyQueued;
        bool CanStartFocus() => OwnsFocusRestore() && input!.FocusIntentRevision == intent &&
            !input.HasPendingFocusRequest;
        if (!CanStartFocus())
        {
            StopDetailFocusRestore();
            return;
        }
        UITextInputComponent? match = null;
        _pendingFocusNodes.Push(detailEditor);
        try
        {
            while (_pendingFocusNodes.TryPop(out SceneNode? node))
            {
                foreach (var component in node.Components)
                    if (component is UITextInputComponent { Binding: EditorTextPropertyBinding binding } text &&
                        Equals(binding.Property, _savedDetailFocusBinding))
                    {
                        if (match is not null)
                        {
                            StopDetailFocusRestore();
                            return;
                        }
                        match = text;
                    }
                foreach (var child in node.Transform.Children)
                    if (child.SceneNode is { } childNode)
                        _pendingFocusNodes.Push(childNode);
            }
        }
        finally { _pendingFocusNodes.Clear(); }
        if (!CanStartFocus() || input!.FocusedComponent is { IsActiveInHierarchy: true } focused &&
            !ReferenceEquals(focused, match))
        {
            StopDetailFocusRestore();
            return;
        }
        if (match is not null && input.TryFocusPresented(match, intent,
                () => OwnsFocusRestore() &&
                    match.Binding is EditorTextPropertyBinding binding &&
                    Equals(binding.Property, _savedDetailFocusBinding) &&
                    match.SceneNode is { } matchNode && IsDetailDescendant(matchNode, detailEditor) &&
                    (input.FocusedComponent is null || ReferenceEquals(input.FocusedComponent, match))))
            StopDetailFocusRestore();
    }

    private void StopDetailFocusRestore(bool clearSavedBinding = true)
    {
        _focusRestoreRevision++;
        _pendingFocusFrames = 0;
        _pendingFocusTargets = null;
        _pendingFocusInput = null;
        _pendingFocusEditor = null;
        _pendingFocusIntentRevision = 0;
        _pendingFocusTargetIntentRevision = 0;
        _pendingFocusNodes.Clear();
        if (clearSavedBinding)
        {
            _savedDetailFocusBinding = null;
            _savedDetailFocusInput = null;
            _savedDetailFocusedControl = null;
            _savedDetailFocusIntentRevision = 0;
            _savedDetailReleasedIntentRevision = 0;
        }
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RestoreDetailFocusTick);
    }

    private bool DidGlobalSelectionChangeWhileHidden()
    {
        if (_savedSceneSelection is null ||
            !ReferenceEquals(_savedAssetSelection, NativeAssetSelection.SelectedTarget))
            return true;
        SceneNode[] current = Selection.SceneNodes;
        return current.Length != _savedSceneSelection.Length ||
            current.Where((node, index) => !ReferenceEquals(node, _savedSceneSelection[index])).Any();
    }

    private void CreateVirtualPropertyList()
    {
        StopPropertyColumnLayout();
        SceneNode root = SceneNode.NewChildWithTransform(out UIDualSplitTransform split, "InspectorContent");
        split.VerticalSplit = true;
        split.FirstFixedSize = true;
        split.FixedSize = InspectorHeaderHeight;
        split.SplitterSize = 0;
        // The header holds the member search field above the command buttons.
        SceneNode header = root.NewChildWithTransform<UIBoundableTransform>(out _, "InspectorHeader");
        CreateMemberSearchField(header);
        SceneNode toolbar = header.NewChildWithTransform(out UIListTransform commands, "InspectorCommands");
        commands.MinAnchor = Vector2.Zero;
        commands.MaxAnchor = new Vector2(1.0f, 0.0f);
        commands.NormalizedPivot = Vector2.Zero;
        commands.Height = 30;
        commands.DisplayHorizontal = true;
        commands.ItemAlignment = EListAlignment.TopOrLeft;
        commands.ItemSpacing = 2;
        CreateComponentCommands(toolbar);
        SceneNode propertyPane = root.NewChildWithTransform<UIBoundableTransform>(out _, "PropertyPane");
        // The virtual list keeps a few rows above and below the visible area. A clear clipping quad keeps them
        // from drawing over the search field and the command buttons above the pane.
        UIMaterialComponent paneClip = propertyPane.AddComponent<UIMaterialComponent>()!;
        paneClip.SetBatchSolidColor(ColorF4.Transparent);
        paneClip.ClipToBounds = true;
        SceneNode scrollNode = propertyPane.NewChild();
        UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
        scroll.MinAnchor = Vector2.Zero;
        scroll.MaxAnchor = Vector2.One;
        scroll.NormalizedPivot = Vector2.Zero;
        scroll.BlocksInputBehind = true;
        scrollNode.AddComponent<UIScrollRegionComponent>();
        _propertyScroll = scroll;

        SceneNode listNode = scrollNode.NewChild();
        UIListTransform list = listNode.SetTransform<UIListTransform>();
        list.MinAnchor = Vector2.Zero;
        list.MaxAnchor = Vector2.One;
        list.NormalizedPivot = Vector2.Zero;
        list.DisplayHorizontal = false;
        list.ItemSize = 30;
        list.ItemAlignment = EListAlignment.TopOrLeft;
        list.VirtualOverscan = 2;
        _virtualPropertyProvider = new PropertyProvider();
        _virtualPropertyGrid = new UIVirtualPropertyGridProvider<PropertyInfo>(
            _virtualPropertyProvider,
            property => _virtualPropertyProvider?.NameFor(property) ?? ResolveName(property) ?? property.Name,
            160, 400,
            CreatePropertyValueCell,
            (cell, property, _) => BindPropertyRow(cell, property, _virtualPropertyProvider.TargetsFor(property)),
            UnbindPropertyRow,
            nameLeadingInset: NameGutter);
        _virtualPropertyGrid.Activated += ToggleStackedSection;
        list.VirtualProvider = _virtualPropertyGrid;
        _virtualPropertyList = list;
        StartPropertyColumnLayout();

        SceneNode detailNode = propertyPane.NewChild();
        UIScrollableTransform detail = detailNode.SetTransform<UIScrollableTransform>();
        detail.MinAnchor = Vector2.Zero;
        detail.MaxAnchor = Vector2.One;
        detail.Visibility = EVisibility.Collapsed;
        detailNode.AddComponent<UIScrollRegionComponent>();
        SceneNode backNode = detailNode.NewChild<UIButtonComponent>(out var backButton);
        UIBoundableTransform backTransform = backNode.GetTransformAs<UIBoundableTransform>(true)!;
        backTransform.MinAnchor = new Vector2(0, 1);
        backTransform.MaxAnchor = new Vector2(1, 1);
        backTransform.NormalizedPivot = new Vector2(0, 1);
        backTransform.Height = 30;
        backNode.NewChild<UITextComponent>(out var backLabel);
        backLabel.Text = "Back to properties";
        backLabel.FontSize = EditorUI.Styles.PropertyInputFontSize;
        backLabel.ClipToBounds = true;
        backLabel.VerticalAlignment = EVerticalAlignment.Center;
        backButton.InteractAction += _ => HideDetailEditor();
        _detailTransform = detail;
        CreateComponentViews(propertyPane);
        RefreshVirtualProperties();
    }

    private const float InspectorHeaderHeight = 64.0f;
    private const float SearchFieldHeight = 28.0f;
    private string _memberSearch = string.Empty;
    private UITextComponent? _memberSearchPlaceholder;

    /// <summary>
    /// Creates the member search field at the top of the inspector, like the ImGui "Search properties" box.
    /// The list filters while the user types. The placeholder text shows while the field is empty.
    /// </summary>
    private void CreateMemberSearchField(SceneNode header)
    {
        SceneNode searchNode = header.NewChild("MemberSearch");
        UIBoundableTransform searchBounds = searchNode.SetTransform<UIBoundableTransform>();
        searchBounds.MinAnchor = new Vector2(0.0f, 1.0f);
        searchBounds.MaxAnchor = new Vector2(1.0f, 1.0f);
        searchBounds.NormalizedPivot = new Vector2(0.0f, 1.0f);
        searchBounds.Height = SearchFieldHeight;
        searchBounds.Margins = new Vector4(4.0f, 2.0f, 4.0f, 0.0f);
        var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(searchNode,
            textMargins: new Vector4(26.0f, 2.0f, 8.0f, 2.0f));
        input.SingleLineMode = true;
        input.Text = _memberSearch;
        NativeUIElements.CreateIcon(searchNode, EUIQuadIcon.Search, new Vector2(0.0f, 0.5f),
            new Vector2(14.0f, 0.0f), 12.0f, EditorGlassTheme.TextSecondary);
        SceneNode placeholderNode = searchNode.NewChild<UITextComponent>(out var placeholder);
        placeholderNode.GetTransformAs<UIBoundableTransform>(true)!.Margins = new Vector4(26.0f, 0.0f, 8.0f, 0.0f);
        placeholder.Text = "Search properties";
        placeholder.FontSize = EditorUI.Styles.PropertyInputFontSize;
        placeholder.Color = EditorGlassTheme.TextDisabled;
        placeholder.VerticalAlignment = EVerticalAlignment.Center;
        placeholder.ClipToBounds = true;
        _memberSearchPlaceholder = placeholder;
        input.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(UITextInputComponent.Text))
                return;
            SetMemberSearch(input.Text);
        };
        input.Cancelled += field => field.Text = string.Empty;
    }

    /// <summary>Applies new member search text: it filters the rows and shows or hides the placeholder.</summary>
    private void SetMemberSearch(string? text)
    {
        string search = text?.Trim() ?? string.Empty;
        if (_memberSearchPlaceholder is { } placeholder)
        {
            EVisibility visibility = string.IsNullOrEmpty(text) ? EVisibility.Visible : EVisibility.Hidden;
            if (placeholder.BoundableTransform.Visibility != visibility)
                placeholder.BoundableTransform.Visibility = visibility;
        }
        if (string.Equals(search, _memberSearch, StringComparison.Ordinal))
            return;
        _memberSearch = search;
        if (_virtualPropertyProvider is not null && _virtualPropertyList is not null)
            RefreshVirtualProperties();
    }

    private void RefreshVirtualProperties()
    {
        InspectorRowLayout? layout = BuildStackedNodeLayout(InspectedObjects, _collapsedStackSections, _memberSearch);
        if (layout is null && InspectedObjects is [{ } single])
        {
            // One object outside the stacked view, such as a component opened from a link row, gets a layout
            // too, so its event members can list their callback rows.
            layout = new InspectorRowLayout();
            List<PropertyInfo> members = GetMatchingProperties(InspectedObjects);
            AddEventFields(members, single.GetType(), null, null);
            foreach (PropertyInfo property in members)
                if (string.IsNullOrWhiteSpace(_memberSearch) || MatchesMemberSearch(property, _memberSearch))
                    layout.Add(property, single);
        }
        if (layout is not null)
            AddEventCallbackRows(layout);
        List<PropertyInfo> properties = layout?.Properties ?? GetMatchingProperties(InspectedObjects);
        if (layout is null && !string.IsNullOrWhiteSpace(_memberSearch))
            properties.RemoveAll(property => !MatchesMemberSearch(property, _memberSearch));
        Properties = properties;
        _virtualPropertyProvider!.Reset(properties, InspectedObjects, layout);
        _virtualPropertyList!.InvalidateMeasure();
        RefreshComponentViews();
        if (_staleDetailSuspended && _detailEditor is not null &&
            !SameTargets(_detailTargets, InspectedObjects))
        {
            ApplyInspectorView();
            return;
        }
        if (_detailProperty is not null && !properties.Contains(_detailProperty))
        {
            if (!HideDetailEditor()) return;
        }
        else if (_detailProperty is not null)
            ShowDetailEditor(_detailProperty);
        ApplyInspectorView();
    }

    /// <summary>Width of the gutter at the left of the name column, where section rows draw their chevron.</summary>
    private const float NameGutter = 20.0f;
    private const float DisclosureIconSize = 12.0f;
    private const float HeaderToggleSize = 14.0f;

    private static readonly UIQuadStyle CollapsedSectionChevron = new UIStyleProperties
    {
        Icon = EUIQuadIcon.ChevronRight,
        BorderWidth = 1.7f,
        DecodeSrgbColors = true,
    }.ToQuadStyle();

    private static readonly UIQuadStyle ExpandedSectionChevron = new UIStyleProperties
    {
        Icon = EUIQuadIcon.ChevronDown,
        BorderWidth = 1.7f,
        DecodeSrgbColors = true,
    }.ToQuadStyle();

    private static readonly UIQuadStyle AddIcon = new UIStyleProperties
    {
        Icon = EUIQuadIcon.Plus,
        BorderWidth = 1.5f,
        DecodeSrgbColors = true,
    }.ToQuadStyle();

    private static readonly UIQuadStyle LinkChevron = new UIStyleProperties
    {
        Icon = EUIQuadIcon.ChevronRight,
        BorderWidth = 1.4f,
        DecodeSrgbColors = true,
    }.ToQuadStyle();

    /// <summary>
    /// Creates a small vector icon node centered at <paramref name="anchor"/> of <paramref name="parent"/> and
    /// moved by <paramref name="offset"/>. The UI quad shader draws the icon, so it needs no font glyph.
    /// </summary>
    private static (UIMaterialComponent Icon, UIBoundableTransform Transform) CreateIconNode(SceneNode parent,
        Vector2 anchor, Vector2 offset, float size, in UIQuadStyle style, ColorF4 color)
    {
        SceneNode node = parent.NewChild<UIMaterialComponent>(out var icon);
        UIBoundableTransform transform = node.GetTransformAs<UIBoundableTransform>(true)!;
        transform.MinAnchor = anchor;
        transform.MaxAnchor = anchor;
        transform.NormalizedPivot = new Vector2(0.5f, 0.5f);
        transform.Translation = offset;
        transform.Width = size;
        transform.Height = size;
        transform.BlocksInputBehind = false;
        icon.SetBatchSolidColor(color);
        icon.QuadStyle = style;
        return (icon, transform);
    }

    private UIBoundableTransform CreatePropertyValueCell(SceneNode tableRow)
    {
        // The name cell has a left gutter (the grid's leading inset). Section header rows draw their disclosure
        // chevron in it, and the member names line up after it. The chevron is a sibling of the clipped label.
        SceneNode nameNode = tableRow.Transform.Children[0].SceneNode!;
        (UIMaterialComponent disclosureIcon, UIBoundableTransform disclosureTransform) = CreateIconNode(nameNode,
            new Vector2(0.0f, 0.5f), new Vector2(NameGutter * 0.5f + 1.0f, 0.0f), DisclosureIconSize,
            ExpandedSectionChevron, EditorGlassTheme.TextSecondary);
        disclosureTransform.Visibility = EVisibility.Hidden;
        SceneNode headerToggle = nameNode.NewChild("HeaderToggle");
        UIBoundableTransform headerToggleTransform = headerToggle.SetTransform<UIBoundableTransform>();
        NativeUIElements.CreateCheckboxToggle(headerToggle, size: HeaderToggleSize);
        headerToggleTransform.MinAnchor = new Vector2(0.0f, 0.5f);
        headerToggleTransform.MaxAnchor = new Vector2(0.0f, 0.5f);
        headerToggleTransform.NormalizedPivot = new Vector2(0.0f, 0.5f);
        headerToggleTransform.Translation = new Vector2(NameGutter, 0.0f);
        headerToggleTransform.Visibility = EVisibility.Hidden;
        // The checkbox takes the click, so a click on it does not also collapse the section under it.
        headerToggleTransform.BlocksInputBehind = true;
        for (int index = 0; index < headerToggleTransform.Children.Count; index++)
            if (headerToggleTransform.Children[index] is UIBoundableTransform part)
                part.BlocksInputBehind = true;

        SceneNode valueRoot = new(tableRow) { Name = "InspectorValue" };
        UIBoundableTransform valueTransform = valueRoot.SetTransform<UIBoundableTransform>();
        valueTransform.Margins = new Vector4(2);

        // A member that has no inline editor shows its value as a quiet link row with a chevron at the right.
        // A click opens the value in the detail view.
        SceneNode buttonNode = valueRoot.NewChild<UIButtonComponent, UIMaterialComponent>(out var detailButton, out var detailBackground);
        detailBackground.SetBatchSolidColor(ColorF4.Transparent);
        detailButton.Style = EditorGlassTheme.Row;
        UIBoundableTransform buttonTransform = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
        buttonTransform.MaxAnchor = Vector2.One;
        // The chevron node comes before the label, so in painter order it follows the button background and
        // both quads draw in one run before the label text.
        (UIMaterialComponent linkIcon, _) = CreateIconNode(buttonNode, new Vector2(1.0f, 0.5f),
            new Vector2(-11.0f, 0.0f), 11.0f, LinkChevron, EditorGlassTheme.TextDisabled);
        SceneNode labelNode = buttonNode.NewChild<UITextComponent>(out var buttonLabel);
        labelNode.GetTransformAs<UIBoundableTransform>(true)!.Margins = new Vector4(8, 0, 22, 0);
        buttonLabel.Text = string.Empty;
        buttonLabel.FontSize = EditorUI.Styles.PropertyInputFontSize;
        buttonLabel.Color = EditorGlassTheme.TextSecondary;
        buttonLabel.ClipToBounds = true;
        buttonLabel.VerticalAlignment = EVerticalAlignment.Center;
        buttonLabel.HorizontalAlignment = EHorizontalAlignment.Left;
        PropertyRow state = new()
        {
            ValueRoot = valueRoot,
            Columns = CapturePropertyColumns(tableRow, valueTransform),
            DetailButton = detailButton, DetailButtonTransform = buttonTransform,
            DetailLabel = buttonLabel,
            DisclosureIcon = disclosureIcon, DisclosureTransform = disclosureTransform,
            HeaderToggle = headerToggle, HeaderToggleTransform = headerToggleTransform,
            LinkIcon = linkIcon,
        };
        detailButton.InteractAction += _ =>
        {
            PropertyInfo? clickedProperty = state.Property;
            if (clickedProperty is null)
                return;
            // An event row adds a persistent callback, like the ImGui "Add Callback" button.
            if (EditorImGuiUI.IsEventCallbackType(clickedProperty.PropertyType) &&
                _virtualPropertyProvider?.TargetsFor(clickedProperty) is [{ } eventOwner])
            {
                try { EditorImGuiUI.AddEventCallback(eventOwner, clickedProperty); }
                catch (Exception exception) { Debug.LogException(exception, $"Could not add a callback to '{clickedProperty.Name}'."); }
                RefreshAfterEventCallbackChange();
                return;
            }
            // A row of the transform or a component section edits another object than the inspected node.
            // Its details open by inspecting that object, which the Properties button returns from.
            if (_virtualPropertyProvider?.TargetsFor(clickedProperty) is { } rowTargets &&
                !SameTargets(rowTargets, InspectedObjects))
            {
                InspectedObjects = rowTargets;
                return;
            }
            ShowDetailEditor(clickedProperty);
        };
        _propertyRows.Add(valueTransform, state);
        return valueTransform;
    }

    private void BindPropertyRow(UIBoundableTransform row, PropertyInfo property, object?[]? targets)
    {
        if (_destroyingOwner)
            return;
        PropertyRow state = _propertyRows[row];
        object token = new();
        state.BindingToken = token;
        state.Columns.Revoke();
        state.DropBinding?.Retire();
        state.DropBinding = null;
        PropertyProvider? provider = _virtualPropertyProvider;
        ulong revision = provider?.Revision ?? 0;
        ulong generation = _componentGeneration;
        EditorDropObjectIdentity owner = new(this);
        EditorDropObjectIdentity visual = new(state.ValueRoot);
        object?[]? capturedTargets = targets is null ? null : (object?[])targets.Clone();
        InspectorPropertyDropBinding? binding = null;
        bool Current() => !_destroyingOwner && owner.IsCurrent && visual.IsCurrent &&
            ReferenceEquals(state.BindingToken, token) &&
            _propertyRows.TryGetValue(row, out PropertyRow? currentRow) && ReferenceEquals(currentRow, state) &&
            provider is not null && ReferenceEquals(_virtualPropertyProvider, provider) && provider.Revision == revision &&
            _componentGeneration == generation && SameTargets(provider.TargetsFor(property), capturedTargets) &&
            SameTargets(provider.Targets, InspectedObjects);
        state.Columns.Bind(Current);
        if (_propertyColumnScroll is not null && IsActiveInHierarchy && !ApplyPropertyColumnLayout(state.Columns))
        {
            if (Current()) throw new InvalidOperationException("The inspector column layout changed during row binding.");
            return;
        }
        if (!Current()) return;
        // Section header rows (the transform and each component) read brighter and larger than member rows,
        // and show a chevron that tells whether the section is open.
        bool header = provider!.IsSectionHeader(property);
        if (state.Columns.NameLabel is { } nameLabel)
        {
            nameLabel.Color = header ? EditorGlassTheme.TextPrimary : EditorGlassTheme.TextSecondary;
            nameLabel.FontSize = header ? EditorGlassTheme.FontSize : EditorGlassTheme.CaptionFontSize;
            // A section title runs across the value column, because a header row has no value editor.
            nameLabel.ClipToBounds = !header;
            if (!Current()) return;
            // A component header shows its active checkbox before the title, so the title moves right.
            bool showToggle = header && property.PropertyType == typeof(bool);
            float labelLeft = showToggle ? NameGutter + HeaderToggleSize + 6.0f : NameGutter;
            Vector4 labelMargins = nameLabel.BoundableTransform.Margins;
            if (labelMargins.X != labelLeft)
                nameLabel.BoundableTransform.Margins = labelMargins with { X = labelLeft };
            if (!Current()) return;
            if (showToggle || state.HeaderToggleShown)
            {
                NativeUIElements.RebindPropertyEditor(state.HeaderToggle, showToggle ? property : null,
                    showToggle ? targets : null, Current);
                if (!Current()) return;
                state.HeaderToggleTransform.Visibility = showToggle ? EVisibility.Visible : EVisibility.Hidden;
                state.HeaderToggleShown = showToggle;
                if (!Current()) return;
            }
        }
        UpdateDisclosureIcon(state, header && provider.SectionKeyFor(property) is { } sectionKey
            ? IsStackedSectionCollapsed(sectionKey) ? 0 : 1
            : -1);
        if (!Current()) return;
        if (targets is { Length: > 0 } && property.SetMethod?.IsPublic == true &&
            (typeof(XRComponent).IsAssignableFrom(property.PropertyType) ||
             typeof(XRAsset).IsAssignableFrom(property.PropertyType)))
            binding = new(property, targets, Current);
        InspectorStringPreviewBinding? oldPreview = state.StringPreview;
        state.StringPreview = null;
        IDisposable? oldDrop = state.DropTarget;
        state.DropTarget = null;
        state.Property = null;
        oldPreview?.Dispose();
        if (!Current()) { oldDrop?.Dispose(); return; }
        oldDrop?.Dispose();
        if (!Current()) return;
        state.Property = property;
        state.DropBinding = binding;
        if (typeof(XRComponent).IsAssignableFrom(property.PropertyType) && targets is { Length: > 0 } &&
            property.SetMethod?.IsPublic == true)
        {
            state.DropTarget = EditorDragDropUtility.RegisterDropTarget(row,
                payload =>
                {
                    if (binding?.IsCurrent != true || !ReferenceEquals(state.Property, property) ||
                        !EditorDragDropUtility.TryGetComponent(payload, out XRComponent? component) || component is null ||
                        !property.PropertyType.IsInstanceOfType(component))
                        return false;
                    try
                    {
                        EditorDropObjectIdentity componentIdentity = new(component);
                        new EditorPropertyAccess(property, binding.Targets).SetReferenceValue(component, () =>
                        {
                            binding.RequireCurrent();
                            if (!componentIdentity.IsCurrent)
                                throw new InvalidOperationException("The dropped component owner changed before assignment.");
                        });
                        binding.RequireCurrent();
                        if (!componentIdentity.IsCurrent)
                            throw new InvalidOperationException("The dropped component changed during assignment.");
                        return true;
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception, $"Failed to set component reference '{property.Name}'.");
                        return false;
                    }
                },
                payload => binding?.IsCurrent == true && ReferenceEquals(state.Property, property) &&
                    EditorDragDropUtility.TryGetComponent(payload, out XRComponent? component) &&
                    component is not null && property.PropertyType.IsInstanceOfType(component));
        }
        else if (typeof(XRAsset).IsAssignableFrom(property.PropertyType) && targets is { Length: > 0 } &&
                 property.SetMethod?.IsPublic == true)
        {
            state.DropTarget = EditorDragDropUtility.RegisterDropTarget(row,
                payload =>
                {
                    if (binding?.IsCurrent != true || !ReferenceEquals(state.Property, property) ||
                        !TryResolveAssetReference(payload, property, out string path, out Type type))
                        return false;
                    return StartAssetReferenceDrop(path, type, binding);
                },
                payload => binding?.IsCurrent == true && ReferenceEquals(state.Property, property) &&
                    TryResolveAssetReference(payload, property, out _, out _));
        }
        // A persistent callback row shows only its node picker, method picker, and remove button.
        if (property is InspectorEventCallbackRow callbackRow && targets is [{ } callbackOwner])
        {
            state.DetailButtonTransform.Visibility = EVisibility.Collapsed;
            foreach (SceneNode editor in state.PrimitiveEditors.Values)
                editor.GetTransformAs<UIBoundableTransform>()!.Visibility = EVisibility.Collapsed;
            state.ObjectField?.Unbind();
            if (!Current()) return;
            state.CallbackEditor ??= new InspectorEventCallbackEditor(state.Columns.NameCell.SceneNode!,
                state.ValueRoot, this, NameGutter);
            if (!Current()) return;
            state.CallbackEditor.Bind(callbackRow, callbackOwner);
            return;
        }
        if (state.CallbackEditor is { IsBound: true } unusedCallback)
        {
            unusedCallback.Unbind();
            if (!Current()) return;
        }
        bool primitive = HasInlineEditor(property.PropertyType);
        // A writable component, scene node, transform, or asset reference gets an object field with a picker.
        bool objectField = !primitive && !header && property.SetMethod?.IsPublic == true && targets is { Length: > 0 } &&
            InspectorObjectCandidateSource.IsObjectReferenceType(property.PropertyType);
        // A section header row that has no inline editor (the transform) shows only its title.
        state.DetailButtonTransform.Visibility = primitive || header || objectField ? EVisibility.Collapsed : EVisibility.Visible;
        if (!Current()) return;
        if (!primitive && !header && !objectField)
        {
            bool eventRow = EditorImGuiUI.IsEventCallbackType(property.PropertyType) && targets is [not null];
            state.DetailLabel.Text = eventRow ? DescribeEventCallbacks(property, targets!) : DescribeValue(property, targets);
            if (!Current()) return;
            if (state.LinkIconIsPlus != eventRow)
            {
                state.LinkIconIsPlus = eventRow;
                state.LinkIcon.QuadStyle = eventRow ? AddIcon : LinkChevron;
                if (!Current()) return;
            }
        }
        if (objectField)
        {
            state.ObjectField ??= new InspectorObjectField(state.ValueRoot, this);
            if (!Current()) return;
            state.ObjectField.Bind(property, targets!, DescribeValue(property, targets), Current);
            if (!Current()) return;
        }
        else if (state.ObjectField is { IsBound: true } unusedField)
        {
            unusedField.Unbind();
            if (!Current()) return;
        }
        foreach (SceneNode editor in state.PrimitiveEditors.Values)
        {
            editor.GetTransformAs<UIBoundableTransform>()!.Visibility = EVisibility.Collapsed;
            if (!Current()) return;
        }
        // A section header row shows only its chevron, checkbox, and title.
        if (!primitive || header)
            return;
        if (!state.PrimitiveEditors.TryGetValue(property.PropertyType, out SceneNode? editorNode))
        {
            editorNode = state.ValueRoot.NewChild();
            if (!Current()) return;
            editorNode.SetTransform<UIBoundableTransform>().MaxAnchor = Vector2.One;
            if (!Current()) return;
            InspectorPropertyEditors.CreateNew(property.PropertyType)?.Invoke(editorNode, property, targets);
            if (!Current()) return;
            // A vector or rotation editor replaces the node transform with a field row that has a fixed height.
            // The row fills the value cell instead, so its anchors stretch and it has no fixed size. Other
            // editors, such as a checkbox, keep the size that they set.
            if (editorNode.GetComponent<InspectorPropertyEditors.DataTransformerBase>() is not null &&
                editorNode.Transform is UIBoundableTransform editorBounds)
            {
                editorBounds.MinAnchor = Vector2.Zero;
                editorBounds.MaxAnchor = Vector2.One;
                editorBounds.Width = null;
                editorBounds.Height = null;
            }
            if (!Current()) return;
            state.PrimitiveEditors.Add(property.PropertyType, editorNode);
        }
        NativeUIElements.RebindPropertyEditor(editorNode, property, targets);
        if (!Current()) return;
        editorNode.GetTransformAs<UIBoundableTransform>()!.Visibility = EVisibility.Visible;
    }

    /// <summary>
    /// Returns true when the inspector edits <paramref name="type"/> inside its row: primitives, enums, strings,
    /// vectors, rotations, colors, and layer masks. Other types show their value as a link to the detail view.
    /// </summary>
    private static bool HasInlineEditor(Type type)
    {
        if (type.IsEnum)
            return true;
        TypeCode typeCode = Type.GetTypeCode(type);
        if (typeCode is not (TypeCode.Object or TypeCode.DateTime or TypeCode.Empty or TypeCode.DBNull))
            return true;
        return type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
            type == typeof(Quaternion) || type == typeof(XREngine.Data.Transforms.Rotations.Rotator) ||
            type == typeof(System.Drawing.Color) || type == typeof(ColorF3) ||
            type == typeof(ColorF4) || type == typeof(LayerMask);
    }

    /// <summary>
    /// Writes a short text for the value that a link row shows: the object name, the item count of a
    /// collection, the type name, "None" for null, or a dash when the selected objects have different values.
    /// </summary>
    private static string DescribeValue(PropertyInfo property, object?[]? targets)
    {
        if (targets is not { Length: > 0 } || !property.CanRead || property.GetIndexParameters().Length > 0)
            return string.Empty;
        object? value = null;
        try
        {
            for (int index = 0; index < targets.Length; index++)
            {
                object? current = targets[index] is { } target ? property.GetValue(target) : null;
                if (index == 0)
                    value = current;
                else if (!ReferenceEquals(value, current) && !Equals(value, current))
                    return "\u2014";
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }
        return value switch
        {
            null => "None",
            string text => text,
            XRComponent component => string.IsNullOrWhiteSpace(component.Name) || component.Name == component.GetType().Name
                ? component.GetType().Name.SplitCamelCase()
                : component.Name,
            XRObjectBase named when !string.IsNullOrWhiteSpace(named.Name) => named.Name!,
            System.Collections.ICollection collection => collection.Count == 1 ? "1 item" : $"{collection.Count} items",
            _ => value.GetType().Name.SplitCamelCase(),
        };
    }

    /// <summary>
    /// Assigns the object that a picker entry names to the property of the edited objects. A live object is
    /// written directly. An asset file loads first, through the same path as a dropped asset.
    /// </summary>
    internal void AssignObjectCandidate(PropertyInfo property, object?[] targets,
        InspectorObjectCandidateSource.Candidate candidate)
    {
        try
        {
            if (candidate.AssetPath is { } path)
            {
                InspectorPropertyDropBinding binding = new(property, targets, () => !_destroyingOwner);
                StartAssetReferenceDrop(path, property.PropertyType, binding);
                return;
            }
            if (candidate.Value is not null && !property.PropertyType.IsInstanceOfType(candidate.Value))
                return;
            new EditorPropertyAccess(property, targets).SetValue(candidate.Value);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, $"Failed to set object reference '{property.Name}'.");
        }
    }

    /// <summary>Writes the value text of an event row: the number of persistent callbacks.</summary>
    private static string DescribeEventCallbacks(PropertyInfo property, object?[] targets)
    {
        int count;
        try { count = EditorImGuiUI.GetEventCallbacks(property.GetValue(targets[0]))?.Count ?? 0; }
        catch (Exception) { return string.Empty; }
        return count switch { 0 => "No callbacks", 1 => "1 callback", _ => $"{count} callbacks" };
    }

    /// <summary>Rebuilds the rows after a callback was added, removed, or changed.</summary>
    internal void RefreshAfterEventCallbackChange()
    {
        if (_virtualPropertyProvider is not null && _virtualPropertyList is not null)
            RefreshVirtualProperties();
    }

    /// <summary>
    /// Adds one row after each event member for each of its persistent callbacks. Only an event row that edits
    /// one object gets callback rows, as in the ImGui inspector.
    /// </summary>
    private static void AddEventCallbackRows(InspectorRowLayout layout)
    {
        for (int index = 0; index < layout.Properties.Count; index++)
        {
            PropertyInfo property = layout.Properties[index];
            if (property is InspectorEventCallbackRow || !EditorImGuiUI.IsEventCallbackType(property.PropertyType) ||
                !layout.Targets.TryGetValue(property, out object?[]? targets) || targets is not [{ } owner])
                continue;
            List<XRPersistentCall>? calls;
            try { calls = EditorImGuiUI.GetEventCallbacks(property.GetValue(owner)); }
            catch (Exception) { continue; }
            if (calls is null)
                continue;
            for (int call = 0; call < calls.Count; call++)
            {
                InspectorEventCallbackRow row = InspectorEventCallbackRow.For(owner, property, call);
                if (layout.Targets.ContainsKey(row))
                    continue;
                layout.Properties.Insert(index + 1 + call, row);
                layout.Targets.Add(row, [owner]);
                layout.Names[row] = string.Empty;
            }
            index += calls.Count;
        }
    }

    /// <summary>Opens a referenced object in the inspector, like a click on a link.</summary>
    internal void InspectReferencedObject(object value) => InspectedObjects = [value];

    /// <summary>Shows, hides, or turns the section chevron of a row. It writes only when the state changes.</summary>
    private static void UpdateDisclosureIcon(PropertyRow state, int code)
    {
        if (state.DisclosureState == code)
            return;
        state.DisclosureState = code;
        state.DisclosureTransform.Visibility = code < 0 ? EVisibility.Hidden : EVisibility.Visible;
        if (code >= 0)
            state.DisclosureIcon.QuadStyle = code == 0 ? CollapsedSectionChevron : ExpandedSectionChevron;
    }

    private static bool SameTargets(object?[]? left, object?[]? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
            if (!ReferenceEquals(left[index], right[index]))
                return false;
        return true;
    }

    private static bool TryResolveAssetReference(in EditorDragDropUtility.DragPayload payload,
        PropertyInfo property, out string path, out Type type)
    {
        path = string.Empty;
        type = null!;
        if (!EditorDragDropUtility.TryGetAssetPath(payload, out string? candidate) ||
            string.IsNullOrWhiteSpace(candidate) ||
            !string.Equals(Path.GetExtension(candidate), $".{AssetManager.AssetExtension}",
                StringComparison.OrdinalIgnoreCase))
            return false;
        Type? resolved = EditorImGuiUI.ResolveNativeAssetType(candidate);
        if (resolved is null || !property.PropertyType.IsAssignableFrom(resolved) ||
            Engine.Assets is null || !File.Exists(candidate))
            return false;
        path = Path.GetFullPath(candidate);
        type = resolved;
        return true;
    }

    private bool StartAssetReferenceDrop(string path, Type type, InspectorPropertyDropBinding binding)
    {
        InspectorAssetDropRequest? request = null;
        try
        {
            binding.RequireCurrent();
            request = new(binding, () =>
                _pendingAssetDrops.TryGetValue(binding.Property, out var current) &&
                ReferenceEquals(current, request));
            _pendingAssetDrops.TryGetValue(binding.Property, out var previous);
            _pendingAssetDrops[binding.Property] = request;
            previous?.Retire();
            request.ObserveAndCapture();
            request.RequireCurrent();
            _ = AssignAssetReferenceAsync(path, type, request);
            return true;
        }
        catch (Exception exception)
        {
            if (request is not null && _pendingAssetDrops.TryGetValue(binding.Property, out var pending) &&
                ReferenceEquals(pending, request))
                _pendingAssetDrops.Remove(binding.Property);
            request?.Retire();
            Debug.LogException(exception, $"Could not prepare dropped asset for '{binding.Property.Name}'.");
            return false;
        }
    }

    private async Task AssignAssetReferenceAsync(string path, Type type, InspectorAssetDropRequest request)
    {
        XRAsset? asset;
        try
        {
            AssetManager? manager = Engine.Assets;
            if (manager is null)
            {
                EditorImGuiUI.EnqueueSceneEdit(() => FinishAssetReferenceDrop(request, null));
                return;
            }
            asset = manager.TryGetAssetByPath(path, out XRAsset? cached)
                ? cached : await manager.LoadAsync(path, type).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, $"Failed to load dropped asset '{path}'.");
            EditorImGuiUI.EnqueueSceneEdit(() => FinishAssetReferenceDrop(request, null));
            return;
        }
        EditorDropObjectIdentity? payload = asset is null ? null : new(asset);
        EditorImGuiUI.EnqueueSceneEdit(() => FinishAssetReferenceDrop(request, asset, payload));
    }

    private void FinishAssetReferenceDrop(InspectorAssetDropRequest request, XRAsset? asset,
        EditorDropObjectIdentity? capturedPayload = null)
    {
        if (!request.TryBeginCompletion())
            return;
        try
        {
            request.RequireCurrent();
            if (asset is null || capturedPayload is not { } payload || !payload.IsCurrent ||
                !request.Property.PropertyType.IsInstanceOfType(asset))
                return;
            request.ValidateValues();
            request.Access.SetReferenceValue(asset, () =>
            {
                request.RequireCurrent();
                if (!payload.IsCurrent)
                    throw new InvalidOperationException("The dropped asset lifetime changed before assignment.");
            }, request);
            request.ValidateValues();
            if (!payload.IsCurrent)
                throw new InvalidOperationException("The dropped asset changed during assignment.");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, $"Failed to set dropped asset on '{request.Property.Name}'.");
        }
        finally
        {
            if (_pendingAssetDrops.TryGetValue(request.Property, out var latest) &&
                ReferenceEquals(latest, request))
                _pendingAssetDrops.Remove(request.Property);
            request.Complete();
        }
    }

    private void UnbindPropertyRow(UIBoundableTransform row)
    {
        if (!_propertyRows.TryGetValue(row, out PropertyRow? state))
            return;
        object token = new();
        state.BindingToken = token;
        state.Columns.Revoke();
        state.DropBinding?.Retire();
        state.DropBinding = null;
        InspectorStringPreviewBinding? preview = state.StringPreview;
        state.StringPreview = null;
        IDisposable? drop = state.DropTarget;
        state.DropTarget = null;
        state.Property = null;
        preview?.Dispose();
        drop?.Dispose();
        foreach (SceneNode editor in state.PrimitiveEditors.Values)
        {
            if (!ReferenceEquals(state.BindingToken, token)) return;
            NativeUIElements.RebindPropertyEditor(editor, null, null);
            if (!ReferenceEquals(state.BindingToken, token)) return;
            editor.GetTransformAs<UIBoundableTransform>()!.Visibility = EVisibility.Collapsed;
            if (!ReferenceEquals(state.BindingToken, token)) return;
        }
        state.ObjectField?.Unbind();
        state.CallbackEditor?.Unbind();
        if (state.HeaderToggleShown)
        {
            NativeUIElements.RebindPropertyEditor(state.HeaderToggle, null, null);
            if (!ReferenceEquals(state.BindingToken, token)) return;
            state.HeaderToggleTransform.Visibility = EVisibility.Hidden;
            state.HeaderToggleShown = false;
        }
    }

    private void ShowDetailEditor(PropertyInfo property)
    {
        if (_detailEditor is { IsDestroyed: false } detailEditor && Equals(_detailProperty, property) &&
            _detailTargets is { } previous && InspectedObjects is { } current &&
            previous.Length == current.Length &&
            !previous.Where((target, index) => !ReferenceEquals(target, current[index])).Any() &&
            _detailCollectionValues?.Any(value => value is not null &&
                value is not System.Collections.IList) != true &&
            DetailCollectionsMatch(property, current) &&
            ReferenceEquals(_detailEditor, detailEditor) && !detailEditor.IsDestroyed)
        {
            detailEditor.GetComponent<InspectorListDetailRefreshComponent>()?.Refresh?.Invoke();
            return;
        }
        ulong targetIntent = _inspectedTargetIntentRevision;
        object?[]? targets = InspectedObjects;
        UIBoundableTransform? detailTransform = _detailTransform;
        UIScrollableTransform? propertyScroll = _propertyScroll;
        SceneNode? originalParentNode = detailTransform?.SceneNode;
        TransformBase? originalParentParent = detailTransform?.Parent;
        IRuntimeWorldContext? originalParentWorld = detailTransform?.World;
        TransformHierarchyStore? originalParentStore = detailTransform?.HierarchyStore;
        TransformHandle originalParentHandle = detailTransform?.HierarchyHandle ?? default;
        ObjectConstructionIdentity? originalParentIdentity = detailTransform is null
            ? null : ObjectConstructionIdentity.Capture(detailTransform);
        ObjectConstructionIdentity? originalParentNodeIdentity = originalParentNode is null
            ? null : ObjectConstructionIdentity.Capture(originalParentNode);
        ObjectConstructionIdentity? originalParentNodeTransformIdentity = originalParentNode is null
            ? null : ObjectConstructionIdentity.Capture(originalParentNode.Transform);
        ObjectConstructionIdentity? originalParentComponentsIdentity = originalParentNode is null
            ? null : ObjectConstructionIdentity.Capture(originalParentNode.OwnedRetirementComponents!);
        ObjectConstructionIdentity? originalParentChildrenIdentity = detailTransform is null
            ? null : ObjectConstructionIdentity.Capture(detailTransform.OwnedRetirementChildren!);
        ObjectConstructionIdentity? originalParentParentIdentity = originalParentParent is null
            ? null : ObjectConstructionIdentity.Capture(originalParentParent);
        long originalParentRevision = detailTransform?.HierarchyMembershipRevision ?? 0;
        bool OriginalParentCurrent() => detailTransform is { IsDestroyed: false, IsDestroyQueued: false } parent &&
            originalParentNode is { IsDestroyed: false, IsDestroyQueued: false } originalNode &&
            originalParentIdentity is { IsCurrent: true } &&
            originalParentNodeIdentity is { IsCurrent: true } &&
            originalParentNodeTransformIdentity is { IsCurrent: true } &&
            originalParentComponentsIdentity is { IsCurrent: true } &&
            originalParentChildrenIdentity is { IsCurrent: true } &&
            (originalParentParentIdentity is null || originalParentParentIdentity.Value.IsCurrent) &&
            ReferenceEquals(parent.SceneNode, originalNode) &&
            ReferenceEquals(originalNode.Transform, parent) &&
            ReferenceEquals(originalNode.OwnedRetirementComponents,
                originalParentComponentsIdentity.Value.Target) &&
            ReferenceEquals(parent.OwnedRetirementChildren,
                originalParentChildrenIdentity.Value.Target) &&
            ReferenceEquals(parent.Parent, originalParentParent) &&
            parent.HasUsableHierarchyMembershipRevision &&
            parent.HierarchyMembershipRevision == originalParentRevision &&
            ReferenceEquals(parent.World, originalParentWorld) &&
            ReferenceEquals(parent.HierarchyStore, originalParentStore) &&
            parent.HierarchyHandle == originalParentHandle;
        if (!HideDetailEditor()) return;
        ulong cleanup = _detailCleanupRevision;
        object?[]? detailTargets = null;
        bool building = false;
        bool CurrentRequest() => cleanup == _detailCleanupRevision &&
            IsCurrentTargetNotification(targetIntent, targets) &&
            ReferenceEquals(_detailTransform, detailTransform) &&
            ReferenceEquals(_propertyScroll, propertyScroll) && _detailEditor is null &&
            (building ? ReferenceEquals(_detailProperty, property) &&
                ReferenceEquals(_detailTargets, detailTargets) : _detailProperty is null) &&
            OriginalParentCurrent() && propertyScroll is not null;
        if (!CurrentRequest()) return;
        _detailProperty = property;
        detailTargets = targets?.ToArray();
        _detailTargets = detailTargets;
        building = true;
        object?[]? collectionValues = CaptureDetailCollections(property, targets);
        if (!CurrentRequest()) return;
        _detailCollectionValues = collectionValues;
        bool stringDetail = property.PropertyType == typeof(string);
        // Reserve custody before allocating: a reentrant getter may start another build.
        if (_activeDetailConstructions.Count + _retainedDetailConstructions.Count >= 8)
            throw new InvalidOperationException("Too many unresolved detail host constructions.");
        TransformBase hostTransform = stringDetail ? new UIBoundableTransform() : new UIListTransform();
        SceneNode host = new(stringDetail ? originalParentWorld : null, "InspectorDetail", hostTransform)
            { IsActiveSelf = false };
        DetachedLeafRetirementReceipt retirement = host.World is { } hostWorld
            ? new(host, hostWorld) : new(host);
        InspectorDetailConstructionAttempt attempt = new(host, retirement);
        _activeDetailConstructions.Add(attempt);
        ObjectConstructionIdentity hostIdentity = ObjectConstructionIdentity.Capture(host);
        ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(hostTransform);
        bool OwnsDetachedHost() => hostIdentity.IsCurrent && transformIdentity.IsCurrent &&
            (stringDetail ? attempt.OwnsOriginalDetachedHost : attempt.OwnsOriginalDetachedHostBoundary);
        bool published = false;
        try
        {
            if (!CurrentRequest() || !OwnsDetachedHost())
            {
                RetireUnpublishedDetailHost(attempt, hostTransform, hostIdentity, transformIdentity);
                return;
            }
            if (stringDetail)
                CreateMultilineStringDetail(attempt, property, targets, CurrentRequest);
            else
            {
                UIListTransform hostLayout = (UIListTransform)hostTransform;
                hostLayout.MinAnchor = new Vector2(0, 1);
                hostLayout.MaxAnchor = new Vector2(1, 1);
                hostLayout.NormalizedPivot = new Vector2(0, 1);
                hostLayout.Translation = new Vector2(0, -34);
                hostLayout.ItemAlignment = EListAlignment.TopOrLeft;
                hostLayout.DisplayHorizontal = false;
                if (CurrentRequest() && OwnsDetachedHost())
                    InspectorPropertyEditors.CreateNew(property.PropertyType)?.Invoke(host, property, targets);
            }
            if (!CurrentRequest() || !OwnsDetachedHost() ||
                stringDetail && (!attempt.OwnsOriginalDetachedText ||
                    !attempt.OwnsComponents(host, attempt.HostComponents,
                        attempt.HostComponentMembershipRevisions) ||
                    !attempt.OwnsComponents(attempt.TextNode!, attempt.TextComponents,
                        attempt.TextComponentMembershipRevisions)) ||
                PhysicsChainWorld.IsWorldMutationActive)
            {
                RetireUnpublishedDetailHost(attempt, hostTransform, hostIdentity, transformIdentity);
                return;
            }
            IRuntimeWorldContext? expectedWorld = originalParentWorld;
            attempt.ExpectedParent = detailTransform;
            attempt.ExpectedParentIdentity = originalParentIdentity;
            attempt.ExpectedParentNodeIdentity = originalParentNodeIdentity;
            attempt.ExpectedParentNodeTransformIdentity = originalParentNodeTransformIdentity;
            attempt.ExpectedParentComponentsIdentity = originalParentComponentsIdentity;
            attempt.ExpectedParentChildrenIdentity = originalParentChildrenIdentity;
            attempt.ExpectedParentParent = originalParentParent;
            attempt.ExpectedParentParentIdentity = originalParentParentIdentity;
            attempt.ExpectedParentMembershipRevision = originalParentRevision;
            attempt.ExpectedWorld = expectedWorld;
            attempt.ExpectedParentStore = originalParentStore;
            attempt.ExpectedParentHandle = originalParentHandle;
            if (stringDetail)
            {
                attempt.HostAttachment = new DetachedLeafAttachmentReceipt(attempt.HostRetirement,
                    detailTransform!);
                attempt.HostAttachment.Attach();
            }
            else
                hostTransform.Parent = detailTransform!;
            if (stringDetail && OriginalParentCurrent() &&
                ReferenceEquals(host.World, expectedWorld) &&
                ReferenceEquals(hostTransform.Parent, detailTransform) &&
                host.HierarchyMembershipRevision == attempt.HostMembershipRevision &&
                hostTransform.HierarchyMembershipRevision == attempt.HostTransformMembershipRevision + 1 &&
                attempt.OwnsComponents(host, attempt.HostComponents,
                    attempt.HostComponentMembershipRevisions))
                attempt.AdoptedHostRetirement = new AdoptedLeafRetirementReceipt(host, detailTransform!,
                    attempt.HostComponents);
            if (!CurrentRequest() || !OriginalParentCurrent() ||
                stringDetail && attempt.AdoptedHostRetirement is null || !hostIdentity.IsCurrent ||
                !transformIdentity.IsCurrent ||
                !ReferenceEquals(host.Transform, hostTransform) ||
                !ReferenceEquals(hostTransform.Parent, detailTransform))
            {
                RetireUnpublishedDetailHost(attempt, hostTransform, hostIdentity, transformIdentity,
                    detailTransform);
                return;
            }
            if (stringDetail && attempt.TextNode is { } textNode)
            {
                if (!attempt.OwnsOriginalDetachedText ||
                    !attempt.OwnsComponents(textNode, attempt.TextComponents,
                        attempt.TextComponentMembershipRevisions))
                {
                    RetireUnpublishedDetailHost(attempt, hostTransform, hostIdentity, transformIdentity,
                        detailTransform);
                    return;
                }
                TransformBase textTransform = attempt.TextTransform!;
                attempt.TextAttachment = new DetachedLeafAttachmentReceipt(attempt.TextRetirement!,
                    hostTransform);
                attempt.TextAttachment.Attach();
                if (attempt.TextNodeIdentity is { IsCurrent: true } &&
                    attempt.TextTransformIdentity is { IsCurrent: true } &&
                    ReferenceEquals(textNode.Transform, textTransform) &&
                    ReferenceEquals(textTransform.Parent, hostTransform) &&
                    ReferenceEquals(textNode.World, expectedWorld) &&
                    OriginalParentCurrent() &&
                    textNode.HierarchyMembershipRevision == attempt.TextMembershipRevision &&
                    textTransform.HierarchyMembershipRevision == attempt.TextTransformMembershipRevision + 1 &&
                    attempt.OwnsComponents(textNode, attempt.TextComponents,
                        attempt.TextComponentMembershipRevisions))
                    attempt.AdoptedTextRetirement = new AdoptedLeafRetirementReceipt(textNode,
                        hostTransform, attempt.TextComponents);
                if (!CurrentRequest() || !ReferenceEquals(textTransform.Parent, hostTransform) ||
                    attempt.AdoptedTextRetirement is null)
                {
                    RetireUnpublishedDetailHost(attempt, hostTransform, hostIdentity, transformIdentity,
                        detailTransform);
                    return;
                }
                attempt.Size?.Bind(attempt.Text!, (UIBoundableTransform)hostTransform);
                if (!CurrentRequest())
                {
                    RetireUnpublishedDetailHost(attempt, hostTransform, hostIdentity, transformIdentity,
                        detailTransform);
                    return;
                }
            }
            _detailEditor = host;
            published = true;
            host.IsActiveSelf = true;
            if (!ReferenceEquals(_detailEditor, host) || !CurrentRequestOwner()) return;
            if (stringDetail)
            {
                RequirePublishedStringDetail();
                attempt.TextNode!.ActivateLeafGuarded(RequirePublishedStringDetail);
                if (!ReferenceEquals(_detailEditor, host) || !CurrentRequestOwner()) return;
                RequirePublishedStringDetail();
                if (!attempt.TextNode.IsActiveInHierarchy)
                    throw new InvalidOperationException("The string detail text did not activate under its current owner.");
            }
            detailTransform!.Visibility = EVisibility.Visible;
            if (ReferenceEquals(_detailEditor, host) && CurrentRequestOwner())
                propertyScroll!.Visibility = EVisibility.Collapsed;
        }
        catch
        {
            // A newer request can keep this host while it waits for focus to settle.
            // Its panel flow now owns retirement, even if this activation callback fails.
            bool handedToNewerRequest = published && ReferenceEquals(_detailEditor, host) &&
                !CurrentRequestOwner();
            if (!handedToNewerRequest)
                RetireUnpublishedDetailHost(attempt, hostTransform, hostIdentity, transformIdentity,
                    detailTransform);
            if (host.IsDestroyed && ReferenceEquals(_detailEditor, host) &&
                cleanup == _detailCleanupRevision &&
                ReferenceEquals(_detailTransform, detailTransform) &&
                ReferenceEquals(_propertyScroll, propertyScroll) &&
                Equals(_detailProperty, property) &&
                ReferenceEquals(_detailTargets, detailTargets))
            {
                _detailEditor = null;
                _detailProperty = null;
                _detailTargets = null;
                _detailCollectionValues = null;
            }
            throw;
        }
        finally
        {
            _activeDetailConstructions.Remove(attempt);
        }

        bool CurrentRequestOwner() => cleanup == _detailCleanupRevision &&
            IsCurrentTargetNotification(targetIntent, targets) &&
            ReferenceEquals(_detailTransform, detailTransform) &&
            ReferenceEquals(_propertyScroll, propertyScroll) &&
            ReferenceEquals(_detailProperty, property) &&
            ReferenceEquals(_detailTargets, detailTargets);

        void RequirePublishedStringDetail()
        {
            if (!ReferenceEquals(_detailEditor, host) || !CurrentRequestOwner() ||
                !OriginalParentCurrent() || !host.IsActiveInHierarchy ||
                !hostIdentity.IsCurrent || !transformIdentity.IsCurrent ||
                attempt.AdoptedHostRetirement is not { HasCurrentIdentity: true } ||
                attempt.AdoptedTextRetirement is not { HasCurrentIdentity: true } ||
                attempt.TextNode is not { IsDestroyed: false, IsDestroyQueued: false } textNode ||
                attempt.TextTransform is not { IsDestroyed: false, IsDestroyQueued: false } textTransform ||
                !ReferenceEquals(host.Transform, hostTransform) ||
                !ReferenceEquals(hostTransform.Parent, detailTransform) ||
                !ReferenceEquals(host.World, originalParentWorld) ||
                !ReferenceEquals(hostTransform.World, originalParentWorld) ||
                !ReferenceEquals(textNode.Transform, textTransform) ||
                !ReferenceEquals(textTransform.Parent, hostTransform) ||
                !ReferenceEquals(textNode.World, originalParentWorld) ||
                !ReferenceEquals(textTransform.World, originalParentWorld) ||
                hostTransform.ChildCount != 1 || !hostTransform.Children.Contains(textTransform) ||
                textTransform.ChildCount != 0 ||
                !attempt.OwnsComponents(host, attempt.HostComponents, attempt.HostComponentMembershipRevisions) ||
                !attempt.OwnsComponents(textNode, attempt.TextComponents, attempt.TextComponentMembershipRevisions))
                throw new InvalidOperationException("The published string detail changed ownership before activation.");
        }
    }

    private void CreateMultilineStringDetail(InspectorDetailConstructionAttempt attempt, PropertyInfo property,
        object?[]? targets, Func<bool> requestIsCurrent)
    {
        SceneNode host = attempt.Host;
        UIBoundableTransform bounds = host.GetTransformAs<UIBoundableTransform>()!;
        bounds.MinAnchor = new Vector2(0, 1);
        bounds.MaxAnchor = Vector2.One;
        bounds.NormalizedPivot = new Vector2(0, 1);
        bounds.Translation = new Vector2(0, -34);
        ObjectConstructionIdentity hostIdentity = ObjectConstructionIdentity.Capture(host);
        ObjectConstructionIdentity boundsIdentity = ObjectConstructionIdentity.Capture(bounds);
        bool HostCurrent() => requestIsCurrent() && hostIdentity.IsCurrent && boundsIdentity.IsCurrent &&
            attempt.OwnsOriginalDetachedHost;
        T Add<T>(SceneNode node, DetachedLeafRetirementReceipt leaf, Func<bool> current,
            List<RuntimeComponentCreationReceipt> attempts, List<long> revisions)
            where T : XRComponent, new()
        {
            RuntimeComponentCreationReceipt receipt = new(node, current);
            leaf.Register(receipt);
            attempts.Add(receipt);
            T component = node.AddComponentGuarded(static () => new T(), receipt) ??
                throw new InvalidOperationException("A detail component factory returned no component.");
            revisions.Add(component.HierarchyMembershipRevision);
            return component;
        }

        UIMaterialComponent material = Add<UIMaterialComponent>(host, attempt.HostRetirement,
            HostCurrent, attempt.HostComponents, attempt.HostComponentMembershipRevisions);
        material.SetBatchBorderStyle(ColorF4.Transparent, ColorF4.Transparent, 2.0f);
        if (!HostCurrent()) return;
        UIBoundableTransform textBounds = new();
        SceneNode textNode = new(attempt.HostRetirement.SourceWorld, "DetailText", textBounds)
            { IsActiveSelf = false };
        DetachedLeafRetirementReceipt textRetirement = textNode.World is { } textWorld
            ? new(textNode, textWorld) : new(textNode);
        attempt.CaptureTextNode(textNode, textRetirement);
        ObjectConstructionIdentity textNodeIdentity = ObjectConstructionIdentity.Capture(textNode);
        ObjectConstructionIdentity textBoundsIdentity = ObjectConstructionIdentity.Capture(textBounds);
        bool TextCurrent() => HostCurrent() && textNodeIdentity.IsCurrent && textBoundsIdentity.IsCurrent &&
            attempt.OwnsOriginalDetachedText;
        UITextComponent text = Add<UITextComponent>(textNode, textRetirement, TextCurrent,
            attempt.TextComponents, attempt.TextComponentMembershipRevisions);
        UITextInputComponent input = Add<UITextInputComponent>(textNode, textRetirement,
            TextCurrent, attempt.TextComponents, attempt.TextComponentMembershipRevisions);
        UIPropertyTextDriverComponent driver = Add<UIPropertyTextDriverComponent>(textNode,
            textRetirement, TextCurrent, attempt.TextComponents,
            attempt.TextComponentMembershipRevisions);
        InspectorStringDetailSizeComponent size = Add<InspectorStringDetailSizeComponent>(host,
            attempt.HostRetirement, HostCurrent, attempt.HostComponents,
            attempt.HostComponentMembershipRevisions);
        attempt.Text = text;
        attempt.Size = size;
        if (!TextCurrent()) return;

        void SetOutline(bool highlighted)
        {
            bool MaterialCurrent() => hostIdentity.IsCurrent && boundsIdentity.IsCurrent &&
                !host.IsDestroyed && !host.IsDestroyQueued && ReferenceEquals(host.Transform, bounds) &&
                (bounds.Parent is null ? requestIsCurrent() : ReferenceEquals(_detailEditor, host));
            if (!MaterialCurrent()) return;
            Vector4 color = highlighted ? ColorF4.White : ColorF4.Transparent;
            if (material.BatchBorderColor == color) return;
            material.UITransform.RecordUIChange(UIChangeDomain.Visual);
            if (MaterialCurrent()) material.BatchBorderColor = color;
        }
        input.GotFocus += _ => SetOutline(true);
        input.MouseDirectOverlapEnter += _ => SetOutline(true);
        input.LostFocus += _ => SetOutline(input.CanInteract && input.IsMouseDirectlyOver);
        input.MouseDirectOverlapLeave += _ => SetOutline(input.CanInteract && input.IsFocused);
        input.IsReadOnly = !property.CanWrite;
        text.FontSize = EditorUI.Styles.PropertyInputFontSize;
        text.Color = EditorUI.Styles.PropertyInputTextColor;
        text.HorizontalAlignment = EHorizontalAlignment.Left;
        input.SingleLineMode = false;
        input.CommitOnSubmit = true;
        text.VerticalAlignment = EVerticalAlignment.Top;
        text.WrapMode = FontGlyphSet.EWrapMode.Word;
        textBounds.MinAnchor = Vector2.Zero;
        textBounds.MaxAnchor = Vector2.One;
        textBounds.Margins = new Vector4(5.0f, 2.0f, 5.0f, 2.0f);
        text.ClipToBounds = true;
        driver.IsActive = false;
        bounds.Height = 180;
        if (!TextCurrent()) return;
        // Binding may invoke the target getter. Keep both leaves detached until it returns.
        if (targets is not null)
            input.Binding = new EditorTextPropertyBinding(property, targets);
    }

    private void RetireUnpublishedDetailHost(InspectorDetailConstructionAttempt attempt, TransformBase hostTransform,
        ObjectConstructionIdentity hostIdentity, ObjectConstructionIdentity transformIdentity,
        UIBoundableTransform? expectedParent = null)
    {
        SceneNode host = attempt.Host;
        try
        {
            if (PhysicsChainWorld.IsWorldMutationActive)
            {
                RetainFailedDetailConstruction(attempt,
                    new InvalidOperationException("The detail host cannot retire during a world transfer."));
                return;
            }
            if (attempt.TextNode is { IsDestroyed: false } textNode)
            {
                TransformBase? textTransform = attempt.TextTransform;
                if (attempt.AdoptedTextRetirement is null &&
                    textTransform is not null &&
                    attempt.TextNodeIdentity is { IsCurrent: true } &&
                    attempt.TextTransformIdentity is { IsCurrent: true } &&
                    ReferenceEquals(textNode.Transform, textTransform) &&
                    ReferenceEquals(textTransform.Parent, hostTransform) &&
                    ReferenceEquals(textNode.World, attempt.ExpectedWorld) &&
                    textNode.HierarchyMembershipRevision == attempt.TextMembershipRevision &&
                    textTransform.HierarchyMembershipRevision == attempt.TextTransformMembershipRevision + 1 &&
                    attempt.OwnsComponents(textNode, attempt.TextComponents,
                        attempt.TextComponentMembershipRevisions))
                    attempt.AdoptedTextRetirement = new AdoptedLeafRetirementReceipt(textNode,
                        hostTransform, attempt.TextComponents);
                bool retiredText = attempt.AdoptedTextRetirement is { } adopted
                    ? adopted.TryRetire() : attempt.TextRetirement is { } textRetirement &&
                        RetireDetachedDetailLeaf(textRetirement);
                if (!retiredText)
                {
                    RetainFailedDetailConstruction(attempt,
                        attempt.AdoptedTextRetirement?.Failure ?? attempt.TextRetirement?.Failure ??
                        new InvalidOperationException("The unpublished detail text leaf could not retire."));
                    return;
                }
            }
            if (host.IsDestroyed)
                return;
            if (!hostIdentity.IsCurrent || !transformIdentity.IsCurrent ||
                !ReferenceEquals(host.Transform, hostTransform) || host.IsDestroyQueued)
            {
                RetainFailedDetailConstruction(attempt,
                    new InvalidOperationException("The unpublished detail host changed lifetime or transform."));
                return;
            }
            if (attempt.AdoptedHostRetirement is null && expectedParent is not null &&
                ReferenceEquals(expectedParent, attempt.ExpectedParent) &&
                attempt.OwnsExpectedParent &&
                ReferenceEquals(hostTransform.Parent, expectedParent) &&
                ReferenceEquals(host.World, attempt.ExpectedWorld) &&
                ReferenceEquals(expectedParent.World, attempt.ExpectedWorld) &&
                host.HierarchyMembershipRevision == attempt.HostMembershipRevision &&
                hostTransform.HierarchyMembershipRevision == attempt.HostTransformMembershipRevision + 1 &&
                attempt.OwnsComponents(host, attempt.HostComponents,
                    attempt.HostComponentMembershipRevisions))
                attempt.AdoptedHostRetirement = new AdoptedLeafRetirementReceipt(host,
                    expectedParent, attempt.HostComponents);
            bool retiredHost = attempt.AdoptedHostRetirement is { } adoptedHost
                ? adoptedHost.TryRetire() : RetireDetachedDetailLeaf(attempt.HostRetirement);
            if (!retiredHost)
                RetainFailedDetailConstruction(attempt,
                    attempt.AdoptedHostRetirement?.Failure ?? attempt.HostRetirement.Failure ??
                    new InvalidOperationException("The unpublished detail host could not retire."));
        }
        catch (Exception error)
        {
            RetainFailedDetailConstruction(attempt, error);
        }
    }

    private void RetainFailedDetailConstruction(InspectorDetailConstructionAttempt attempt, Exception error)
    {
        // The attempt reserved its slot before any allocation or reentrant callback.
        if (!_retainedDetailConstructions.Contains(attempt))
            _retainedDetailConstructions.Add(attempt);
        Debug.LogException(error, "Could not retire an unpublished inspector detail host.");
    }

    private static bool RetireDetachedDetailLeaf(DetachedLeafRetirementReceipt retirement)
        => retirement.SourceWorld is { } world
            ? retirement.TryRetireFromWorld(world) : retirement.TryRetire();

    private bool HideDetailEditor()
    {
        _detailHideWaitsForFocus = false;
        if (PhysicsChainWorld.IsWorldMutationActive)
            return false;
        ulong cleanup = ++_detailCleanupRevision;
        SceneNode? editor = _detailEditor;
        UIBoundableTransform? detailTransform = _detailTransform;
        UIScrollableTransform? propertyScroll = _propertyScroll;
        PropertyInfo? detailProperty = _detailProperty;
        object?[]? detailTargets = _detailTargets;
        ulong targetIntent = _inspectedTargetIntentRevision;
        bool CurrentFields() => cleanup == _detailCleanupRevision &&
            _inspectedTargetIntentRevision == targetIntent && ReferenceEquals(_detailEditor, editor) &&
            ReferenceEquals(_detailTransform, detailTransform) &&
            ReferenceEquals(_propertyScroll, propertyScroll) && Equals(_detailProperty, detailProperty) &&
            ReferenceEquals(_detailTargets, detailTargets);
        bool CurrentEditor() => CurrentFields() && editor is { IsDestroyed: false } &&
            ReferenceEquals(editor.Transform.Parent, detailTransform);
        if (editor is not null)
        {
            if (!CurrentEditor()) return false;
            InspectorListDetailRefreshComponent? listRefresh = editor.GetComponent<InspectorListDetailRefreshComponent>();
            InspectorStringDetailSizeComponent? stringSize = editor.GetComponent<InspectorStringDetailSizeComponent>();
            IUICanvasInputSource? input = detailTransform?.GetCanvasComponent()?.GetInputComponent();
            UIInteractableComponent? focused = input?.FocusedComponent;
            ulong focusIntent = input?.FocusIntentRevision ?? 0;
            if (input?.HasPendingFocusRequest == true)
            {
                _detailHideWaitsForFocus = true;
                return false;
            }
            if (input is not null && focusIntent != 0 &&
                ReferenceEquals(detailTransform?.GetCanvasComponent()?.GetInputComponent(), input) &&
                !input.HasPendingFocusRequest && ReferenceEquals(input.FocusedComponent, focused) &&
                input.FocusIntentRevision == focusIntent && focused?.SceneNode is { } focusedNode &&
                IsDetailDescendant(focusedNode, editor))
            {
                bool released = input.TryReleaseFocusPreservingDraft(focused, focusIntent,
                    CurrentEditor, out ulong releasedIntent);
                if (!CurrentEditor()) return false;
                if (released && ReferenceEquals(_savedDetailFocusInput, input) &&
                    ReferenceEquals(_savedDetailFocusedControl, focused) &&
                    _savedDetailFocusIntentRevision == focusIntent)
                    _savedDetailReleasedIntentRevision = releasedIntent;
                if (!released && input.FocusedComponent?.SceneNode is { } remainingNode &&
                    IsDetailDescendant(remainingNode, editor))
                    return false;
            }
            else if (input?.FocusedComponent?.SceneNode is { } remainingFocusedNode &&
                     IsDetailDescendant(remainingFocusedNode, editor))
                return false;
            if (!CurrentEditor()) return false;
            editor.IsActiveSelf = false;
            if (!CurrentEditor()) return false;
            editor.Destroy(true);
            if (!CurrentFields()) return false;
            if (!editor.IsDestroyed)
            {
                if (CurrentEditor())
                    editor.IsActiveSelf = true;
                return false;
            }
            _detailEditor = null;
            _detailProperty = null;
            _detailTargets = null;
            _detailCollectionValues = null;
            if (detailTransform is not null)
                detailTransform.Visibility = EVisibility.Collapsed;
            if (cleanup == _detailCleanupRevision && _detailEditor is null &&
                ReferenceEquals(_detailTransform, detailTransform) &&
                ReferenceEquals(_propertyScroll, propertyScroll) && propertyScroll is not null)
                propertyScroll.Visibility = EVisibility.Visible;
            if (ReferenceEquals(listRefresh?.SceneNode, editor))
                listRefresh.Retire();
            if (ReferenceEquals(stringSize?.SceneNode, editor))
                stringSize.Retire();
            bool accepted = cleanup == _detailCleanupRevision && _detailEditor is null &&
                ReferenceEquals(_detailTransform, detailTransform) &&
                ReferenceEquals(_propertyScroll, propertyScroll) &&
                _inspectedTargetIntentRevision == targetIntent;
            if (accepted) _staleDetailSuspended = false;
            return accepted;
        }
        if (!CurrentFields()) return false;
        _detailProperty = null;
        _detailTargets = null;
        _detailCollectionValues = null;
        if (detailTransform is not null)
            detailTransform.Visibility = EVisibility.Collapsed;
        if (cleanup == _detailCleanupRevision && _detailEditor is null &&
            ReferenceEquals(_detailTransform, detailTransform) &&
            ReferenceEquals(_propertyScroll, propertyScroll) && propertyScroll is not null)
            propertyScroll.Visibility = EVisibility.Visible;
        bool completed = cleanup == _detailCleanupRevision && _detailEditor is null &&
            ReferenceEquals(_detailTransform, detailTransform) &&
            ReferenceEquals(_propertyScroll, propertyScroll) &&
            _inspectedTargetIntentRevision == targetIntent;
        if (completed) _staleDetailSuspended = false;
        return completed;
    }

    private static bool IsDetailDescendant(SceneNode node, SceneNode root)
    {
        for (SceneNode? current = node; current is not null;
             current = current.Transform.Parent?.SceneNode)
            if (ReferenceEquals(current, root))
                return true;
        return false;
    }

    private bool DetailCollectionsMatch(PropertyInfo property, object?[] targets)
    {
        object?[]? previous = _detailCollectionValues;
        if (previous is null) return true;
        if (previous.Length != targets.Length) return false;
        try
        {
            for (int index = 0; index < targets.Length; index++)
                if (!ReferenceEquals(previous[index], targets[index] is null
                    ? null : property.GetValue(targets[index])))
                    return false;
        }
        catch { return false; }
        return true;
    }

    private static object?[]? CaptureDetailCollections(PropertyInfo property, object?[]? targets)
    {
        if (targets is null || !typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType) ||
            property.PropertyType == typeof(string))
            return null;
        try { return targets.Select(target => target is null ? null : property.GetValue(target)).ToArray(); }
        catch { return null; }
    }

    private void ReleaseVirtualPropertyList()
    {
        if (PhysicsChainWorld.IsWorldMutationActive) return;
        if (!HideDetailEditor()) return;
        StopDeferredTargetRefresh();
        ReleaseVirtualPropertyOwners();
        if (_virtualPropertyList is not null)
            _virtualPropertyList.VirtualProvider = null;
        _virtualPropertyProvider = null;
        _virtualPropertyGrid = null;
        _virtualPropertyList = null;
        _propertyScroll = null;
        _detailTransform = null;
        ReleaseComponentViews();
    }

    private void QueueDeferredTargetRefresh()
    {
        _targetRefreshPending = true;
        _targetRefreshWaitFrames = 30;
        if (_targetRefreshTickRegistered || !IsActiveInHierarchy || _destroyingOwner)
            return;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, RetryDeferredTargetRefresh);
        _targetRefreshTickRegistered = true;
    }

    private void SuspendStaleDetailEditor()
    {
        if (_detailEditor is not { IsDestroyed: false } editor ||
            SameTargets(_detailTargets, InspectedObjects))
            return;
        _staleDetailSuspended = true;
        editor.IsActiveSelf = false;
        if (ReferenceEquals(_detailEditor, editor) && !SameTargets(_detailTargets, InspectedObjects))
        {
            if (_detailTransform is not null)
                _detailTransform.Visibility = EVisibility.Collapsed;
            if (_propertyScroll is not null)
                _propertyScroll.Visibility = EVisibility.Visible;
        }
    }

    private void RetryDeferredTargetRefresh()
    {
        if (!_targetRefreshPending || _destroyingOwner || IsDestroyed || IsDestroyQueued)
        {
            StopDeferredTargetRefresh();
            return;
        }
        if (!IsActiveInHierarchy)
        {
            PauseDeferredTargetRefresh();
            return;
        }
        if (PhysicsChainWorld.IsWorldMutationActive)
            return;
        if (--_targetRefreshWaitFrames <= 0)
        {
            StopDeferredTargetRefresh();
            return;
        }
        IUICanvasInputSource? input = _detailTransform?.GetCanvasComponent()?.GetInputComponent();
        if (input?.HasPendingFocusRequest == true)
            return;
        StopDeferredTargetRefresh();
        if (_detailEditor is null || SameTargets(_detailTargets, InspectedObjects) || HideDetailEditor())
        {
            // The source may already be current while its structural refresh was deferred.
            RemakeChildren();
            ApplyInspectorView();
        }
    }

    private void PauseDeferredTargetRefresh()
    {
        if (!_targetRefreshTickRegistered) return;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RetryDeferredTargetRefresh);
        _targetRefreshTickRegistered = false;
    }

    private void StopDeferredTargetRefresh()
    {
        _targetRefreshPending = false;
        PauseDeferredTargetRefresh();
    }

    private void ReleaseVirtualPropertyOwners(bool terminal = false)
    {
        StopPropertyColumnLayout();
        PropertyRow[] rows = [.. _propertyRows.Values];
        _propertyRows.Clear();
        foreach (PropertyRow row in rows)
        {
            row.BindingToken = new();
            row.DropBinding?.Retire();
            row.DropBinding = null;
        }
        if (terminal)
        {
            _virtualPropertyProvider?.Reset([], null);
            _detailProperty = null;
            _detailEditor = null;
        }
        InspectorAssetDropRequest[] pending = [.. _pendingAssetDrops.Values];
        _pendingAssetDrops.Clear();
        foreach (InspectorAssetDropRequest request in pending)
        {
            request.Retire();
        }
        foreach (PropertyRow row in rows)
        {
            row.StringPreview?.Dispose();
            row.StringPreview = null;
            IDisposable? dropTarget = row.DropTarget;
            row.DropTarget = null;
            dropTarget?.Dispose();
        }
    }
}

using System.ComponentModel;
using System.Numerics;
using System.Reflection;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class InspectorPanel
{
    private sealed class PropertyRow
    {
        public required SceneNode ValueRoot;
        public required UIButtonComponent DetailButton;
        public required UIBoundableTransform DetailButtonTransform;
        public required UITextComponent DetailLabel;
        public readonly Dictionary<Type, SceneNode> PrimitiveEditors = [];
        public PropertyInfo? Property;
        public IDisposable? DropTarget;
    }

    private sealed class AssetDropRequest(PropertyInfo property, object?[] targets,
        EditorPropertyAccess access, object?[] before, ulong providerRevision, ulong generation)
    {
        private int _invalidated;
        private readonly List<Action> _unsubscribe = [];
        public PropertyInfo Property { get; } = property;
        public object?[] Targets { get; } = targets;
        public EditorPropertyAccess Access { get; } = access;
        public object?[] Before { get; } = before;
        public ulong ProviderRevision { get; } = providerRevision;
        public ulong Generation { get; } = generation;
        public IRuntimeWorldContext?[] Worlds { get; } = targets
            .Select(target => (target as XRWorldObjectBase)?.World).ToArray();
        public SceneNode?[] Owners { get; } = targets
            .Select(target => (target as XRComponent)?.SceneNode).ToArray();
        public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;
        public void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

        public void ObserveSynchronousChanges()
        {
            foreach (object? target in Targets)
            {
                if (target is IXRNotifyPropertyChanged engineObject)
                {
                    XRPropertyChangedEventHandler handler = (_, args) =>
                    {
                        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == Property.Name)
                            Invalidate();
                    };
                    engineObject.PropertyChanged += handler;
                    _unsubscribe.Add(() => engineObject.PropertyChanged -= handler);
                }
                else if (target is INotifyPropertyChanged observable)
                {
                    PropertyChangedEventHandler handler = (_, args) =>
                    {
                        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == Property.Name)
                            Invalidate();
                    };
                    observable.PropertyChanged += handler;
                    _unsubscribe.Add(() => observable.PropertyChanged -= handler);
                }
            }
        }

        public void StopObserving()
        {
            foreach (Action unsubscribe in _unsubscribe)
                unsubscribe();
            _unsubscribe.Clear();
        }
    }

    private sealed class PropertyProvider : IUIVirtualTableSource<PropertyInfo>, IVirtualListItemIndex
    {
        private readonly List<PropertyInfo> _properties = [];
        private readonly Dictionary<PropertyInfo, ulong> _ids = [];
        private readonly Dictionary<ulong, int> _indices = [];
        private object?[] _targetIdentities = [];
        private ulong _nextId;
        public object?[]? Targets { get; private set; }
        public int Count => _properties.Count;
        public ulong Revision { get; private set; } = 1;
        public ulong GetItemId(int index) => _ids[_properties[index]];
        public PropertyInfo GetItem(int index) => _properties[index];
        public bool TryGetIndexOfItemId(ulong id, out int index) => _indices.TryGetValue(id, out index);

        public void Reset(List<PropertyInfo> properties, object?[]? targets)
        {
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
    private readonly Dictionary<PropertyInfo, AssetDropRequest> _pendingAssetDrops = [];
    private UIBoundableTransform? _detailTransform;
    private SceneNode? _detailEditor;
    private PropertyInfo? _detailProperty;
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
    private object?[]? _pendingFocusTargets;
    private readonly Stack<SceneNode> _pendingFocusNodes = new();
    private int _pendingFocusFrames;

    private void CaptureViewState()
    {
        _savedViewTargets = InspectedObjects?.ToArray();
        _savedSceneSelection = (SceneNode[])Selection.SceneNodes.Clone();
        _savedAssetSelection = NativeAssetSelection.SelectedTarget;
        _savedInspectorView = _inspectorView;
        _savedDetailProperty = _detailProperty;
        _savedPropertyScroll = _propertyScroll?.ScrollOffset ?? Vector2.Zero;
        _savedDetailScroll = (_detailTransform as UIScrollableTransform)?.ScrollOffset ?? Vector2.Zero;
        _savedComponentScroll = _componentListView?.ScrollOffset ?? Vector2.Zero;
        _savedPickerScroll = _componentPickerView?.ScrollOffset ?? Vector2.Zero;
        _savedDetailFocusBinding = _detailEditor is { } detail &&
            _detailTransform?.GetCanvasComponent()?.GetInputComponent()?.FocusedComponent is { } focused &&
            focused.SceneNode is { } focusedNode && IsDetailDescendant(focusedNode, detail) &&
            focused is UITextInputComponent { Binding: EditorTextPropertyBinding binding }
                ? binding.Property : null;
    }

    private void RestoreViewState()
    {
        object?[]? saved = _savedViewTargets;
        _savedViewTargets = null;
        if (saved is null || InspectedObjects is not { } current || saved.Length != current.Length ||
            saved.Where((target, index) => !ReferenceEquals(target, current[index])).Any())
            return;
        _inspectorView = _savedInspectorView;
        _propertyScroll!.ScrollOffset = _savedPropertyScroll;
        if (_componentListView is not null)
            _componentListView.ScrollOffset = _savedComponentScroll;
        if (_componentPickerView is not null)
            _componentPickerView.ScrollOffset = _savedPickerScroll;
        if (_savedDetailProperty is { } detail && Properties?.Contains(detail) == true)
        {
            ShowDetailEditor(detail);
            if (_detailTransform is UIScrollableTransform scroll)
                scroll.ScrollOffset = _savedDetailScroll;
            if (_savedDetailFocusBinding is not null)
            {
                if (_pendingFocusFrames > 0)
                    UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RestoreDetailFocusTick);
                _pendingFocusTargets = current.ToArray();
                _pendingFocusFrames = 30;
                RegisterTick(ETickGroup.Late, ETickOrder.Scene, RestoreDetailFocusTick);
            }
        }
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
        var input = _detailTransform?.GetCanvasComponent()?.GetInputComponent();
        if (input is null)
            return;
        UITextInputComponent? match = null;
        _pendingFocusNodes.Push(_detailEditor);
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
        if (input.FocusedComponent is { IsActiveInHierarchy: true } focused &&
            !ReferenceEquals(focused, match))
        {
            StopDetailFocusRestore();
            return;
        }
        if (match is not null && input.TryFocusPresented(match))
            StopDetailFocusRestore();
    }

    private void StopDetailFocusRestore(bool clearSavedBinding = true)
    {
        _pendingFocusFrames = 0;
        _pendingFocusTargets = null;
        _pendingFocusNodes.Clear();
        if (clearSavedBinding)
            _savedDetailFocusBinding = null;
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
        SceneNode root = SceneNode.NewChildWithTransform(out UIDualSplitTransform split, "InspectorContent");
        split.VerticalSplit = true;
        split.FirstFixedSize = true;
        split.FixedSize = 30;
        split.SplitterSize = 0;
        SceneNode toolbar = root.NewChildWithTransform(out UIListTransform commands, "InspectorCommands");
        commands.DisplayHorizontal = true;
        commands.ItemAlignment = EListAlignment.TopOrLeft;
        commands.ItemSpacing = 2;
        CreateComponentCommands(toolbar);
        SceneNode body = root.NewChildWithTransform(out UIDualSplitTransform componentSplit, "InspectorBody");
        componentSplit.VerticalSplit = false;
        componentSplit.FirstFixedSize = true;
        componentSplit.FixedSize = 0;
        componentSplit.SplitterSize = 0;
        _componentSplit = componentSplit;
        SceneNode componentPane = body.NewChildWithTransform<UIBoundableTransform>(out _, "ComponentListPane");
        SceneNode propertyPane = body.NewChildWithTransform<UIBoundableTransform>(out _, "PropertyPane");
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
            property => ResolveName(property) ?? property.Name,
            160, 400,
            CreatePropertyValueCell,
            (cell, property, _) => BindPropertyRow(cell, property, _virtualPropertyProvider.Targets),
            UnbindPropertyRow);
        list.VirtualProvider = _virtualPropertyGrid;
        _virtualPropertyList = list;

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
        backLabel.VerticalAlignment = EVerticalAlignment.Center;
        backButton.InteractAction += _ => HideDetailEditor();
        _detailTransform = detail;
        CreateComponentViews(componentPane, propertyPane);
        RefreshVirtualProperties();
    }

    private void RefreshVirtualProperties()
    {
        List<PropertyInfo> properties = GetMatchingProperties(InspectedObjects);
        Properties = properties;
        _virtualPropertyProvider!.Reset(properties, InspectedObjects);
        _virtualPropertyList!.InvalidateMeasure();
        RefreshComponentViews();
        if (_detailProperty is not null && !properties.Contains(_detailProperty))
            HideDetailEditor();
        else if (_detailProperty is not null)
            ShowDetailEditor(_detailProperty);
        ApplyInspectorView();
    }

    private UIBoundableTransform CreatePropertyValueCell(SceneNode tableRow)
    {
        SceneNode valueRoot = new(tableRow) { Name = "InspectorValue" };
        UIBoundableTransform valueTransform = valueRoot.SetTransform<UIBoundableTransform>();
        valueTransform.Margins = new Vector4(2);

        SceneNode buttonNode = valueRoot.NewChild<UIButtonComponent>(out var detailButton);
        UIBoundableTransform buttonTransform = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
        buttonTransform.MaxAnchor = Vector2.One;
        buttonNode.NewChild<UITextComponent>(out var buttonLabel);
        buttonLabel.Text = "Open details";
        buttonLabel.VerticalAlignment = EVerticalAlignment.Center;
        buttonLabel.HorizontalAlignment = EHorizontalAlignment.Center;
        PropertyRow state = new()
        {
            ValueRoot = valueRoot,
            DetailButton = detailButton, DetailButtonTransform = buttonTransform,
            DetailLabel = buttonLabel
        };
        detailButton.InteractAction += _ =>
        {
            if (state.Property is not null)
                ShowDetailEditor(state.Property);
        };
        _propertyRows.Add(valueTransform, state);
        return valueTransform;
    }

    private void BindPropertyRow(UIBoundableTransform row, PropertyInfo property, object?[]? targets)
    {
        if (_destroyingOwner)
            return;
        PropertyRow state = _propertyRows[row];
        state.DropTarget?.Dispose();
        state.DropTarget = null;
        state.Property = property;
        if (typeof(XRComponent).IsAssignableFrom(property.PropertyType) && targets is { Length: > 0 } &&
            property.SetMethod?.IsPublic == true)
        {
            object?[] boundTargets = targets;
            state.DropTarget = EditorDragDropUtility.RegisterDropTarget(row,
                payload =>
                {
                    if (!ReferenceEquals(state.Property, property) ||
                        !EditorDragDropUtility.TryGetComponent(payload, out XRComponent? component) || component is null ||
                        !property.PropertyType.IsInstanceOfType(component))
                        return false;
                    try
                    {
                        new EditorPropertyAccess(property, boundTargets).SetValue(component);
                        return true;
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception, $"Failed to set component reference '{property.Name}'.");
                        return false;
                    }
                },
                payload => ReferenceEquals(state.Property, property) &&
                    EditorDragDropUtility.TryGetComponent(payload, out XRComponent? component) &&
                    component is not null && property.PropertyType.IsInstanceOfType(component));
        }
        else if (typeof(XRAsset).IsAssignableFrom(property.PropertyType) && targets is { Length: > 0 } &&
                 property.SetMethod?.IsPublic == true)
        {
            object?[] boundTargets = targets;
            state.DropTarget = EditorDragDropUtility.RegisterDropTarget(row,
                payload =>
                {
                    if (!ReferenceEquals(state.Property, property) ||
                        !TryResolveAssetReference(payload, property, out string path, out Type type))
                        return false;
                    return StartAssetReferenceDrop(path, type, property, boundTargets);
                },
                payload => ReferenceEquals(state.Property, property) &&
                    TryResolveAssetReference(payload, property, out _, out _));
        }
        TypeCode typeCode = Type.GetTypeCode(property.PropertyType);
        bool isString = property.PropertyType == typeof(string);
        bool primitive = !isString && typeCode is not (TypeCode.Object or TypeCode.DateTime or TypeCode.Empty or TypeCode.DBNull);
        state.DetailButtonTransform.Visibility = primitive ? EVisibility.Collapsed : EVisibility.Visible;
        state.DetailLabel.Text = isString ? GetStringPreview(property, targets) : "Open details";
        foreach (SceneNode editor in state.PrimitiveEditors.Values)
            editor.GetTransformAs<UIBoundableTransform>()!.Visibility = EVisibility.Collapsed;
        if (!primitive)
            return;
        if (!state.PrimitiveEditors.TryGetValue(property.PropertyType, out SceneNode? editorNode))
        {
            editorNode = state.ValueRoot.NewChild();
            editorNode.SetTransform<UIBoundableTransform>().MaxAnchor = Vector2.One;
            InspectorPropertyEditors.CreateNew(property.PropertyType)?.Invoke(editorNode, property, targets);
            state.PrimitiveEditors.Add(property.PropertyType, editorNode);
        }
        NativeUIElements.RebindPropertyEditor(editorNode, property, targets);
        editorNode.GetTransformAs<UIBoundableTransform>()!.Visibility = EVisibility.Visible;
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

    private bool StartAssetReferenceDrop(string path, Type type, PropertyInfo property, object?[] targets)
    {
        AssetDropRequest? request = null;
        try
        {
            EditorPropertyAccess access = new(property, targets);
            object?[] before = targets.Select(target => target is null ? null : property.GetValue(target)).ToArray();
            request = new AssetDropRequest(property, targets, access, before,
                _virtualPropertyProvider!.Revision, _componentGeneration);
            if (_pendingAssetDrops.TryGetValue(property, out AssetDropRequest? previous))
            {
                previous.Invalidate();
                previous.StopObserving();
            }
            _pendingAssetDrops[property] = request;
            request.ObserveSynchronousChanges();
            _ = AssignAssetReferenceAsync(path, type, request);
            return true;
        }
        catch (Exception exception)
        {
            request?.StopObserving();
            if (request is not null && _pendingAssetDrops.TryGetValue(property, out var pending) &&
                ReferenceEquals(pending, request))
                _pendingAssetDrops.Remove(property);
            Debug.LogException(exception, $"Could not prepare dropped asset for '{property.Name}'.");
            return false;
        }
    }

    private async Task AssignAssetReferenceAsync(string path, Type type, AssetDropRequest request)
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
        EditorImGuiUI.EnqueueSceneEdit(() => FinishAssetReferenceDrop(request, asset));
    }

    private void FinishAssetReferenceDrop(AssetDropRequest request, XRAsset? asset)
    {
        request.StopObserving();
        if (!_pendingAssetDrops.TryGetValue(request.Property, out AssetDropRequest? latest) ||
            !ReferenceEquals(latest, request))
            return;
        _pendingAssetDrops.Remove(request.Property);
        object?[]? current = _virtualPropertyProvider?.Targets;
        if (asset is null || !request.Property.PropertyType.IsInstanceOfType(asset) ||
            request.IsInvalidated || request.Generation != _componentGeneration ||
            SceneNode.IsDestroyed || _virtualPropertyProvider?.Revision != request.ProviderRevision ||
            current is null || current.Length != request.Targets.Length ||
            request.Targets.Where((target, index) => !ReferenceEquals(target, current[index])).Any() ||
            Properties?.Contains(request.Property) != true)
            return;
        for (int index = 0; index < request.Targets.Length; index++)
        {
            object? target = request.Targets[index];
            if (target is null || target is XRObjectBase { IsDestroyed: true } ||
                target is XRWorldObjectBase worldObject &&
                !ReferenceEquals(worldObject.World, request.Worlds[index]) ||
                target is XRComponent component &&
                (!ReferenceEquals(component.SceneNode, request.Owners[index]) ||
                 component.SceneNode.IsDestroyed || !component.SceneNode.Components.Contains(component)))
                return;
            try
            {
                if (!Equals(request.Property.GetValue(target), request.Before[index]))
                    return;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, $"Could not recheck '{request.Property.Name}' before asset assignment.");
                return;
            }
        }
        try
        {
            request.Access.SetValue(asset);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, $"Failed to set dropped asset on '{request.Property.Name}'.");
        }
    }

    private void UnbindPropertyRow(UIBoundableTransform row)
    {
        if (!_propertyRows.TryGetValue(row, out PropertyRow? state))
            return;
        state.DropTarget?.Dispose();
        state.DropTarget = null;
        state.Property = null;
        foreach (SceneNode editor in state.PrimitiveEditors.Values)
        {
            NativeUIElements.RebindPropertyEditor(editor, null, null);
            editor.GetTransformAs<UIBoundableTransform>()!.Visibility = EVisibility.Collapsed;
        }
    }

    private void ShowDetailEditor(PropertyInfo property)
    {
        HideDetailEditor();
        _detailProperty = property;
        SceneNode host = _detailTransform!.SceneNode!.NewChild();
        if (property.PropertyType == typeof(string))
        {
            CreateMultilineStringDetail(host, property);
            _detailEditor = host;
            _detailTransform.Visibility = EVisibility.Visible;
            _propertyScroll!.Visibility = EVisibility.Collapsed;
            return;
        }
        UIListTransform hostLayout = host.SetTransform<UIListTransform>();
        hostLayout.MinAnchor = new Vector2(0, 1);
        hostLayout.MaxAnchor = new Vector2(1, 1);
        hostLayout.NormalizedPivot = new Vector2(0, 1);
        hostLayout.Translation = new Vector2(0, -34);
        hostLayout.ItemAlignment = EListAlignment.TopOrLeft;
        hostLayout.DisplayHorizontal = false;
        InspectorPropertyEditors.CreateNew(property.PropertyType)?.Invoke(host, property, InspectedObjects);
        _detailEditor = host;
        _detailTransform.Visibility = EVisibility.Visible;
        _propertyScroll!.Visibility = EVisibility.Collapsed;
    }

    private void CreateMultilineStringDetail(SceneNode host, PropertyInfo property)
    {
        UIBoundableTransform bounds = host.SetTransform<UIBoundableTransform>();
        bounds.MinAnchor = new Vector2(0, 1);
        bounds.MaxAnchor = Vector2.One;
        bounds.NormalizedPivot = new Vector2(0, 1);
        bounds.Translation = new Vector2(0, -34);
        var (input, text, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(host, property, InspectedObjects);
        input.SingleLineMode = false;
        input.CommitOnSubmit = true;
        text.VerticalAlignment = EVerticalAlignment.Top;
        text.WrapMode = FontGlyphSet.EWrapMode.Word;
        text.BoundableTransform.MinAnchor = Vector2.Zero;
        text.BoundableTransform.MaxAnchor = Vector2.One;
        bounds.Height = EstimateStringDetailHeight(input.Text);
        input.PropertyChanged += TextChanged;

        void TextChanged(object? _, IXRPropertyChangedEventArgs change)
        {
            if (change.PropertyName == nameof(UITextInputComponent.Text))
                bounds.Height = EstimateStringDetailHeight(input.Text);
        }
    }

    private static float EstimateStringDetailHeight(string? value)
    {
        int lines = 1;
        int width = 0;
        foreach (char character in value ?? string.Empty)
        {
            if (character == '\n')
            {
                lines += Math.Max(1, (width + 63) / 64);
                width = 0;
            }
            else if (character != '\r')
                width++;
        }
        lines += Math.Max(1, (width + 63) / 64);
        return Math.Clamp(lines * 22.0f + 12, 180, 20000);
    }

    private static string GetStringPreview(PropertyInfo property, object?[]? targets)
    {
        if (targets?.FirstOrDefault(target => target is not null) is not { } target)
            return "Open text details";
        try
        {
            string value = property.GetValue(target) as string ?? string.Empty;
            int breakIndex = value.IndexOfAny(['\r', '\n']);
            if (breakIndex >= 0)
                value = value[..breakIndex];
            return value.Length > 56 ? value[..56] + "…" : value.Length == 0 ? "Open text details" : value;
        }
        catch (Exception)
        {
            return "Open text details";
        }
    }

    private void HideDetailEditor()
    {
        if (_detailEditor is not null)
        {
            if (_detailTransform?.GetCanvasComponent()?.GetInputComponent() is { FocusedComponent: { } focused } input &&
                focused.SceneNode is { } focusedNode && IsDetailDescendant(focusedNode, _detailEditor))
                input.FocusedComponent = null;
            _detailEditor.IsActiveSelf = false;
            _detailEditor.Destroy(true);
            _detailEditor = null;
        }
        _detailProperty = null;
        if (_detailTransform is not null)
            _detailTransform.Visibility = EVisibility.Collapsed;
        if (_propertyScroll is not null)
            _propertyScroll.Visibility = EVisibility.Visible;
    }

    private static bool IsDetailDescendant(SceneNode node, SceneNode root)
    {
        for (SceneNode? current = node; current is not null;
             current = current.Transform.Parent?.SceneNode)
            if (ReferenceEquals(current, root))
                return true;
        return false;
    }

    private void ReleaseVirtualPropertyList()
    {
        ReleaseVirtualPropertyOwners();
        HideDetailEditor();
        if (_virtualPropertyList is not null)
            _virtualPropertyList.VirtualProvider = null;
        _virtualPropertyProvider = null;
        _virtualPropertyGrid = null;
        _virtualPropertyList = null;
        _propertyScroll = null;
        _detailTransform = null;
        ReleaseComponentViews();
    }

    private void ReleaseVirtualPropertyOwners(bool terminal = false)
    {
        if (terminal)
        {
            _virtualPropertyProvider?.Reset([], null);
            _detailProperty = null;
            _detailEditor = null;
        }
        AssetDropRequest[] pending = [.. _pendingAssetDrops.Values];
        _pendingAssetDrops.Clear();
        foreach (AssetDropRequest request in pending)
        {
            request.Invalidate();
            request.StopObserving();
        }
        PropertyRow[] rows = [.. _propertyRows.Values];
        _propertyRows.Clear();
        foreach (PropertyRow row in rows)
        {
            IDisposable? dropTarget = row.DropTarget;
            row.DropTarget = null;
            dropTarget?.Dispose();
        }
    }
}

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
        split.FixedSize = 30;
        split.SplitterSize = 0;
        SceneNode toolbar = root.NewChildWithTransform(out UIListTransform commands, "InspectorCommands");
        commands.DisplayHorizontal = true;
        commands.ItemAlignment = EListAlignment.TopOrLeft;
        commands.ItemSpacing = 2;
        CreateComponentCommands(toolbar);
        SceneNode propertyPane = root.NewChildWithTransform<UIBoundableTransform>(out _, "PropertyPane");
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

    private void RefreshVirtualProperties()
    {
        List<PropertyInfo> properties = GetMatchingProperties(InspectedObjects);
        Properties = properties;
        _virtualPropertyProvider!.Reset(properties, InspectedObjects);
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
        buttonLabel.FontSize = EditorUI.Styles.PropertyInputFontSize;
        buttonLabel.ClipToBounds = true;
        buttonLabel.VerticalAlignment = EVerticalAlignment.Center;
        buttonLabel.HorizontalAlignment = EHorizontalAlignment.Center;
        PropertyRow state = new()
        {
            ValueRoot = valueRoot,
            Columns = CapturePropertyColumns(tableRow, valueTransform),
            DetailButton = detailButton, DetailButtonTransform = buttonTransform,
            DetailLabel = buttonLabel
        };
        detailButton.InteractAction += _ =>
        {
            PropertyInfo? clickedProperty = state.Property;
            if (clickedProperty is not null)
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
            _componentGeneration == generation && SameTargets(provider.Targets, capturedTargets) &&
            SameTargets(InspectedObjects, capturedTargets);
        state.Columns.Bind(Current);
        if (_propertyColumnScroll is not null && IsActiveInHierarchy && !ApplyPropertyColumnLayout(state.Columns))
        {
            if (Current()) throw new InvalidOperationException("The inspector column layout changed during row binding.");
            return;
        }
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
        TypeCode typeCode = Type.GetTypeCode(property.PropertyType);
        bool isString = property.PropertyType == typeof(string);
        bool primitive = !isString && typeCode is not (TypeCode.Object or TypeCode.DateTime or TypeCode.Empty or TypeCode.DBNull);
        state.DetailButtonTransform.Visibility = primitive ? EVisibility.Collapsed : EVisibility.Visible;
        if (!Current()) return;
        state.DetailLabel.Text = "Open details";
        if (!Current()) return;
        if (isString && targets is { Length: > 0 })
        {
            InspectorStringPreviewBinding? preview = null;
            preview = new(property, targets, value =>
            {
                if (preview is not null && Current() && ReferenceEquals(state.StringPreview, preview))
                    state.DetailLabel.Text = value;
            });
            if (!Current()) { preview.Dispose(); return; }
            state.StringPreview = preview;
            preview.Refresh();
            if (!Current() || !ReferenceEquals(state.StringPreview, preview))
                return;
        }
        foreach (SceneNode editor in state.PrimitiveEditors.Values)
        {
            editor.GetTransformAs<UIBoundableTransform>()!.Visibility = EVisibility.Collapsed;
            if (!Current()) return;
        }
        if (!primitive)
            return;
        if (!state.PrimitiveEditors.TryGetValue(property.PropertyType, out SceneNode? editorNode))
        {
            editorNode = state.ValueRoot.NewChild();
            if (!Current()) return;
            editorNode.SetTransform<UIBoundableTransform>().MaxAnchor = Vector2.One;
            if (!Current()) return;
            InspectorPropertyEditors.CreateNew(property.PropertyType)?.Invoke(editorNode, property, targets);
            if (!Current()) return;
            state.PrimitiveEditors.Add(property.PropertyType, editorNode);
        }
        NativeUIElements.RebindPropertyEditor(editorNode, property, targets);
        if (!Current()) return;
        editorNode.GetTransformAs<UIBoundableTransform>()!.Visibility = EVisibility.Visible;
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

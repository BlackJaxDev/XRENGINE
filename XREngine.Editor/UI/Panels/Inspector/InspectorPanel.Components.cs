using System.Numerics;
using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class InspectorPanel
{
    private InspectorView _inspectorView;
    private SceneNode[] _componentTargets = [];
    private readonly NativeTableRows<XRComponent> _componentRows = new();
    private readonly NativeTableRows<NativeComponentType> _componentTypes = new();
    private readonly Dictionary<XRComponent, ulong> _componentIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, (SceneNode Node, XRComponent Component)> _componentsById = [];
    private readonly Dictionary<ulong, (SceneNode Node, XRComponent Component)[]> _componentGroupsById = [];
    private readonly Dictionary<UIVirtualTableRowComponent, Vector2> _componentDragStarts = [];
    private readonly Dictionary<Type, ulong> _componentTypeIds = [];
    private readonly Dictionary<ulong, Type> _componentTypesById = [];
    private ulong _nextComponentId;
    private ulong _nextTypeId;
    private string _componentSearch = string.Empty;
    private UIScrollableTransform? _componentListView;
    private UIScrollableTransform? _componentPickerView;
    private UIDualSplitTransform? _componentSplit;
    private UIListTransform? _componentList;
    private UIListTransform? _componentPicker;
    private UIVirtualTableProvider<XRComponent>? _componentTable;
    private UIVirtualTableProvider<NativeComponentType>? _componentTypeTable;
    private ulong _componentGeneration;
    private UITextComponent? _componentStatusLabel;
    private readonly HashSet<SceneNode> _observedComponentNodes = new(ReferenceEqualityComparer.Instance);

    private void CreateComponentCommands(SceneNode toolbar)
    {
        NativeUIElements.CreateButton(toolbar, "Properties", _ => ShowSceneProperties(), width: 88, height: 28);
        NativeUIElements.CreateButton(toolbar, "Components", _ => SetInspectorView(InspectorView.Components), width: 88, height: 28);
        NativeUIElements.CreateButton(toolbar, "Add", _ => SetInspectorView(InspectorView.AddComponent), width: 70, height: 28);
        NativeUIElements.CreateButton(toolbar, "Remove", _ => RemoveSelectedComponent(), width: 75, height: 28);

        SceneNode searchNode = toolbar.NewChild();
        searchNode.SetTransform<UIBoundableTransform>().Width = 140;
        var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(searchNode);
        input.SingleLineMode = true;
        input.Text = _componentSearch;
        input.Submitted += field =>
        {
            _componentSearch = field.Text.Trim();
            RefreshComponentTypes();
            SetInspectorView(InspectorView.AddComponent);
        };
        input.Cancelled += field => field.Text = _componentSearch;

        SceneNode statusNode = toolbar.NewChild<UITextComponent>(out var status);
        statusNode.GetTransformAs<UIBoundableTransform>(true)!.Width = 300;
        status.VerticalAlignment = EVerticalAlignment.Center;
        _componentStatusLabel = status;
    }

    private void CreateComponentViews(SceneNode componentPane, SceneNode propertyPane)
    {
        (_componentListView, _componentList) = CreateModeList(componentPane, "Components");
        _componentTable = new UIVirtualTableProvider<XRComponent>(_componentRows,
            UIVirtualTableColumn<XRComponent>.Text("component", "Component", 165,
                component => component.Name ?? component.GetType().Name));
        _componentTable.AccessibleRowName = component => component.Name ?? component.GetType().Name;
        _componentTable.Activated += InspectComponent;
        _componentTable.RowPointerMoved += DragComponentPointerMoved;
        _componentTable.RowPointerLeft += source => _componentDragStarts.Remove(source);
        _componentList.VirtualProvider = _componentTable;

        (_componentPickerView, _componentPicker) = CreateModeList(propertyPane, "AddComponent");
        _componentTypeTable = new UIVirtualTableProvider<NativeComponentType>(_componentTypes,
            UIVirtualTableColumn<NativeComponentType>.Text("name", "Component", 230, item => item.Name),
            UIVirtualTableColumn<NativeComponentType>.Text("namespace", "Namespace", 300, item => item.Namespace));
        _componentTypeTable.AccessibleRowName = item => item.Name;
        _componentTypeTable.Activated += AddComponent;
        _componentPicker.VirtualProvider = _componentTypeTable;
        RefreshComponentTypes();
        ApplyInspectorView();
    }

    private static (UIScrollableTransform Scroll, UIListTransform List) CreateModeList(SceneNode body, string name)
    {
        SceneNode scrollNode = body.NewChild();
        scrollNode.Name = name;
        UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
        scroll.MinAnchor = Vector2.Zero;
        scroll.MaxAnchor = Vector2.One;
        scroll.BlocksInputBehind = true;
        scrollNode.AddComponent<UIScrollRegionComponent>();
        SceneNode listNode = scrollNode.NewChild();
        UIListTransform list = listNode.SetTransform<UIListTransform>();
        list.MinAnchor = Vector2.Zero;
        list.MaxAnchor = Vector2.One;
        list.ItemSize = 28;
        list.ItemAlignment = EListAlignment.TopOrLeft;
        list.VirtualOverscan = 2;
        return (scroll, list);
    }

    private void RefreshComponentViews()
    {
        if (InspectedObjects is { Length: > 0 } targets && targets.All(target => target is SceneNode))
            _componentTargets = targets.Cast<SceneNode>().ToArray();
        ObserveComponentNodes(_componentTargets);
        if (_componentList is null)
            return;
        List<(ulong Id, XRComponent Item)> rows = [];
        _componentsById.Clear();
        _componentGroupsById.Clear();
        HashSet<XRComponent> current = new(ReferenceEqualityComparer.Instance);
        if (_componentTargets.Length == 1)
        {
            SceneNode node = _componentTargets[0];
            foreach (XRComponent component in node.Components)
            {
                if (component.IsDestroyed)
                    continue;
                current.Add(component);
                if (!_componentIds.TryGetValue(component, out ulong id))
                    _componentIds.Add(component, id = ++_nextComponentId);
                _componentsById[id] = (node, component);
                _componentGroupsById[id] = [(node, component)];
                rows.Add((id, component));
            }
        }
        else if (_componentTargets.Length > 1)
        {
            Dictionary<Type, List<(SceneNode Node, XRComponent Component)>> byType = [];
            HashSet<Type> duplicates = [];
            foreach (SceneNode node in _componentTargets)
            {
                HashSet<Type> seenOnNode = [];
                foreach (XRComponent component in node.Components)
                {
                    if (component.IsDestroyed)
                        continue;
                    Type type = component.GetType();
                    if (!seenOnNode.Add(type))
                        duplicates.Add(type);
                    if (!byType.TryGetValue(type, out var group))
                        byType[type] = group = [];
                    group.Add((node, component));
                }
            }
            foreach (var (type, group) in byType.OrderBy(pair => pair.Key.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (duplicates.Contains(type) || group.Count != _componentTargets.Length)
                    continue;
                XRComponent representative = group[0].Component;
                current.Add(representative);
                if (!_componentIds.TryGetValue(representative, out ulong id))
                    _componentIds.Add(representative, id = ++_nextComponentId);
                _componentsById[id] = group[0];
                _componentGroupsById[id] = [.. group];
                rows.Add((id, representative));
            }
        }
        foreach (XRComponent stale in _componentIds.Keys.Where(component => !current.Contains(component)).ToArray())
            _componentIds.Remove(stale);
        _componentRows.Replace(rows);
        _componentList.InvalidateMeasure();
        if (_componentTable?.SelectedId is ulong selected && !_componentsById.ContainsKey(selected))
            _componentTable.SelectedId = null;
    }

    private void ObserveComponentNodes(IEnumerable<SceneNode> nodes)
    {
        HashSet<SceneNode> current = new(nodes, ReferenceEqualityComparer.Instance);
        foreach (SceneNode stale in _observedComponentNodes.Where(node => !current.Contains(node)).ToArray())
        {
            stale.ComponentAdded -= ComponentListChanged;
            stale.ComponentRemoved -= ComponentListChanged;
            _observedComponentNodes.Remove(stale);
        }
        foreach (SceneNode node in current)
        {
            if (!_observedComponentNodes.Add(node))
                continue;
            node.ComponentAdded += ComponentListChanged;
            node.ComponentRemoved += ComponentListChanged;
        }
    }

    private void ComponentListChanged((SceneNode node, XRComponent comp) _)
        => RefreshComponentViews();

    private void RefreshComponentTypes()
    {
        if (_componentPicker is null)
            return;
        NativeComponentType[] types = EditorImGuiUI.GetNativeComponentTypes(_componentSearch);
        List<(ulong Id, NativeComponentType Item)> rows = new(types.Length);
        _componentTypesById.Clear();
        foreach (var type in types)
        {
            if (!_componentTypeIds.TryGetValue(type.Type, out ulong id))
                _componentTypeIds.Add(type.Type, id = ++_nextTypeId);
            _componentTypesById[id] = type.Type;
            rows.Add((id, type));
        }
        _componentTypes.Replace(rows);
        _componentPicker.InvalidateMeasure();
    }

    private void InspectComponent(ulong id)
    {
        if (!_componentGroupsById.TryGetValue(id, out var group))
            return;
        _inspectorView = InspectorView.Properties;
        InspectedObjects = group.Select(entry => (object?)entry.Component).ToArray();
    }

    private void DragComponentPointerMoved(ulong id, UIVirtualTableRowComponent source, Vector2 localPoint)
    {
        if (!EditorDragDropUtility.IsLeftMouseButtonHeldFor(source))
        {
            _componentDragStarts.Remove(source);
            return;
        }
        Vector2 point = source.BoundableTransform.LocalToCanvas(localPoint);
        if (!_componentDragStarts.TryGetValue(source, out Vector2 start))
        {
            _componentDragStarts[source] = point;
            return;
        }
        if (Vector2.Distance(point, start) < EditorDragDropUtility.DefaultActivationDistance ||
            !_componentsById.TryGetValue(id, out var entry) || entry.Component.IsDestroyed)
            return;
        if (EditorDragDropUtility.BeginDrag(source,
            EditorDragDropUtility.CreateComponentPayload(entry.Component), point))
            _componentDragStarts[source] = point;
    }

    private void ShowSceneProperties()
    {
        _inspectorView = InspectorView.Properties;
        if (_componentTargets.Length > 0)
            InspectedObjects = _componentTargets.Cast<object?>().ToArray();
        else
            ApplyInspectorView();
    }

    private void SetInspectorView(InspectorView view)
    {
        if (_componentTargets.Length == 0 && view != InspectorView.Properties)
            return;
        HideDetailEditor();
        _inspectorView = view;
        if (view == InspectorView.Components)
            RefreshComponentViews();
        else if (view == InspectorView.AddComponent)
            RefreshComponentTypes();
        ApplyInspectorView();
    }

    private void ApplyInspectorView()
    {
        if (_propertyScroll is null)
            return;
        if (_componentSplit is not null)
            _componentSplit.FixedSize = _componentTargets.Length > 0 ? 170 : 0;
        _propertyScroll.Visibility = _inspectorView == InspectorView.Properties && _detailProperty is null
            ? EVisibility.Visible : EVisibility.Collapsed;
        if (_detailTransform is not null)
            _detailTransform.Visibility = _inspectorView == InspectorView.Properties && _detailProperty is not null
                ? EVisibility.Visible : EVisibility.Collapsed;
        if (_componentListView is not null)
            _componentListView.Visibility = _componentTargets.Length > 0
                ? EVisibility.Visible : EVisibility.Collapsed;
        if (_componentPickerView is not null)
            _componentPickerView.Visibility = _inspectorView == InspectorView.AddComponent
                ? EVisibility.Visible : EVisibility.Collapsed;
    }

    private void AddComponent(ulong id)
    {
        if (!_componentTypesById.TryGetValue(id, out Type? type) || _componentTargets.Length == 0)
            return;
        SceneNode[] targets = [.. _componentTargets];
        ulong generation = _componentGeneration;
        EditorImGuiUI.QueueNativeComponentAdd(targets, type, error =>
        {
            if (generation != _componentGeneration || SceneNode.IsDestroyed || _componentList is null ||
                !targets.SequenceEqual(_componentTargets, ReferenceEqualityComparer.Instance))
                return;
            if (_componentStatusLabel is not null)
                _componentStatusLabel.Text = error ?? $"Added {type.Name}";
            SetInspectorView(InspectorView.Components);
        });
    }

    private void RemoveSelectedComponent()
    {
        if (_componentTable?.SelectedId is not ulong id || !_componentGroupsById.TryGetValue(id, out var group))
            return;
        ulong generation = _componentGeneration;
        EditorImGuiUI.QueueNativeComponentRemoveMany(group, () =>
        {
            if (generation != _componentGeneration || SceneNode.IsDestroyed || _componentList is null ||
                group.Any(entry => !_componentTargets.Contains(entry.Node)))
                return;
            if (_componentStatusLabel is not null)
                _componentStatusLabel.Text = $"Removed {group[0].Component.GetType().Name}";
            ShowSceneProperties();
            SetInspectorView(InspectorView.Components);
        });
    }

    private void ReleaseComponentViews()
    {
        _componentGeneration++;
        ObserveComponentNodes([]);
        if (_componentList is not null)
            _componentList.VirtualProvider = null;
        if (_componentPicker is not null)
            _componentPicker.VirtualProvider = null;
        _componentListView = null;
        _componentPickerView = null;
        _componentSplit = null;
        _componentList = null;
        _componentPicker = null;
        _componentTable = null;
        _componentTypeTable = null;
        _componentStatusLabel = null;
        _componentsById.Clear();
        _componentGroupsById.Clear();
        _componentDragStarts.Clear();
        _componentTypesById.Clear();
    }
}

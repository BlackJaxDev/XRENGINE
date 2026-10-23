using System.Numerics;
using System.Reflection;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
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
        public readonly Dictionary<Type, SceneNode> PrimitiveEditors = [];
        public PropertyInfo? Property;
    }

    private sealed class PropertyProvider : IUIVirtualTableSource<PropertyInfo>
    {
        private readonly List<PropertyInfo> _properties = [];
        private readonly Dictionary<PropertyInfo, ulong> _ids = [];
        private object?[] _targetIdentities = [];
        private ulong _nextId;
        public object?[]? Targets { get; private set; }
        public int Count => _properties.Count;
        public ulong Revision { get; private set; } = 1;
        public ulong GetItemId(int index) => _ids[_properties[index]];
        public PropertyInfo GetItem(int index) => _properties[index];

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
            Revision++;
        }
    }

    private PropertyProvider? _virtualPropertyProvider;
    private UIVirtualPropertyGridProvider<PropertyInfo>? _virtualPropertyGrid;
    private UIListTransform? _virtualPropertyList;
    private UIScrollableTransform? _propertyScroll;
    private readonly Dictionary<UIBoundableTransform, PropertyRow> _propertyRows = [];
    private UIBoundableTransform? _detailTransform;
    private SceneNode? _detailEditor;
    private PropertyInfo? _detailProperty;

    private void CreateVirtualPropertyList()
    {
        SceneNode scrollNode = SceneNode.NewChild();
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

        SceneNode detailNode = SceneNode.NewChild();
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
        RefreshVirtualProperties();
    }

    private void RefreshVirtualProperties()
    {
        List<PropertyInfo> properties = GetMatchingProperties(InspectedObjects);
        Properties = properties;
        _virtualPropertyProvider!.Reset(properties, InspectedObjects);
        _virtualPropertyList!.InvalidateMeasure();
        if (_detailProperty is not null && !properties.Contains(_detailProperty))
            HideDetailEditor();
        else if (_detailProperty is not null)
            ShowDetailEditor(_detailProperty);
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
            DetailButton = detailButton, DetailButtonTransform = buttonTransform
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
        PropertyRow state = _propertyRows[row];
        state.Property = property;
        TypeCode typeCode = Type.GetTypeCode(property.PropertyType);
        bool primitive = typeCode is not (TypeCode.Object or TypeCode.DateTime or TypeCode.Empty or TypeCode.DBNull);
        state.DetailButtonTransform.Visibility = primitive ? EVisibility.Collapsed : EVisibility.Visible;
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

    private void UnbindPropertyRow(UIBoundableTransform row)
    {
        PropertyRow state = _propertyRows[row];
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

    private void HideDetailEditor()
    {
        if (_detailEditor is not null)
        {
            _detailEditor.IsActiveSelf = false;
            _detailEditor.Transform.Parent = null;
            _detailEditor = null;
        }
        _detailProperty = null;
        if (_detailTransform is not null)
            _detailTransform.Visibility = EVisibility.Collapsed;
        if (_propertyScroll is not null)
            _propertyScroll.Visibility = EVisibility.Visible;
    }

    private void ReleaseVirtualPropertyList()
    {
        HideDetailEditor();
        if (_virtualPropertyList is not null)
            _virtualPropertyList.VirtualProvider = null;
        _propertyRows.Clear();
        _virtualPropertyProvider = null;
        _virtualPropertyGrid = null;
        _virtualPropertyList = null;
        _propertyScroll = null;
        _detailTransform = null;
    }
}

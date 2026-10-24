using System.ComponentModel;
using System.Numerics;
using System.Reflection;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class InspectorPanel : EditorPanel
{
    private object?[]? _inspectedObjects;
    public object?[]? InspectedObjects
    {
        get => _inspectedObjects;
        set => SetField(ref _inspectedObjects, value);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(InspectedObjects):
                if (prev is object?[] previous &&
                    (field is not object?[] next || previous.Length != next.Length ||
                     previous.Where((item, index) => !ReferenceEquals(item, next[index])).Any()))
                    HideDetailEditor();
                RemakeChildren();
                break;
        }
    }

    private bool _isLocked = false;
    private bool _inspectingAsset;
    public bool IsLocked
    {
        get => _isLocked;
        set => SetField(ref _isLocked, value);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        SubscribePlayModeTransitions();
        bool hadPlaySnapshot = _playInspectedTargets is not null;
        if (hadPlaySnapshot && Engine.PlayMode.State == EPlayModeState.Edit)
            RestorePlayModeState();
        if (!IsActiveInHierarchy || IsDestroyed || IsDestroyQueued)
            return;
        if (_virtualPropertyList is null)
            RemakeChildren();
        if (!IsActiveInHierarchy || IsDestroyed || IsDestroyQueued)
            return;
        Selection.SelectionChanged += OnSelectionChanged;
        NativeAssetSelection.SelectionChanged += OnNativeAssetSelectionChanged;
        if (hadPlaySnapshot)
            return;
        if (!IsLocked && DidGlobalSelectionChangeWhileHidden())
        {
            if (NativeAssetSelection.SelectedTarget is { } assetTarget)
            {
                if (!_inspectingAsset || InspectedObjects is not { Length: 1 } selected ||
                    !ReferenceEquals(selected[0], assetTarget))
                    OnNativeAssetSelectionChanged(assetTarget);
            }
            else
            {
                SceneNode[] selected = Selection.SceneNodes;
                if (_inspectingAsset || InspectedObjects is not { } current || current.Length != selected.Length ||
                    selected.Where((node, index) => !ReferenceEquals(node, current[index])).Any())
                    OnSelectionChanged(selected);
            }
        }
        RestoreViewState();
    }

    protected override void OnComponentDeactivated()
    {
        if (_playInspectedTargets is null)
            CaptureViewState();
        StopDetailFocusRestore(clearSavedBinding: false);
        base.OnComponentDeactivated();
        Selection.SelectionChanged -= OnSelectionChanged;
        NativeAssetSelection.SelectionChanged -= OnNativeAssetSelectionChanged;
        ReleaseVirtualPropertyList();
    }

    protected override void OnDestroying()
    {
        UnsubscribePlayModeTransitions();
        Selection.SelectionChanged -= OnSelectionChanged;
        NativeAssetSelection.SelectionChanged -= OnNativeAssetSelectionChanged;
        base.OnDestroying();
    }

    private void OnSelectionChanged(SceneNode[] obj)
    {
        if (obj.Length == 0 && Engine.PlayMode.State == EPlayModeState.EnteringPlay)
            CapturePlayModeState();
        if (!IsLocked)
        {
            _componentGeneration++;
            _componentTargets = [.. obj];
            _inspectorView = InspectorView.Properties;
            _inspectingAsset = false;
            NativeAssetSelection.Clear();
            InspectedObjects = obj;
        }
    }

    private void OnNativeAssetSelectionChanged(object? target)
    {
        if (IsLocked)
            return;
        if (target is null)
        {
            _componentGeneration++;
            if (_inspectingAsset)
                InspectedObjects = null;
            _inspectingAsset = false;
            return;
        }
        _componentGeneration++;
        _componentTargets = [];
        _inspectorView = InspectorView.Properties;
        _inspectingAsset = true;
        InspectedObjects = [target];
    }

    private void RemakeChildren()
    {
        using var sample = Engine.Profiler.Start("InspectorPanel.RemakeChildren");
        if (_virtualPropertyList is not null)
        {
            RefreshVirtualProperties();
            return;
        }
        foreach (var child in SceneNode.Transform.Children.ToArray())
            child.SceneNode?.Destroy(true);
        CreateVirtualPropertyList();
    }

    /// <summary>Gets property metadata that is valid for every selected object.</summary>
    private static List<PropertyInfo> GetMatchingProperties(object?[]? objects)
    {
        using var sample = Engine.Profiler.Start("InspectorPanel.GetMatchingProperties");
        List<PropertyInfo> matching = [];
        if (objects is null)
            return matching;

        bool first = true;
        foreach (object? obj in objects)
        {
            if (obj is null)
                continue;
            Type type = obj.GetType();
            if (first)
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (property.GetIndexParameters().Length == 0)
                        matching.Add(property);
                first = false;
                continue;
            }
            for (int i = matching.Count - 1; i >= 0; i--)
            {
                PropertyInfo property = matching[i];
                if (property.DeclaringType?.IsAssignableFrom(type) != true)
                    matching.RemoveAt(i);
            }
        }
        return matching;
    }

    public float Margin { get; set; } = 4.0f;

    private void CreatePropertyList(SceneNode parentNode, InspectorPanel inspectorPanel)
    {
        using var sample = Engine.Profiler.Start("InspectorPanel.CreatePropertyList");
        var listNode = parentNode.NewChild<UIMaterialComponent>(out var menuMat);
        ApplyBackground(menuMat);
        var listTfm = listNode.SetTransform<UIListTransform>();
        listTfm.DisplayHorizontal = false;
        listTfm.ItemSpacing = 0.0f;
        listTfm.Padding = new Vector4(0.0f);
        listTfm.ItemAlignment = EListAlignment.TopOrLeft;
        listTfm.ItemSize = null;
        listTfm.Width = 150;
        listTfm.Height = null;
        Properties = CreateNodes(listNode, InspectedObjects);
    }

    public List<PropertyInfo>? Properties { get; private set; }

    private static List<PropertyInfo> CreateNodes(SceneNode listNode, object?[]? inspectedObjects)
    {
        using var sample = Engine.Profiler.Start("InspectorPanel.CreateNodes");
        float fontSize = EditorUI.Styles.PropertyNameFontSize;
        float leftMargin = 5.0f;
        float rightMargin = 5.0f;
        float verticalSpacing = 2.0f;

        List<PropertyInfo> props = GetMatchingProperties(inspectedObjects);
        float textWidth = MeasureTextWidth(fontSize, props);
        foreach (var prop in props)
            CreatePropertyDisplay(listNode, inspectedObjects, fontSize, leftMargin, rightMargin, verticalSpacing, textWidth, prop);
        return props;
    }

    private static float MeasureTextWidth(float fontSize, List<PropertyInfo> matching)
    {
        using var sample = Engine.Profiler.Start("InspectorPanel.MeasureTextWidth");
        float textWidth = 0.0f;
        foreach (var prop in matching)
            textWidth = Math.Max(textWidth, UITextComponent.MeasureWidth(ResolveName(prop) ?? string.Empty, FontGlyphSet.LoadDefaultFont(), fontSize));
        return textWidth;
    }

    private static void CreatePropertyDisplay(
        SceneNode listNode,
        object?[]? inspectedObjects,
        float fontSize,
        float leftMargin,
        float rightMargin,
        float verticalSpacing,
        float textWidth,
        PropertyInfo prop)
    {
        var n = listNode.NewChild();
        var splitter = n.SetTransform<UIDualSplitTransform>();
        var nameNode = n.NewChild<UITextComponent>(out var nameText);
        var valueNode = n.NewChild();

        splitter.FixedSize = textWidth + leftMargin + rightMargin;
        splitter.VerticalSplit = false;
        splitter.FirstFixedSize = true;
        splitter.SplitPercent = 0.5f;

        nameText.Text = ResolveName(prop);
        nameText.FontSize = fontSize;
        nameText.HorizontalAlignment = EHorizontalAlignment.Right;
        nameText.VerticalAlignment = EVerticalAlignment.Center;
        nameText.Color = EditorUI.Styles.PropertyNameTextColor;
        var nameTfm = nameText.BoundableTransform;
        nameTfm.Margins = new Vector4(leftMargin, verticalSpacing, rightMargin, verticalSpacing);

        InspectorPropertyEditors.CreateNew(prop.PropertyType)?.Invoke(valueNode, prop, inspectedObjects);
    }

    private static string? ResolveName(PropertyInfo prop)
    {
        var name = prop.Name;
        var attr = prop.GetCustomAttribute<DisplayNameAttribute>();
        if (attr is not null)
            name = attr.DisplayName;
        return CamelCaseWithSpaces(name);
    }

    private static string? CamelCaseWithSpaces(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // Insert space between lowercase or digit and uppercase letter
        string withSpaces = System.Text.RegularExpressions.Regex.Replace(
            name,
            "(?<=[a-z0-9])([A-Z])",
            " $1");

        // Handle sequences like "XMLHttpRequest" -> "XML Http Request"
        withSpaces = System.Text.RegularExpressions.Regex.Replace(
            withSpaces,
            "([A-Z])([A-Z][a-z])",
            "$1 $2");

        return withSpaces;
    }
}

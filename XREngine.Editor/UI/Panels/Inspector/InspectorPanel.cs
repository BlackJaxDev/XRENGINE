using System.ComponentModel;
using System.Numerics;
using System.Reflection;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public class InspectorPanel : EditorPanel
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
                RemakeChildren();
                break;
        }
    }

    private bool _isLocked = false;
    public bool IsLocked
    {
        get => _isLocked;
        set => SetField(ref _isLocked, value);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RemakeChildren();
        Selection.SelectionChanged += OnSelectionChanged;
    }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        SceneNode.Transform.Clear();
        Selection.SelectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged(SceneNode[] obj)
    {
        if (!IsLocked)
            InspectedObjects = obj;
    }

    private void RemakeChildren()
    {
        using var sample = Engine.Profiler.Start("InspectorPanel.RemakeChildren");
        SceneNode.Transform.Clear();
        CreatePropertyList(SceneNode, this);
    }

    /// <summary>
    /// Get the properties that are common to all the inspected objects.
    /// </summary>
    /// <returns></returns>
    private static List<PropertyInfo> GetMatchingProperties(object?[]? objects)
    {
        using var sample = Engine.Profiler.Start("InspectorPanel.GetMatchingProperties");
        List<PropertyInfo> matching = [];
        if (objects is null)
            return matching;
        
        bool first = true;
        foreach (var obj in objects)
        {
            if (obj is null)
                continue;

            var props = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (prop is null || prop.GetIndexParameters().Length > 0)
                    continue;

                if (first)
                    matching.Add(prop);
                else
                    matching = [.. matching.Intersect([prop])];
            }
            first = false;
        }
        return matching;
    }

    public float Margin { get; set; } = 4.0f;

    private void CreatePropertyList(SceneNode parentNode, InspectorPanel inspectorPanel)
    {
        using var sample = Engine.Profiler.Start("InspectorPanel.CreatePropertyList");
        var listNode = parentNode.NewChild<UIMaterialComponent>(out var menuMat);
        menuMat.Material = MakeBackgroundMaterial();
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

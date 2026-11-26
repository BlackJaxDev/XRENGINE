using System.Numerics;
using System.Reflection;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// The inspector field of a component, scene node, transform, or asset reference. It shows the referenced
/// object's name in a field well. A click opens a picker with "None" and the matching objects, and the chosen
/// object is assigned to all edited objects. The arrow button at the right opens the referenced object in the
/// inspector. A drop on the row also assigns an object; the row registers that drop target.
/// </summary>
internal sealed class InspectorObjectField
{
    private readonly InspectorPanel _owner;
    private readonly UIComboBoxComponent _combo;
    private readonly UITextComponent _label;
    private readonly UIBoundableTransform _root;
    private readonly UIBoundableTransform _openButton;
    private readonly InspectorObjectCandidateSource _source = new();
    private PropertyInfo? _property;
    private object?[]? _targets;
    private object? _value;
    private Func<bool>? _current;

    /// <summary>Width of the open button at the right of the field.</summary>
    private const float OpenButtonWidth = 24.0f;

    public InspectorObjectField(SceneNode valueRoot, InspectorPanel owner)
    {
        _owner = owner;
        SceneNode root = valueRoot.NewChild("ObjectField");
        _root = root.SetTransform<UIBoundableTransform>();
        _root.MaxAnchor = Vector2.One;

        // The picker field: the glass field well, the name text, and a down chevron that shows it opens a list.
        SceneNode field = root.NewChild("Picker");
        UIBoundableTransform fieldBounds = field.SetTransform<UIBoundableTransform>();
        fieldBounds.MaxAnchor = Vector2.One;
        fieldBounds.Margins = new Vector4(0.0f, 0.0f, OpenButtonWidth + 2.0f, 0.0f);
        _combo = field.AddComponent<UIComboBoxComponent>()!;
        NativePropertyPopupHost popup = field.AddComponent<NativePropertyPopupHost>()!;
        _combo.Popup = popup.CreatePopup();
        _combo.Opening += _ => _combo.PopupPosition = popup.GetPosition();
        _combo.SelectionChanged += OnSelectionChanged;
        NativeUIElements.ApplyFieldStyle(field.AddComponent<UIMaterialComponent>()!);
        // The chevron node comes before the label, so the two quads draw in one run before the text.
        NativeUIElements.CreateIcon(field, EUIQuadIcon.ChevronDown, new Vector2(1.0f, 0.5f),
            new Vector2(-12.0f, 0.0f), 10.0f, EditorGlassTheme.TextSecondary);
        field.NewChild<UITextComponent>(out _label);
        _label.BoundableTransform.Margins = new Vector4(8.0f, 0.0f, 24.0f, 0.0f);
        _label.FontSize = EditorUI.Styles.PropertyInputFontSize;
        _label.Color = EditorUI.Styles.PropertyInputTextColor;
        _label.VerticalAlignment = EVerticalAlignment.Center;
        _label.ClipToBounds = true;

        // The open button: a quiet square with a right chevron.
        SceneNode open = root.NewChild<UIButtonComponent, UIMaterialComponent>(out var openButton, out var openBackground);
        openBackground.SetBatchSolidColor(ColorF4.Transparent);
        openButton.Style = EditorGlassTheme.Row;
        _openButton = open.GetTransformAs<UIBoundableTransform>(true)!;
        _openButton.MinAnchor = new Vector2(1.0f, 0.0f);
        _openButton.MaxAnchor = Vector2.One;
        _openButton.NormalizedPivot = new Vector2(1.0f, 0.0f);
        _openButton.Width = OpenButtonWidth;
        _openButton.BlocksInputBehind = true;
        NativeUIElements.CreateIcon(open, EUIQuadIcon.ChevronRight, new Vector2(0.5f, 0.5f), Vector2.Zero, 10.0f,
            EditorGlassTheme.TextSecondary);
        openButton.InteractAction += _ =>
        {
            if (_value is { } value && _current?.Invoke() == true)
                _owner.InspectReferencedObject(value);
        };
        _root.Visibility = EVisibility.Collapsed;
    }

    /// <summary>True while the field edits a property.</summary>
    public bool IsBound => _property is not null;

    /// <summary>
    /// Shows the field for <paramref name="property"/> of <paramref name="targets"/>. <paramref name="text"/> is
    /// the name of the current value, "None", or a dash for different values.
    /// </summary>
    public void Bind(PropertyInfo property, object?[] targets, string text, Func<bool> current)
    {
        _property = property;
        _targets = targets;
        _current = current;
        _value = CommonValue(property, targets);
        bool nullable = !property.PropertyType.IsValueType;
        _source.Reset(property.PropertyType, targets, nullable);
        _combo.VirtualOptions = _source;
        _combo.IsReadOnly = property.SetMethod?.IsPublic != true;
        _combo.AccessibilityLabel = property.Name;
        if (_label.Text != text)
            _label.Text = text;
        EVisibility openVisibility = _value is null ? EVisibility.Hidden : EVisibility.Visible;
        if (_openButton.Visibility != openVisibility)
            _openButton.Visibility = openVisibility;
        if (_root.Visibility != EVisibility.Visible)
            _root.Visibility = EVisibility.Visible;
    }

    /// <summary>Hides the field and releases the edited objects.</summary>
    public void Unbind()
    {
        _property = null;
        _targets = null;
        _current = null;
        _value = null;
        _combo.Close();
        _combo.VirtualOptions = null;
        if (_root.Visibility != EVisibility.Collapsed)
            _root.Visibility = EVisibility.Collapsed;
    }

    private void OnSelectionChanged(UIValueChange<ulong?> change)
    {
        if (change.Origin != EUIValueChangeOrigin.User || change.NewValue is not { } id ||
            _property is not { } property || _targets is not { } targets || _current?.Invoke() != true)
            return;
        if (_source.TryGetCandidate(id, out InspectorObjectCandidateSource.Candidate candidate))
            _owner.AssignObjectCandidate(property, targets, candidate);
        // The picker does not keep a selection, so the same entry can be picked again.
        _combo.Select(null);
        _source.Invalidate();
    }

    /// <summary>Returns the value that all edited objects share, or null when it differs or is missing.</summary>
    private static object? CommonValue(PropertyInfo property, object?[] targets)
    {
        object? value = null;
        try
        {
            for (int index = 0; index < targets.Length; index++)
            {
                object? current = targets[index] is { } target ? property.GetValue(target) : null;
                if (index == 0)
                    value = current;
                else if (!ReferenceEquals(value, current))
                    return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
        return value;
    }
}

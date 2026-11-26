using System.Numerics;
using System.Reflection;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// The controls of one persistent event callback row, like a row of the ImGui callbacks table: a node picker
/// in the name column, a method picker in the value column, and a remove button at the right. Each change is
/// recorded for undo, and then the inspector rebuilds its rows.
/// </summary>
internal sealed class InspectorEventCallbackEditor : IUIComboOptionSource
{
    private readonly InspectorPanel _owner;
    private readonly UIBoundableTransform _nodeRoot;
    private readonly UIBoundableTransform _methodRoot;
    private readonly UIComboBoxComponent _nodeCombo;
    private readonly UIComboBoxComponent _methodCombo;
    private readonly UITextComponent _nodeLabel;
    private readonly UITextComponent _methodLabel;
    private readonly InspectorObjectCandidateSource _nodes = new();
    private readonly List<EditorImGuiUI.EventCallbackMethodChoice> _methods = [];
    private object? _target;
    private InspectorEventCallbackRow? _row;
    private XRPersistentCall? _call;
    private bool _methodsStale;
    private ulong _methodsRevision;

    private const float RemoveButtonWidth = 24.0f;

    public InspectorEventCallbackEditor(SceneNode nameCell, SceneNode valueRoot, InspectorPanel owner, float nameInset)
    {
        _owner = owner;
        (_nodeRoot, _nodeCombo, _nodeLabel) = CreatePicker(nameCell, "CallbackNode",
            new Vector4(nameInset, 2.0f, 6.0f, 2.0f));
        _nodeCombo.SelectionChanged += OnNodePicked;

        SceneNode valueNode = valueRoot.NewChild("CallbackMethod");
        _methodRoot = valueNode.SetTransform<UIBoundableTransform>();
        _methodRoot.MaxAnchor = Vector2.One;
        (_, _methodCombo, _methodLabel) = CreatePicker(valueNode, "Method",
            new Vector4(0.0f, 0.0f, RemoveButtonWidth + 2.0f, 0.0f));
        _methodCombo.SelectionChanged += OnMethodPicked;

        SceneNode remove = valueNode.NewChild<UIButtonComponent, UIMaterialComponent>(out var removeButton, out var removeBackground);
        removeBackground.SetBatchSolidColor(ColorF4.Transparent);
        removeButton.Style = EditorGlassTheme.Row;
        UIBoundableTransform removeBounds = remove.GetTransformAs<UIBoundableTransform>(true)!;
        removeBounds.MinAnchor = new Vector2(1.0f, 0.0f);
        removeBounds.MaxAnchor = Vector2.One;
        removeBounds.NormalizedPivot = new Vector2(1.0f, 0.0f);
        removeBounds.Width = RemoveButtonWidth;
        removeBounds.BlocksInputBehind = true;
        NativeUIElements.CreateIcon(remove, EUIQuadIcon.Cross, new Vector2(0.5f, 0.5f), Vector2.Zero, 9.0f,
            EditorGlassTheme.TextSecondary);
        removeButton.InteractAction += _ => Remove();

        _nodeRoot.Visibility = EVisibility.Collapsed;
        _methodRoot.Visibility = EVisibility.Collapsed;
    }

    /// <summary>True while the editor shows a callback.</summary>
    public bool IsBound => _row is not null;

    /// <summary>Shows callback <paramref name="row"/> of the event on <paramref name="target"/>.</summary>
    public void Bind(InspectorEventCallbackRow row, object target)
    {
        _row = row;
        _target = target;
        List<XRPersistentCall>? calls = EditorImGuiUI.GetEventCallbacks(row.EventProperty.GetValue(target));
        _call = calls is not null && row.Index < calls.Count ? calls[row.Index] : null;
        SceneNode? node = _call is null ? null : EditorImGuiUI.ResolveEventCallbackNode(_call);
        SetText(_nodeLabel, node?.Name ?? (_call?.NodeId == Guid.Empty ? "Select node" : "Missing node"));
        SetText(_methodLabel, _call is null ? string.Empty : EditorImGuiUI.DescribeEventCallback(_call));
        _nodes.Reset(typeof(SceneNode), [target], nullable: false);
        _nodeCombo.VirtualOptions = _nodes;
        _methodsStale = true;
        _methodCombo.VirtualOptions = this;
        _methodCombo.IsReadOnly = node is null;
        Show(_nodeRoot);
        Show(_methodRoot);
    }

    /// <summary>Hides the controls and releases the callback.</summary>
    public void Unbind()
    {
        _row = null;
        _target = null;
        _call = null;
        _nodeCombo.Close();
        _methodCombo.Close();
        _nodeCombo.VirtualOptions = null;
        _methodCombo.VirtualOptions = null;
        _nodeRoot.Visibility = EVisibility.Collapsed;
        _methodRoot.Visibility = EVisibility.Collapsed;
    }

    // The method picker lists "None" (clear the method), then the methods of the callback's node.
    public int Count
    {
        get
        {
            if (_methodsStale)
                RefreshMethods();
            return _methods.Count + 1;
        }
    }

    public ulong Revision => _methodsRevision;

    public UIComboOption GetOption(int index)
        => index == 0
            ? new(1, "None")
            : new((ulong)index + 1, $"{_methods[index - 1].Group} · {_methods[index - 1].Label}");

    public int IndexOfId(ulong id) => id >= 1 && id <= (ulong)_methods.Count + 1 ? (int)(id - 1) : -1;

    private void RefreshMethods()
    {
        _methodsStale = false;
        _methodsRevision++;
        _methods.Clear();
        if (_call is not null && _row is not null && EditorImGuiUI.ResolveEventCallbackNode(_call) is { } node)
            _methods.AddRange(EditorImGuiUI.GetEventCallbackMethodChoices(node, _row.PropertyType));
    }

    private void OnNodePicked(UIValueChange<ulong?> change)
    {
        if (change.Origin != EUIValueChangeOrigin.User || change.NewValue is not { } id ||
            _call is not { } call || _target is not { } target ||
            !_nodes.TryGetCandidate(id, out InspectorObjectCandidateSource.Candidate candidate) ||
            candidate.Value is not SceneNode node)
            return;
        _nodeCombo.Select(null);
        Run(() => EditorImGuiUI.SetEventCallbackNode(target, call, node), "set the callback node");
    }

    private void OnMethodPicked(UIValueChange<ulong?> change)
    {
        if (change.Origin != EUIValueChangeOrigin.User || change.NewValue is not { } id ||
            _call is not { } call || _target is not { } target)
            return;
        int index = IndexOfId(id);
        if (index < 0)
            return;
        EditorImGuiUI.EventCallbackMethodChoice? choice = index == 0 ? null : _methods[index - 1];
        _methodCombo.Select(null);
        Run(() => EditorImGuiUI.SetEventCallbackMethod(target, call, choice), "set the callback method");
    }

    private void Remove()
    {
        if (_row is not { } row || _target is not { } target ||
            EditorImGuiUI.GetEventCallbacks(row.EventProperty.GetValue(target)) is not { } calls)
            return;
        Run(() => EditorImGuiUI.RemoveEventCallback(target, calls, row.Index), "remove the callback");
    }

    private void Run(Action change, string description)
    {
        try { change(); }
        catch (Exception exception) { Debug.LogException(exception, $"Could not {description}."); }
        _owner.RefreshAfterEventCallbackChange();
    }

    private static (UIBoundableTransform Root, UIComboBoxComponent Combo, UITextComponent Label) CreatePicker(
        SceneNode parent, string name, Vector4 margins)
    {
        SceneNode field = parent.NewChild(name);
        UIBoundableTransform bounds = field.SetTransform<UIBoundableTransform>();
        bounds.MaxAnchor = Vector2.One;
        bounds.Margins = margins;
        bounds.BlocksInputBehind = true;
        UIComboBoxComponent combo = field.AddComponent<UIComboBoxComponent>()!;
        NativePropertyPopupHost popup = field.AddComponent<NativePropertyPopupHost>()!;
        combo.Popup = popup.CreatePopup();
        combo.Opening += _ => combo.PopupPosition = popup.GetPosition();
        NativeUIElements.ApplyFieldStyle(field.AddComponent<UIMaterialComponent>()!);
        NativeUIElements.CreateIcon(field, EUIQuadIcon.ChevronDown, new Vector2(1.0f, 0.5f),
            new Vector2(-11.0f, 0.0f), 9.0f, EditorGlassTheme.TextSecondary);
        field.NewChild<UITextComponent>(out var label);
        label.BoundableTransform.Margins = new Vector4(8.0f, 0.0f, 22.0f, 0.0f);
        label.FontSize = EditorUI.Styles.PropertyInputFontSize;
        label.Color = EditorUI.Styles.PropertyInputTextColor;
        label.VerticalAlignment = EVerticalAlignment.Center;
        label.ClipToBounds = true;
        return (bounds, combo, label);
    }

    private static void SetText(UITextComponent label, string text)
    {
        if (label.Text != text)
            label.Text = text;
    }

    private static void Show(UIBoundableTransform bounds)
    {
        if (bounds.Visibility != EVisibility.Visible)
            bounds.Visibility = EVisibility.Visible;
    }
}

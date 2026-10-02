using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Browser;

/// <summary>Exposes the focused engine button to a native DOM accessibility control.</summary>
internal sealed class BrowserAccessibleControlBridge
{
    private static int _nextGeneration;
    private UIButtonComponent? _target;
    private int _generation;
    private string _observedLabel = "Engine button";
    private int _labelVersion;

    public int Generation => _generation;
    public string Label => _observedLabel;
    public int LabelVersion => _labelVersion;
    public float X { get; private set; } = -1;
    public float Y { get; private set; } = -1;
    public float Width { get; private set; }
    public float Height { get; private set; }

    public int Refresh(object? focusedInteractable, XRViewport? viewport)
    {
        UIButtonComponent? target = focusedInteractable as UIButtonComponent;
        if (target is not { IsActiveInHierarchy: true, IsFocused: true } ||
            !target.UITransform.IsVisibleInHierarchy ||
            !BrowserUiBoundsProjection.TryProject(target, viewport,
                out float x, out float y, out float width, out float height))
        {
            Clear();
            return 0;
        }
        if (!ReferenceEquals(target, _target))
        {
            _target = target;
            _generation = Interlocked.Increment(ref _nextGeneration);
        }
        ObserveLabel();
        X = x;
        Y = y;
        Width = width;
        Height = height;
        return _generation;
    }

    public bool Activate(int generation, object? focusedInteractable)
    {
        if (generation == 0 || generation != _generation ||
            _target is not { IsActiveInHierarchy: true, IsFocused: true } ||
            !ReferenceEquals(_target, focusedInteractable))
            return false;
        _target.OnInteract();
        return true;
    }

    public void Clear()
    {
        _target = null;
        _generation = 0;
        _observedLabel = "Engine button";
        X = Y = -1;
        Width = Height = 0;
    }

    private void ObserveLabel()
    {
        string label = _target?.TextComponent?.Text is { Length: > 0 } text ? text :
            _target?.Name ?? _target?.SceneNode?.Name ?? "Engine button";
        if (!string.Equals(label, _observedLabel, StringComparison.Ordinal))
        {
            _observedLabel = label;
            _labelVersion++;
        }
    }
}

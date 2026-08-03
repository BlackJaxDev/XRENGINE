namespace XREngine.Components;

/// <summary>
/// A collapsible group of existing programmable controls. The group retains the source
/// field collection so changes made through either inspector operate on the same state.
/// </summary>
public sealed class CustomUIGroupField(
    string label,
    IReadOnlyList<CustomUIField> fields,
    Func<bool>? isVisible = null,
    bool defaultOpen = true,
    string? helpText = null)
    : CustomUIField(label, helpText)
{
    public IReadOnlyList<CustomUIField> Fields { get; } =
        fields ?? throw new ArgumentNullException(nameof(fields));

    public bool DefaultOpen { get; } = defaultOpen;

    public bool IsVisible()
        => isVisible?.Invoke() ?? true;
}

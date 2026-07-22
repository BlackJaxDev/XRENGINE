namespace XREngine.Components;

/// <summary>Editable enum selection exposed by <see cref="CustomUIComponent"/>.</summary>
public sealed class CustomUIEnumField(
    string label,
    Func<int> getter,
    Action<int> setter,
    string[] options,
    string? helpText = null)
    : CustomUIField(label, helpText)
{
    public string[] Options { get; } = options;

    public int GetSelectedIndex()
        => getter();

    public void SetSelectedIndex(int value)
        => setter(value);
}

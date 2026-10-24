namespace XREngine.UnitTests.Rendering;

internal sealed class UITextDraftPropertyTarget
{
    private string _value = string.Empty;
    private int _number;
    public Action? OnValueSet { get; set; }
    public Action? OnNumberSet { get; set; }
    public string Value
    {
        get => _value;
        set { _value = value; OnValueSet?.Invoke(); }
    }
    public int Number
    {
        get => _number;
        set { _number = value; OnNumberSet?.Invoke(); }
    }
}

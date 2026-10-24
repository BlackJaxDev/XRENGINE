namespace XREngine.UnitTests.Editor;

/// <summary>Allows a completed scalar write to retire the next write's caller.</summary>
public sealed class EditorGuardedPropertyTarget
{
    private int _number;
    public Action? AfterWrite { get; set; }
    public int Number
    {
        get => _number;
        set { _number = value; AfterWrite?.Invoke(); }
    }
}

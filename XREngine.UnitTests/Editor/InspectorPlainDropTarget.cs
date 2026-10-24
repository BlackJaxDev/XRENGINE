namespace XREngine.UnitTests.Editor;

/// <summary>Exposes a non-notifying reference getter for drop admission tests.</summary>
public sealed class InspectorPlainDropTarget
{
    public object? Stored;
    public Action? OnRead { get; set; }
    public int Writes { get; private set; }
    public object? Reference
    {
        get { OnRead?.Invoke(); return Stored; }
        set { Stored = value; Writes++; }
    }
}

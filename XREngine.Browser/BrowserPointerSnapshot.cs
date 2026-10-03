namespace XREngine.Browser;

/// <summary>Retained normalized canvas pointer, including its last capture/release/cancel phase.</summary>
public readonly record struct BrowserPointerSnapshot(int Id, int Phase, float X, float Y, int Kind)
{
    public bool Active => Phase is 0 or 1;
}

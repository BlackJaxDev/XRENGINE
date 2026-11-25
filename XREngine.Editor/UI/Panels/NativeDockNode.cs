namespace XREngine.Editor;

/// <summary>A persisted split or tab stack in one native editor window.</summary>
public sealed class NativeDockNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public NativeDockNode? First { get; set; }
    public NativeDockNode? Second { get; set; }
    public bool Vertical { get; set; }
    public bool FirstFixedSize { get; set; } = true;
    public float FixedSize { get; set; } = 300;
    public List<string> Tabs { get; set; } = [];
    public string? Selected { get; set; }
    public bool IsSplit => First is not null && Second is not null;
}

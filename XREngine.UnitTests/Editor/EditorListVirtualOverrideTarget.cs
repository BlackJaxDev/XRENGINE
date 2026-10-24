namespace XREngine.UnitTests.Editor;

public sealed class EditorListVirtualOverrideTarget : EditorListVirtualBaseTarget
{
    public Action? OnGet { get; set; }

    public override List<int> Values
    {
        get
        {
            OnGet?.Invoke();
            return base.Values;
        }
        set => base.Values = value;
    }
}

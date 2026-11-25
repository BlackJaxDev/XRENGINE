namespace XREngine.UnitTests.Editor;

/// <summary>Runs a test callback when a list owner resolves its property.</summary>
public sealed class EditorListMutationGetterTarget
{
    private List<int> _values = [];

    public Action? OnGet { get; set; }

    public List<int> Values
    {
        get
        {
            OnGet?.Invoke();
            return _values;
        }
        set => _values = value;
    }
}

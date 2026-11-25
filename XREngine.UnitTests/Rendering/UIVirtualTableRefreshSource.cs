using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UIVirtualTableRefreshSource : IUIVirtualTableSource<string>
{
    public ulong ItemId { get; private set; } = 11;
    public string Text { get; private set; } = "First";
    public int Count => 1;
    public ulong Revision { get; private set; } = 1;
    public Action? OnGetItem { get; set; }
    public event Action? Changed;
    public ulong GetItemId(int index)
        => index == 0 ? ItemId : throw new ArgumentOutOfRangeException(nameof(index));
    public string GetItem(int index)
    {
        if (index != 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        OnGetItem?.Invoke();
        return Text;
    }
    public void Replace(ulong id, string text)
    {
        ItemId = id;
        Text = text;
        Revision++;
    }
    public void RaiseChanged() => Changed?.Invoke();
    public void AdvanceRevision() => Revision++;
}

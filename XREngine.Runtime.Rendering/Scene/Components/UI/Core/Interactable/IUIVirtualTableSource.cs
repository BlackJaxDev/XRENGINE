namespace XREngine.Rendering.UI;

/// <summary>Owner-thread source for a virtual table. IDs stay stable across order changes.</summary>
public interface IUIVirtualTableSource<out T>
{
    int Count { get; }
    ulong Revision { get; }
    ulong GetItemId(int index);
    T GetItem(int index);
}

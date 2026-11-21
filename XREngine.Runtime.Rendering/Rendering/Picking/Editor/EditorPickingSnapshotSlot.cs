using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>Holds frozen owner rows until the last snapshot lease is released.</summary>
internal sealed class EditorPickingSnapshotSlot
{
    public EditorPickingSnapshotSlot(int initialRowCapacity)
    {
        Rows = new EditorPickingSnapshotRow[initialRowCapacity];
        RowIndexByKey = new Dictionary<uint, int>(initialRowCapacity);
    }

    public EditorPickingSnapshotRow[] Rows;
    public readonly Dictionary<uint, int> RowIndexByKey;
    public int RowCount;
    public int RefCount;
    public ulong Generation;
    public long PackageGeneration;
    public BackendReadyFramePackageIdentity Identity;

    public void ClearRows()
    {
        Array.Clear(Rows, 0, RowCount);
        RowIndexByKey.Clear();
        RowCount = 0;
        PackageGeneration = 0;
        Identity = default;
    }
}

namespace XREngine.Rendering;

/// <summary>Tracks one independently disposable use of a frozen snapshot slot.</summary>
internal struct EditorPickingLeaseToken
{
    public int SlotIndex;
    public ulong SlotGeneration;
    public ulong TokenGeneration;
    public bool Active;
}

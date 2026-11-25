namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Identifies one binding of a pooled row. A callback must match both values before it changes a row.
    /// </summary>
    public readonly record struct UIVirtualRowLease(ulong ItemId, ulong Incarnation);
}

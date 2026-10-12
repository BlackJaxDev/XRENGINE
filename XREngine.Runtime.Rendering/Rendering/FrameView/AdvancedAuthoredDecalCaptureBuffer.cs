namespace XREngine.Rendering;

/// <summary>Reusable storage whose generation cannot be observed after its exclusive lease is returned.</summary>
internal sealed class AdvancedAuthoredDecalCaptureBuffer
{
    internal AdvancedAuthoredDecalCaptureRow[] Storage = [];
    internal int Count;
    internal bool Leased;
    internal ulong Generation;
    internal ReadOnlySpan<AdvancedAuthoredDecalCaptureRow> Read(ulong generation)
    {
        if (!Leased || Generation != generation)
            throw new InvalidOperationException("The authored decal capture lease has expired.");
        return Storage.AsSpan(0, Count);
    }
    internal void Release(ulong generation)
    {
        lock (this)
        {
            if (!Leased || Generation != generation) return;
            Array.Clear(Storage, 0, Count);
            Count = 0;
            Leased = false;
        }
    }
}

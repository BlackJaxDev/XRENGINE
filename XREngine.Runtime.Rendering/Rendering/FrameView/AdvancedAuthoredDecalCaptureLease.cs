namespace XREngine.Rendering;

/// <summary>Exclusive producer input retained by GPUScene across publication retries, never by an output snapshot.</summary>
internal readonly record struct AdvancedAuthoredDecalCaptureLease(AdvancedAuthoredDecalCaptureBuffer? Owner, ulong Generation)
{
    internal int Count => Rows.Length;
    internal ReadOnlySpan<AdvancedAuthoredDecalCaptureRow> Rows
        => Owner is null ? [] : Owner.Read(Generation);
    internal void Release() => Owner?.Release(Generation);
}

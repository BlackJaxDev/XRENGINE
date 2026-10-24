namespace XREngine.Editor;

/// <summary>Resolves a bounded, navigable slice of a complete frame-output manifest.</summary>
internal readonly record struct NativeProfilerOutputPage(int Index, int PageCount, int Start, int Count, int Total)
{
    public const int Size = 16;

    public static NativeProfilerOutputPage Resolve(int total, int requestedIndex)
    {
        total = Math.Max(0, total);
        int pages = total == 0 ? 0 : 1 + (total - 1) / Size;
        int index = Math.Clamp(requestedIndex, 0, Math.Max(0, pages - 1));
        int start = index * Size;
        return new(index, pages, start, Math.Min(Size, total - start), total);
    }
}

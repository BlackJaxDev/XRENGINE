namespace XREngine;

/// <summary>
/// Per-thread queue shared by the lower YAML graph visitor and the Runtime.Core transform
/// inspector. The queue contains no transform types or runtime behavior.
/// </summary>
public static class YamlTransformReferenceContext
{
    [ThreadStatic]
    private static Queue<bool>? _readEntries;

    [ThreadStatic]
    private static Queue<bool>? _writeEntries;

    public static void EnqueueRead()
        => (_readEntries ??= new Queue<bool>()).Enqueue(true);

    public static bool ConsumeRead()
    {
        Queue<bool>? entries = _readEntries;
        if (entries is null || entries.Count == 0)
            return false;

        _ = entries.Dequeue();
        if (entries.Count == 0)
            _readEntries = null;
        return true;
    }

    public static void EnqueueWrite()
        => (_writeEntries ??= new Queue<bool>()).Enqueue(true);

    public static bool ConsumeWrite()
    {
        Queue<bool>? entries = _writeEntries;
        if (entries is null || entries.Count == 0)
            return false;

        _ = entries.Dequeue();
        if (entries.Count == 0)
            _writeEntries = null;
        return true;
    }

    public static void ResetReadState()
        => _readEntries = null;

    public static void ResetWriteState()
        => _writeEntries = null;
}

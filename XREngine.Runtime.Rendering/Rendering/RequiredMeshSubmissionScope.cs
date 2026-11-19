namespace XREngine.Rendering;

/// <summary>
/// Marks a synchronous draw enqueue as required. The Vulkan request freezes this
/// flag before its deferred preparation starts.
/// </summary>
public readonly ref struct RequiredMeshSubmissionScope
{
    [ThreadStatic]
    private static int s_depth;

    private readonly int _previousDepth;

    private RequiredMeshSubmissionScope(bool required)
    {
        _previousDepth = required ? s_depth : -1;
        if (required)
            s_depth++;
    }

    public static bool IsActive => s_depth > 0;

    public static RequiredMeshSubmissionScope EnterIf(bool required)
        => new(required);

    public void Dispose()
    {
        if (_previousDepth >= 0)
            s_depth = _previousDepth;
    }
}

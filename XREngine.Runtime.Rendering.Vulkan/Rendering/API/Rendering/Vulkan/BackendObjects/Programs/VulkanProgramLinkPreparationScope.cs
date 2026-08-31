namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Keeps explicit logical-frame preparation synchronous for one resource owner.
/// This does not change desktop compile policy or flow into worker threads.
/// </summary>
internal ref struct VulkanProgramLinkPreparationScope
{
    [ThreadStatic]
    private static VulkanResourceRuntime? _current;
    private readonly VulkanResourceRuntime? _previous;
    private readonly int _threadId;
    private readonly bool _active;
    private bool _disposed;

    internal VulkanProgramLinkPreparationScope(VulkanResourceRuntime owner, bool enabled = true)
    {
        _active = enabled;
        _previous = _current;
        _threadId = Environment.CurrentManagedThreadId;
        _disposed = false;
        if (enabled)
            _current = owner;
    }

    internal static bool RequiresSynchronousLink(VulkanResourceRuntime owner)
        => ReferenceEquals(_current, owner);

    public void Dispose()
    {
        if (!_active || _disposed)
            return;
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Program preparation scopes must end on their owning thread.");
        _current = _previous;
        _disposed = true;
    }
}

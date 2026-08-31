namespace XREngine.Rendering;

public abstract partial class GenericRenderObject
{
    [ThreadStatic]
    private static IRenderApiWrapperOwner? _apiWrapperCreationOwner;

    /// <summary>
    /// Selects the owner of newly constructed render objects during synchronous
    /// explicit-host work. It neither registers a window nor flows to workers.
    /// Wrapper-creation suppression continues to take precedence.
    /// </summary>
    public static ApiWrapperCreationOwnerScope PushApiWrapperCreationOwner(IRenderApiWrapperOwner owner)
        => new(owner);

    /// <summary>Restores the calling thread's previous cold-creation owner.</summary>
    public ref struct ApiWrapperCreationOwnerScope
    {
        private readonly IRenderApiWrapperOwner? _previous;
        private readonly int _threadId;
        private bool _disposed;

        internal ApiWrapperCreationOwnerScope(IRenderApiWrapperOwner owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            _previous = _apiWrapperCreationOwner;
            _threadId = Environment.CurrentManagedThreadId;
            _disposed = false;
            _apiWrapperCreationOwner = owner;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            if (Environment.CurrentManagedThreadId != _threadId)
                throw new InvalidOperationException("Render-object creation scopes must end on their owning thread.");
            _apiWrapperCreationOwner = _previous;
            _disposed = true;
        }
    }
}

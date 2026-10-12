namespace XREngine.Rendering;

public static partial class RenderDiagnosticsFlags
{
    [ThreadStatic]
    private static int _deferredDebugViewWriteSuppressionDepth;

    /// <summary>
    /// Prevents synchronous asset reads on this thread from changing the live deferred
    /// debug preference. Authored pipeline values still hydrate normally, and preference
    /// changes on other threads remain visible without being restored over on disposal.
    /// Dispose the returned scope on the creating thread; do not carry it across awaits.
    /// </summary>
    public static IDisposable SuppressDeferredDebugViewWritesForCurrentThread()
        => new DeferredDebugViewWriteSuppressionScope();

    private sealed class DeferredDebugViewWriteSuppressionScope : IDisposable
    {
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private bool _disposed;

        public DeferredDebugViewWriteSuppressionScope()
            => _deferredDebugViewWriteSuppressionDepth++;

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("Deferred debug preference suppression must be disposed on its creating thread.");
            if (_disposed)
                return;

            _deferredDebugViewWriteSuppressionDepth--;
            _disposed = true;
        }
    }
}

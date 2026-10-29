namespace XREngine.Data;

/// <summary>Coordinates short source publications with host installation changes.</summary>
public static class RuntimeSourcePublicationServices
{
    private static readonly object Gate = new();
    private static long _generation;
    private static int _changes;
    private static int _publications;

    public static long Generation
    {
        get { lock (Gate) return _generation; }
    }

    public static bool IsCurrent(long generation)
    {
        lock (Gate) return generation == _generation && _changes == 0;
    }

    /// <summary>Admits a host change unless a source commit is active. No gate remains held.</summary>
    public static IDisposable BeginInstallationChange()
    {
        lock (Gate)
        {
            if (_publications != 0)
                throw new InvalidOperationException("SourcePublication.PublicationInProgress: retry the host installation change after source publication completes.");
            _generation = checked(_generation + 1);
            _changes++;
        }
        return new Reservation(publication: false);
    }

    /// <summary>Reserves an unchanged installation for synchronous publication, never for I/O.</summary>
    public static IDisposable BeginPublication(long generation)
    {
        lock (Gate)
        {
            if (generation != _generation || _changes != 0)
                throw new OperationCanceledException("SourcePublication.StaleInstallation: retry source preparation against the current host installation.");
            _publications++;
        }
        return new Reservation(publication: true);
    }

    private sealed class Reservation(bool publication) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate)
            {
                if (publication) _publications--;
                else _changes--;
            }
        }
    }
}

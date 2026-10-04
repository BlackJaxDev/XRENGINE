namespace XREngine.Data;

/// <summary>Owns immutable raw-read installations and admission to their synchronous publication.</summary>
public static class RuntimeAssetReadServices
{
    internal static readonly object Gate = new();
    private static RuntimeAssetReadBinding _current = new(null);
    private static bool _callerThread;

    /// <summary>The source and its lower-level adapter are published together through this installation.</summary>
    public static IRuntimeAssetReadSource? Source => Volatile.Read(ref _current).Source;

    /// <summary>Set by runtime composition when its explicit caller-thread executor starts or stops.</summary>
    public static bool IsCallerThread
    {
        get => Volatile.Read(ref _callerThread);
        set => Volatile.Write(ref _callerThread, value);
    }

    /// <summary>Replaces an installation, cancelling its pending reads without retargeting them.</summary>
    public static void SetSource(IRuntimeAssetReadSource? source)
    {
        RuntimeAssetReadBinding previous;
        lock (Gate)
        {
            previous = _current;
            if (previous.Publishers != 0)
                throw new InvalidOperationException("AssetSource.PublicationInProgress: retry source replacement after the current raw asset publication completes.");
            var replacement = new RuntimeAssetReadBinding(source);
            previous.Retired = true;
            Volatile.Write(ref _current, replacement);
        }
        // Cancellation may execute source/user callbacks, including another replacement.
        previous.CancelRetiredReads();
    }

    /// <summary>Captures the installed source and its lifetime before starting a read.</summary>
    public static RuntimeAssetReadLease Capture(CancellationToken cancellationToken = default)
    {
        RuntimeAssetReadBinding binding;
        lock (Gate)
        {
            binding = _current;
            binding.Readers++;
        }
        try { return new RuntimeAssetReadLease(binding, cancellationToken); }
        catch
        {
            binding.ReleaseReader();
            throw;
        }
    }

    /// <summary>Rejects authoring APIs before host-file access or a default background import is started.</summary>
    public static void EnsureHostFileAccess(string operation)
    {
        using RuntimeAssetReadLease read = Capture();
        read.EnsureHostFileAccess(operation);
    }

    internal static async Task<T> RunHostOperationAsync<T>(string operation, Func<T> action)
    {
        using RuntimeAssetReadLease read = Capture();
        read.EnsureHostFileAccess(operation);
        return await Task.Run(() =>
        {
            read.EnsureHostFileAccess(operation);
            using IDisposable publication = read.BeginPublication();
            return action();
        }, read.CancellationToken).ConfigureAwait(false);
    }

    internal static void EnsureCurrent(RuntimeAssetReadBinding binding)
    {
        if (binding.Retired || !ReferenceEquals(Volatile.Read(ref _current), binding))
            throw new OperationCanceledException("AssetSource.StaleRead: the raw asset source installation was retired.", binding.Token);
    }
}

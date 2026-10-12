namespace XREngine.Data;

/// <summary>A read retains its source and cancellation epoch until it finishes or is discarded.</summary>
public sealed class RuntimeAssetReadLease : IDisposable
{
    private readonly RuntimeAssetReadBinding _binding;
    private readonly CancellationTokenSource _cancellation;
    private int _disposed;

    internal RuntimeAssetReadLease(RuntimeAssetReadBinding binding, CancellationToken cancellationToken)
    {
        _binding = binding;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(binding.Token, cancellationToken);
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public bool Exists(string path)
    {
        EnsureCurrent();
        IRuntimeAssetReadSource? source = _binding.Source;
        bool exists;
        if (source is not null)
        {
            if (source.SupportsHostFileAccess)
                EnsureHostFileAccess("Asset existence lookup");
            exists = source.Exists(path);
        }
        else
        {
            EnsureHostFileAccess("Asset existence lookup");
            exists = File.Exists(path);
        }
        EnsureCurrent();
        return exists;
    }

    /// <summary>Uses source metadata when available; a caller-thread host-file read skips its synchronous probe.</summary>
    public bool TryGetExists(string path, out bool exists)
    {
        EnsureCurrent();
        if ((OperatingSystem.IsBrowser() || RuntimeAssetReadServices.IsCallerThread)
            && (_binding.Source?.SupportsHostFileAccess ?? true))
        {
            exists = false;
            EnsureCurrent();
            return false;
        }
        exists = Exists(path);
        return true;
    }

    public byte[] ReadAllBytes(string path)
    {
        EnsureCurrent();
        IRuntimeAssetReadSource? source = _binding.Source;
        byte[] bytes;
        if (source is not null)
        {
            if (!source.SupportsSynchronousReads)
                throw new NotSupportedException("AssetSource.AsyncReadRequired: this source requires an asynchronous raw asset read.");
            if (source.SupportsHostFileAccess)
                EnsureHostFileAccess("Raw asset read");
            bytes = source.ReadAllBytes(path);
        }
        else
        {
            EnsureHostFileAccess("Raw asset read");
            bytes = File.ReadAllBytes(path);
        }
        EnsureCurrent();
        return bytes;
    }

    public async Task<byte[]> ReadAllBytesAsync(string path)
    {
        EnsureCurrent();
        IRuntimeAssetReadSource? source = _binding.Source;
        byte[] bytes;
        if (source is not null)
            bytes = await source.ReadAllBytesAsync(path, CancellationToken).ConfigureAwait(false);
        else
        {
            EnsureHostFileAccess("Raw asset read");
            bytes = await File.ReadAllBytesAsync(path, CancellationToken).ConfigureAwait(false);
        }
        EnsureCurrent();
        return bytes;
    }

    public void EnsureCurrent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CancellationToken.ThrowIfCancellationRequested();
        RuntimeAssetReadServices.EnsureCurrent(_binding);
    }

    public void EnsureHostFileAccess(string operation)
    {
        EnsureCurrent();
        if (OperatingSystem.IsBrowser() || RuntimeAssetReadServices.IsCallerThread
            || _binding.Source is { SupportsHostFileAccess: false })
            throw new NotSupportedException($"AssetSource.HostFileUnavailable: {operation} requires a synchronous host-file owner; use the installed runtime asset source for reads.");
        EnsureCurrent();
    }

    /// <summary>Reserves synchronous publication; replacement must retry until this reservation ends.</summary>
    public IDisposable BeginPublication()
    {
        lock (RuntimeAssetReadServices.Gate)
        {
            EnsureCurrent();
            _binding.Publishers++;
        }
        return new PublicationLease(_binding);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancellation.Dispose();
        _binding.ReleaseReader();
    }

    private sealed class PublicationLease(RuntimeAssetReadBinding binding) : IDisposable
    {
        private RuntimeAssetReadBinding? _binding = binding;
        public void Dispose()
        {
            RuntimeAssetReadBinding? current = Interlocked.Exchange(ref _binding, null);
            if (current is null)
                return;
            lock (RuntimeAssetReadServices.Gate)
                current.Publishers--;
        }
    }
}

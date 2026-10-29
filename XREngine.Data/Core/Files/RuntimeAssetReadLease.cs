namespace XREngine.Data;

/// <summary>A read retains its source, host fallback owner, and cancellation epoch until it finishes or is discarded.</summary>
public sealed class RuntimeAssetReadLease : IDisposable
{
    private readonly RuntimeAssetReadBinding _binding;
    private readonly CancellationTokenSource _cancellation;
    private readonly IRuntimeFileDiscovery? _discovery;
    private readonly long _discoveryGeneration;
    private int _disposed;

    internal RuntimeAssetReadLease(RuntimeAssetReadBinding binding, CancellationToken cancellationToken)
    {
        _binding = binding;
        if (binding.Source is null)
            _discovery = RuntimeFileDiscoveryServices.Capture(out _discoveryGeneration);
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
            IRuntimeHostFileReadBackend files = RequireHostFileReadBackend();
            try { exists = files.FileExists(path); }
            finally { EnsureCurrent(); }
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
            IRuntimeHostFileReadBackend files = RequireHostFileReadBackend();
            try { bytes = files.ReadAllBytes(path); }
            finally { EnsureCurrent(); }
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
            if (_discovery is not IRuntimeHostFileAsyncReadBackend files)
                throw new NotSupportedException("AssetSource.HostFileAsyncReadUnavailable: the captured file system does not provide asynchronous host-file reads.");
            try { bytes = await files.ReadAllBytesAsync(path, CancellationToken).ConfigureAwait(false); }
            finally { EnsureCurrent(); }
        }
        EnsureCurrent();
        return bytes;
    }

    public void EnsureCurrent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CancellationToken.ThrowIfCancellationRequested();
        RuntimeAssetReadServices.EnsureCurrent(_binding);
        if (_binding.Source is null && !RuntimeFileDiscoveryServices.IsCurrent(_discovery, _discoveryGeneration))
            throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the asset file discovery installation changed during the raw asset read.");
    }

    private IRuntimeHostFileReadBackend RequireHostFileReadBackend()
        => _discovery as IRuntimeHostFileReadBackend ?? throw new NotSupportedException(
            "AssetSource.HostFileReadUnavailable: the captured file system does not provide direct host-file reads.");

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
        IDisposable? hostPublication = null;
        try
        {
            // Reserve the current installation window, then check this lease's owners.
            // Unrelated host changes before publication do not retire a raw read.
            if (_binding.Source is null)
                hostPublication = RuntimeSourcePublicationServices.BeginPublication(RuntimeSourcePublicationServices.Generation);
            lock (RuntimeAssetReadServices.Gate)
            {
                EnsureCurrent();
                _binding.Publishers++;
            }
            return new PublicationLease(_binding, hostPublication);
        }
        catch
        {
            hostPublication?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancellation.Dispose();
        _binding.ReleaseReader();
    }

    private sealed class PublicationLease(RuntimeAssetReadBinding binding, IDisposable? hostPublication) : IDisposable
    {
        private RuntimeAssetReadBinding? _binding = binding;
        public void Dispose()
        {
            RuntimeAssetReadBinding? current = Interlocked.Exchange(ref _binding, null);
            if (current is null)
                return;
            lock (RuntimeAssetReadServices.Gate)
                current.Publishers--;
            hostPublication?.Dispose();
        }
    }
}

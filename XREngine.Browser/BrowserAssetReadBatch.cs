using XREngine.Core.Files;

namespace XREngine.Browser;

/// <summary>Bounded asynchronous asset reads with batch-owned result bytes.</summary>
internal sealed class BrowserAssetReadBatch(BrowserEngineAssetSource source) : IAssetReadBatch
{
    private readonly List<(string Path, long Offset, int Length)> _requests = [];
    private readonly CancellationTokenSource _lifetime = new();
    private byte[][]? _results;
    private bool _started;
    private bool _disposed;
    public int Count => _requests.Count;
    public int AddFile(string path) => Add(path, 0, -1);
    public int AddRange(string path, long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return Add(path, offset, length);
    }
    private int Add(string path, long offset, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started || _requests.Count >= 3)
            throw new InvalidOperationException("AssetSource.BatchBudgetExceeded: a batch admits up to three reads before execution.");
        _requests.Add((path, offset, length));
        return _requests.Count - 1;
    }
    public bool Execute(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("AssetSource.AsyncReadRequired: execute browser batches asynchronously.");
    public async Task<bool> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("AssetSource.BatchAlreadyExecuted.");
        _started = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        Task<byte[]>[] reads = new Task<byte[]>[_requests.Count];
        for (int index = 0; index < reads.Length; index++)
        {
            var request = _requests[index];
            reads[index] = request.Length < 0 ? source.ReadAllBytesAsync(request.Path, cancellation.Token)
                : source.ReadRangeAsync(request.Path, request.Offset, request.Length, cancellation.Token);
        }
        byte[][] results = await Task.WhenAll(reads);
        cancellation.Token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _results = results;
        return true;
    }
    public byte[] GetResult(int index) => TryGetResult(index, out byte[]? result) ? result!
        : throw new InvalidOperationException("AssetSource.BatchResultUnavailable.");
    public bool TryGetResult(int index, out byte[]? data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        data = _results is not null && index >= 0 && index < _results.Length ? _results[index] : null;
        return data is not null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _results = null;
        _requests.Clear();
    }
}

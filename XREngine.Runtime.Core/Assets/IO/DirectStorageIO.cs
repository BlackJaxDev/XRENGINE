namespace XREngine.Core.Files;

/// <summary>Asset reads delegated to the source installed by the application.</summary>
public static class DirectStorageIO
{
    /// <summary>Installs the asset source before worlds or importers begin loading.</summary>
    public static IRuntimeAssetSource? Source
    {
        get => (XREngine.Data.RuntimeAssetReadServices.Source as RuntimeAssetReadSource)?.Source;
        set => XREngine.Data.RuntimeAssetReadServices.SetSource(value is null ? null : new RuntimeAssetReadSource(value));
    }

    public static bool IsEnabled => Source?.IsAccelerated ?? false;
    public static string Status => Source?.Status ?? "No asset source is installed in this application.";

    private static IRuntimeAssetSource RequireSource()
        => Source ?? throw new InvalidOperationException("Asset I/O requires an explicitly installed asset source.");

    public static byte[] ReadAllBytes(string filePath) => RequireSource().ReadAllBytes(filePath);
    public static Task<byte[]> ReadAllBytesAsync(string filePath, CancellationToken cancellationToken = default)
        => RequireSource().ReadAllBytesAsync(filePath, cancellationToken);
    public static byte[] ReadRange(string filePath, long offset, int length)
        => RequireSource().ReadRange(filePath, offset, length);
    public static Task<byte[]> ReadRangeAsync(string filePath, long offset, int length, CancellationToken cancellationToken = default)
        => RequireSource().ReadRangeAsync(filePath, offset, length, cancellationToken);
    public static unsafe bool TryReadInto(string filePath, long offset, int length, void* destination, CancellationToken cancellationToken = default)
        => RequireSource().TryReadInto(filePath, offset, length, destination, cancellationToken);
    public static unsafe bool TryReadFileInto(string filePath, void* destination, int destinationSize, CancellationToken cancellationToken = default)
        => RequireSource().TryReadFileInto(filePath, destination, destinationSize, cancellationToken);

    /// <summary>Preserves the batch API while the installed source owns submission and lifetime.</summary>
    public sealed class ReadBatch : IAssetReadBatch
    {
        private readonly IAssetReadBatch _batch = RequireSource().CreateBatch();
        public int Count => _batch.Count;
        public int AddFile(string filePath) => _batch.AddFile(filePath);
        public int AddRange(string filePath, long offset, int length) => _batch.AddRange(filePath, offset, length);
        public bool Execute(CancellationToken cancellationToken = default) => _batch.Execute(cancellationToken);
        public Task<bool> ExecuteAsync(CancellationToken cancellationToken = default) => _batch.ExecuteAsync(cancellationToken);
        public byte[] GetResult(int index) => _batch.GetResult(index);
        public bool TryGetResult(int index, out byte[]? data) => _batch.TryGetResult(index, out data);
        public void Dispose() => _batch.Dispose();
    }
}

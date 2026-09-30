namespace XREngine.Core.Files;

/// <summary>A batch owned by its asset source; results remain available until disposal.</summary>
public interface IAssetReadBatch : IDisposable
{
    int Count { get; }
    int AddFile(string path);
    int AddRange(string path, long offset, int length);
    bool Execute(CancellationToken cancellationToken = default);
    Task<bool> ExecuteAsync(CancellationToken cancellationToken = default);
    byte[] GetResult(int index);
    bool TryGetResult(int index, out byte[]? data);
}

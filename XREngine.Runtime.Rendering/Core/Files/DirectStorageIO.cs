namespace XREngine.Core.Files;

/// <summary>Routes rendering asset reads through the host's installed asset source.</summary>
public static class RuntimeDirectStorageIO
{
    public static bool IsEnabled => DirectStorageIO.IsEnabled;
    public static string Status => DirectStorageIO.Status;
    public static byte[] ReadAllBytes(string filePath) => DirectStorageIO.ReadAllBytes(filePath);
    public static Task<byte[]> ReadAllBytesAsync(string filePath, CancellationToken cancellationToken = default)
        => DirectStorageIO.ReadAllBytesAsync(filePath, cancellationToken);
    public static unsafe bool TryReadInto(string filePath, long offset, int length, void* destination, CancellationToken cancellationToken = default)
        => DirectStorageIO.TryReadInto(filePath, offset, length, destination, cancellationToken);
}

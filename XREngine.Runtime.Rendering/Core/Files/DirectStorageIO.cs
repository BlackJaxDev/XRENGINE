using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Core.Files;

public static class DirectStorageIO
{
    public static bool IsEnabled => false;
    public static string Status => "DirectStorage host service is not configured for the runtime rendering assembly.";

    public static byte[] ReadAllBytes(string filePath)
        => File.ReadAllBytes(filePath);

    public static async Task<byte[]> ReadAllBytesAsync(string filePath, CancellationToken cancellationToken = default)
        => await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);

    public static unsafe bool TryReadInto(string filePath, long offset, int length, void* destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (length == 0)
            return false;

        using FileStream stream = File.OpenRead(filePath);
        stream.Seek(offset, SeekOrigin.Begin);

        Span<byte> target = new(destination, length);
        stream.ReadExactly(target);
        return false;
    }
}
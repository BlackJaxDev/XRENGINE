using System.Security.Cryptography;
using XREngine.Rendering;

namespace XREngine.RenderBench;

/// <summary>
/// Cold deterministic resident payload for real upload/chunking checks. Every
/// row, column and mip contributes to the expected native-content digest.
/// This is synthetic upload evidence, not a disk-decode benchmark.
/// </summary>
internal sealed class RenderBenchTextureStreamingFixture : IDisposable
{
    private bool _disposed;

    public Mipmap2D[] Mipmaps { get; }
    public string[] ExpectedMipSha256 { get; }
    public long ByteCount { get; }

    public RenderBenchTextureStreamingFixture(uint dimension, int patternSeed)
    {
        if (dimension == 0 || dimension > 4096 || (dimension & (dimension - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(dimension), "The upload fixture requires a power of two no larger than 4096.");
        List<Mipmap2D> mipmaps = [];
        List<string> digests = [];
        try
        {
            for (uint size = dimension, level = 0; size != 0; size >>= 1, level++)
            {
                byte[] pixels = new byte[checked((int)(size * size * 4))];
                for (uint y = 0; y < size; y++)
                for (uint x = 0; x < size; x++)
                {
                    int offset = checked((int)((y * size + x) * 4));
                    pixels[offset] = unchecked((byte)(x + (uint)patternSeed * 31 + level * 17));
                    pixels[offset + 1] = unchecked((byte)(y + (uint)patternSeed * 47 + level * 23));
                    pixels[offset + 2] = unchecked((byte)((x ^ y) + level * 13 + (uint)patternSeed * 7));
                    pixels[offset + 3] = 255;
                }
                digests.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                mipmaps.Add(new Mipmap2D(size, size, pixels));
                ByteCount += pixels.Length;
            }
            Mipmaps = [.. mipmaps];
            ExpectedMipSha256 = [.. digests];
        }
        catch
        {
            foreach (Mipmap2D mipmap in mipmaps)
                mipmap.Data?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (Mipmap2D mipmap in Mipmaps)
            mipmap.Data?.Dispose();
    }
}

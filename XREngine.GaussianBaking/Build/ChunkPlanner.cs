using System.Security.Cryptography;
using XREngine.Rendering.Models.Gaussian.Baking.Encoding;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Build;

/// <summary>
/// Puts sections into chunks in stream order. Each chunk holds the sections of one unit (one kind and one level),
/// and no chunk holds more than the data limit.
/// </summary>
/// <remarks>
/// A unit starts a new chunk. A unit that is too large for one chunk continues in the next chunks with the same chunk
/// information. The caller splits a section that does not fit (see <see cref="AppendSplit"/>).
/// </remarks>
internal sealed class ChunkPlanner(int maxChunkDataBytes)
{
    private readonly List<(GaussianCacheChunkInfo Info, byte[] Data)> _chunks = [];
    private GaussianCacheChunkWriter? _current;
    private GaussianCacheChunkKind _kind;
    private byte _splatLevel;
    private byte _driverLevel;
    private byte _temporalLevel;

    /// <summary>The largest number of data bytes in one chunk.</summary>
    public int MaxChunkDataBytes { get; } = maxChunkDataBytes;

    /// <summary>The closed chunks, in order.</summary>
    public IReadOnlyList<(GaussianCacheChunkInfo Info, byte[] Data)> Chunks => _chunks;

    /// <summary>True when the open chunk has no section.</summary>
    public bool CurrentIsEmpty => _current is null || _current.Length == 0;

    /// <summary>Closes the open chunk and starts a unit. The next section goes into a new chunk.</summary>
    public void BeginUnit(GaussianCacheChunkKind kind, int splatLevel, int driverLevel, int temporalLevel)
    {
        Close();
        _kind = kind;
        _splatLevel = checked((byte)splatLevel);
        _driverLevel = checked((byte)driverLevel);
        _temporalLevel = checked((byte)temporalLevel);
    }

    /// <summary>Returns true when the section fits in the open chunk.</summary>
    public bool Fits(in GaussianCacheSection section)
        => (_current?.Length ?? 0) + (long)GaussianCacheSection.HeaderSize + section.Payload.Length <= MaxChunkDataBytes;

    public void Append(in GaussianCacheSection section)
    {
        if (!Fits(section))
            throw new InvalidOperationException("The section does not fit in the open chunk.");
        _current ??= new GaussianCacheChunkWriter();
        _current.Append(section);
    }

    /// <summary>Closes the open chunk. The next section of the same unit goes into a new chunk.</summary>
    public void StartNextChunk()
        => Close();

    /// <summary>Closes the open chunk and returns all chunks.</summary>
    public IReadOnlyList<(GaussianCacheChunkInfo Info, byte[] Data)> Finish()
    {
        Close();
        return _chunks;
    }

    /// <summary>
    /// Appends items <c>[0, itemCount)</c> as one or more sections. <paramref name="encode"/> makes the section of the
    /// items <c>[first, first + count)</c>. When the remaining items do not fit in the open chunk, the planner finds
    /// the largest count that fits, appends that part, and continues in a new chunk.
    /// </summary>
    /// <returns>The appended parts, in order.</returns>
    /// <exception cref="GaussianBakeException">One item does not fit in an empty chunk.</exception>
    public List<(int First, int Count, GaussianCacheSection Section)> AppendSplit(int itemCount, Func<int, int, GaussianCacheSection> encode, string what)
    {
        List<(int, int, GaussianCacheSection)> parts = [];
        int first = 0;
        while (first < itemCount)
        {
            int remaining = itemCount - first;
            GaussianCacheSection whole = encode(first, remaining);
            if (Fits(whole))
            {
                Append(whole);
                parts.Add((first, remaining, whole));
                return parts;
            }

            // The encoded size grows with the item count, so a binary search finds the largest part that fits.
            GaussianCacheSection fitting = default;
            int fittingCount = 0;
            int low = 1;
            int high = remaining - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                GaussianCacheSection candidate = encode(first, middle);
                if (Fits(candidate))
                {
                    fitting = candidate;
                    fittingCount = middle;
                    low = middle + 1;
                }
                else
                    high = middle - 1;
            }

            if (fittingCount == 0)
            {
                if (CurrentIsEmpty)
                    throw new GaussianBakeException(
                        GaussianBakeFailureCodes.EncodeFailed,
                        $"One item of {what} is larger than the chunk data limit of {MaxChunkDataBytes} bytes.");
                StartNextChunk();
                continue;
            }

            Append(fitting);
            parts.Add((first, fittingCount, fitting));
            first += fittingCount;
            StartNextChunk();
        }

        return parts;
    }

    private void Close()
    {
        if (_current is null || _current.Length == 0)
            return;

        byte[] data = _current.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        _chunks.Add((new GaussianCacheChunkInfo(_chunks.Count, _kind, _splatLevel, _driverLevel, _temporalLevel, data.Length, hash), data));
        _current = null;
    }
}

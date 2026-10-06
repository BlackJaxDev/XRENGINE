using XREngine.Data;

namespace XREngine.Core.Files;

/// <summary>
/// A long-lived, read-only view of one cooked asset archive. The handle maps the file once and
/// parses the header, footer, string dictionary, and table of contents once. Lookups are
/// thread-safe and allocation-free after construction for every lookup mode: hash buckets,
/// sorted-by-hash, and linear. Use after disposal raises <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed unsafe class PublishedArchiveHandle : IDisposable
{
    private FileMap? _map;
    private readonly byte* _base;
    private readonly long _length;
    private readonly uint[] _hashes;
    private readonly string[] _paths;
    private readonly long[] _dataOffsets;
    private readonly int[] _compressedSizes;
    private readonly long[] _uncompressedSizes;
    private readonly ulong[] _contentHashes;
    private readonly long[] _sourceTimestamps;
    private readonly CompressionCodec[] _codecs;
    private readonly int[]? _bucketStarts;
    private readonly int[]? _bucketCounts;
    private int _disposed;
    private readonly object _lifetimeGate = new();
    private int _payloadReferences;

    private PublishedArchiveHandle(string filePath, FileMap map)
    {
        FilePath = filePath;
        _map = map;
        _base = (byte*)map.Address;
        _length = map.Length;

        using CookedBinaryReader reader = new(_base, _length);
        if (reader.ReadInt32() != AssetPacker.Magic)
            throw new InvalidDataException($"'{filePath}' is not a cooked asset archive.");

        int version = reader.ReadInt32();
        if (version != AssetPacker.CurrentVersion)
            throw new InvalidDataException(AssetPacker.DescribeUnsupportedVersion(filePath, version));

        Flags = (ArchiveFlags)reader.ReadInt32();
        LookupMode = (TocLookupMode)reader.ReadInt32();
        int count = reader.ReadInt32();
        BuildTimestampUtcTicks = reader.ReadInt64();
        DeadBytes = reader.ReadInt64();
        if (count < 0 || count > _length / 16)
            throw new InvalidDataException("Archive entry count exceeds its file bounds.");
        AssetPacker.FooterInfo footer = AssetPacker.ReadFooter(reader);

        reader.Position = AssetPacker.ResolveDictionaryOffset(footer);
        AssetPacker.StringCompressor strings = new(reader);

        _hashes = new uint[count];
        _paths = new string[count];
        _dataOffsets = new long[count];
        _compressedSizes = new int[count];
        _uncompressedSizes = new long[count];
        _contentHashes = new ulong[count];
        _sourceTimestamps = new long[count];
        _codecs = new CompressionCodec[count];

        reader.Position = footer.TocPosition;
        for (int i = 0; i < count; i++)
        {
            AssetPacker.TocEntryData entry = AssetPacker.ReadSequentialTocEntry(reader);
            if (entry.DataOffset < 0 || entry.CompressedSize < 0 || entry.DataOffset > _length - entry.CompressedSize)
                throw new InvalidDataException($"'{filePath}' has a table-of-contents entry outside the file.");

            _hashes[i] = entry.Hash;
            _paths[i] = AssetPacker.NormalizePath(strings.GetString(entry.StringOffset));
            _dataOffsets[i] = entry.DataOffset;
            _compressedSizes[i] = entry.CompressedSize;
            _uncompressedSizes[i] = entry.UncompressedSize;
            _contentHashes[i] = entry.ContentHash;
            _sourceTimestamps[i] = entry.SourceTimestampUtcTicks;
            _codecs[i] = entry.Codec;
        }

        if (LookupMode == TocLookupMode.HashBuckets && footer.IndexTableOffset != 0)
        {
            reader.Position = footer.IndexTableOffset;
            int bucketCount = reader.ReadInt32();
            if (bucketCount > 0 && bucketCount <= _length / 8 && (bucketCount & (bucketCount - 1)) == 0)
            {
                _bucketStarts = new int[bucketCount];
                _bucketCounts = new int[bucketCount];
                for (int i = 0; i < bucketCount; i++)
                {
                    _bucketStarts[i] = reader.ReadInt32();
                    _bucketCounts[i] = reader.ReadInt32();
                }
            }
        }

        TocOffset = footer.TocPosition;
        StringTableOffset = footer.StringTableOffset;
        IndexTableOffset = footer.IndexTableOffset;
    }

    /// <summary>Maps the archive and parses its tables. Throws an actionable diagnostic for a stale archive version.</summary>
    public static PublishedArchiveHandle Open(string archiveFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveFilePath);
        string fullPath = Path.GetFullPath(archiveFilePath);
        RuntimeAssetReadServices.EnsureHostFileAccess("Archive mapping");
        // FileMap creates a missing file, so reject a missing archive before mapping.
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Archive '{fullPath}' not found.", fullPath);

        FileMap map = FileMap.FromFile(fullPath, FileMapProtect.Read);
        try
        {
            return new PublishedArchiveHandle(fullPath, map);
        }
        catch
        {
            map.Dispose();
            throw;
        }
    }

    public string FilePath { get; }
    public ArchiveFlags Flags { get; }
    public TocLookupMode LookupMode { get; }
    public long BuildTimestampUtcTicks { get; }
    public long DeadBytes { get; }
    public long TocOffset { get; }
    public long StringTableOffset { get; }
    public long IndexTableOffset { get; }
    public long FileSize => _length;
    public int EntryCount => _paths.Length;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Finds the table-of-contents index for an asset path using the archive's lookup mode.</summary>
    public bool TryFindEntry(string assetPath, out int entryIndex)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(assetPath);

        string normalized = AssetPacker.NormalizePath(assetPath);
        uint hash = AssetPacker.FastHash(normalized);

        if (_bucketStarts is not null && _bucketCounts is not null)
        {
            int bucket = (int)(hash & (uint)(_bucketStarts.Length - 1));
            int start = _bucketStarts[bucket];
            long endLong = (long)start + _bucketCounts[bucket];
            int end = endLong <= int.MaxValue ? (int)endLong : -1;
            if (start < 0 || end < start || end > _paths.Length)
                return TryFindLinear(normalized, hash, out entryIndex);

            for (int i = start; i < end; i++)
            {
                if (_hashes[i] == hash && string.Equals(_paths[i], normalized, StringComparison.Ordinal))
                {
                    entryIndex = i;
                    return true;
                }
            }

            entryIndex = -1;
            return false;
        }

        if (LookupMode == TocLookupMode.SortedByHash)
            return TryFindSorted(normalized, hash, out entryIndex);

        return TryFindLinear(normalized, hash, out entryIndex);
    }

    public bool Contains(string assetPath)
        => TryFindEntry(assetPath, out _);

    /// <summary>Returns the entry metadata at a table-of-contents index.</summary>
    public AssetPacker.ArchiveEntryInfo GetEntry(int entryIndex)
    {
        ThrowIfDisposed();
        if ((uint)entryIndex >= (uint)_paths.Length)
            throw new ArgumentOutOfRangeException(nameof(entryIndex));

        return new AssetPacker.ArchiveEntryInfo(
            _paths[entryIndex],
            _hashes[entryIndex],
            _dataOffsets[entryIndex],
            _compressedSizes[entryIndex],
            _uncompressedSizes[entryIndex],
            _contentHashes[entryIndex],
            _sourceTimestamps[entryIndex],
            _codecs[entryIndex]);
    }

    /// <summary>Copies every asset path in table-of-contents order.</summary>
    public IReadOnlyList<string> GetAssetPaths()
    {
        ThrowIfDisposed();
        return (string[])_paths.Clone();
    }

    /// <summary>
    /// Reads an asset without a transient copy. Stored entries return a span over the mapping;
    /// compressed entries decompress into lease-owned storage.
    /// </summary>
    public bool TryReadAsset(string assetPath, out CookedPayloadLease lease)
    {
        if (!TryFindEntry(assetPath, out int index))
        {
            lease = default;
            return false;
        }

        lease = ReadEntry(index);
        return true;
    }

    /// <summary>Reads an asset or throws <see cref="FileNotFoundException"/> naming the archive and asset.</summary>
    public CookedPayloadLease ReadAsset(string assetPath)
    {
        if (!TryFindEntry(assetPath, out int index))
            throw new FileNotFoundException($"Asset '{assetPath}' was not found in archive '{FilePath}'.", assetPath);

        return ReadEntry(index);
    }

    /// <summary>Reads an entry by index into a lease.</summary>
    public CookedPayloadLease ReadEntry(int entryIndex)
    {
        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            if ((uint)entryIndex >= (uint)_paths.Length)
                throw new ArgumentOutOfRangeException(nameof(entryIndex));
            _payloadReferences++;
        }
        bool retained = false;
        try
        {
            ReadOnlySpan<byte> stored = new(_base + _dataOffsets[entryIndex], _compressedSizes[entryIndex]);
            CompressionCodec codec = _codecs[entryIndex];
            long uncompressed = _uncompressedSizes[entryIndex];
            if (uncompressed < 0 || uncompressed > int.MaxValue)
                throw new InvalidDataException($"Archive entry '{_paths[entryIndex]}' declares an unsupported uncompressed size ({uncompressed}).");
            if (codec == CompressionCodec.Stored)
            {
                if (uncompressed != stored.Length)
                    throw new InvalidDataException($"Stored archive entry '{_paths[entryIndex]}' has inconsistent payload lengths.");
                CookedPayloadLease mapped = CookedPayloadLease.Mapped(this, _base + _dataOffsets[entryIndex], stored.Length);
                retained = true;
                return mapped;
            }
            CookedPayloadLease lease = CookedPayloadBufferPool.Rent((int)uncompressed);
            try
            {
                int written = Compression.Decompress(stored, codec, lease.WritableSpan);
                if (written != (int)uncompressed)
                    throw new InvalidDataException($"Archive entry '{_paths[entryIndex]}' decompressed to {written} bytes but declares {uncompressed}.");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        finally
        {
            if (!retained) ReleasePayloadReference();
        }
    }

    internal void ReleasePayloadReference()
    {
        lock (_lifetimeGate)
        {
            if (--_payloadReferences < 0)
                throw new InvalidOperationException("Archive payload ownership was released more than once.");
            if (_payloadReferences == 0 && IsDisposed)
            {
                _map?.Dispose();
                _map = null;
            }
        }
    }

    /// <summary>Reads an asset into a heap owner that may cross a job boundary.</summary>
    public CookedPayloadOwner ReadAssetOwned(string assetPath)
    {
        CookedPayloadLease lease = ReadAsset(assetPath);
        return lease.TransferToOwner();
    }

    /// <summary>Copies an asset into a new array. Intended for tooling, never for the runtime load path.</summary>
    public byte[] ReadAssetBytes(string assetPath)
    {
        CookedPayloadLease lease = ReadAsset(assetPath);
        try
        {
            return lease.Span.ToArray();
        }
        finally
        {
            lease.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_payloadReferences == 0)
            {
                _map?.Dispose();
                _map = null;
            }
        }
    }

    private bool TryFindSorted(string normalized, uint hash, out int entryIndex)
    {
        int left = 0;
        int right = _hashes.Length - 1;
        while (left <= right)
        {
            int mid = left + ((right - left) >> 1);
            uint midHash = _hashes[mid];
            if (midHash == hash)
            {
                int first = mid;
                while (first > 0 && _hashes[first - 1] == hash)
                    first--;
                int last = mid;
                while (last < _hashes.Length - 1 && _hashes[last + 1] == hash)
                    last++;
                for (int i = first; i <= last; i++)
                {
                    if (string.Equals(_paths[i], normalized, StringComparison.Ordinal))
                    {
                        entryIndex = i;
                        return true;
                    }
                }

                entryIndex = -1;
                return false;
            }

            if (midHash < hash)
                left = mid + 1;
            else
                right = mid - 1;
        }

        entryIndex = -1;
        return false;
    }

    private bool TryFindLinear(string normalized, uint hash, out int entryIndex)
    {
        for (int i = 0; i < _hashes.Length; i++)
        {
            if (_hashes[i] == hash && string.Equals(_paths[i], normalized, StringComparison.Ordinal))
            {
                entryIndex = i;
                return true;
            }
        }

        entryIndex = -1;
        return false;
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(PublishedArchiveHandle), $"The archive handle for '{FilePath}' was disposed. Published archives stay open for the life of their content root; reopen through PublishedArchiveRegistry instead of holding a stale handle.");
    }
}

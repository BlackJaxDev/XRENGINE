using MemoryPack;
using System;
using System.Security.Cryptography;
using System.Threading;
using XREngine.Rendering.Meshlets;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Cache;

// Decodes chunk 0 and accepts the later chunks in order. The decode uses the installed meshoptimizer backend.
// One lock serializes the decode, because the asset manager can give the same cache to more than one component.
public sealed partial class GaussianCache
{
    private readonly Lock _decodeLock = new();
    private GaussianCacheRuntimeData? _runtime;
    private int _nextChunkIndex = 1;

    /// <summary>
    /// The decoded data. The first access checks the header, the chunk paths, and the hash of chunk 0, and decodes
    /// chunk 0. Set <see cref="Header"/>, <see cref="RootChunkData"/>, and <see cref="ChunkAssetPaths"/> before the first
    /// access. A reader that can run while another thread accepts a chunk holds <see cref="DecodeLock"/>.
    /// </summary>
    /// <exception cref="GaussianCacheFormatException">The header, a chunk path, or chunk 0 is not valid.</exception>
    [YamlIgnore]
    [MemoryPackIgnore]
    public GaussianCacheRuntimeData Runtime
    {
        get
        {
            GaussianCacheRuntimeData? runtime = Volatile.Read(ref _runtime);
            if (runtime is not null)
                return runtime;

            lock (_decodeLock)
                return _runtime ?? DecodeRoot();
        }
    }

    /// <summary>The index of the chunk that <see cref="AcceptChunk"/> accepts next. Chunk 0 is in the root asset, so the first value is 1.</summary>
    [YamlIgnore]
    [MemoryPackIgnore]
    public int NextChunkIndex
    {
        get => _nextChunkIndex;
        private set => SetField(ref _nextChunkIndex, value);
    }

    /// <summary>True when every chunk of the chunk table is accepted.</summary>
    [YamlIgnore]
    [MemoryPackIgnore]
    public bool IsComplete => NextChunkIndex >= _header.Chunks.Length;

    /// <summary>
    /// The lock of the decode. <see cref="AcceptChunk"/> holds it while it decodes and while it raises the change of
    /// <see cref="NextChunkIndex"/>. Hold it to read <see cref="Runtime"/> when another thread can accept a chunk.
    /// </summary>
    internal Lock DecodeLock => _decodeLock;

    /// <summary>
    /// Gets the index and the asset path of the chunk that <see cref="AcceptChunk"/> accepts next, in one step under the
    /// decode lock.
    /// </summary>
    /// <param name="index">The index of the next chunk.</param>
    /// <param name="assetPath">The asset path of the next chunk, relative to the folder of the root asset.</param>
    /// <returns>False when every chunk is accepted.</returns>
    /// <exception cref="GaussianCacheFormatException">The header, a chunk path, or chunk 0 is not valid.</exception>
    public bool TryGetNextChunk(out int index, out string assetPath)
    {
        lock (_decodeLock)
        {
            _ = _runtime ?? DecodeRoot();
            index = NextChunkIndex;
            if (index >= _header.Chunks.Length)
            {
                assetPath = string.Empty;
                return false;
            }

            assetPath = _chunkAssetPaths[index - 1];
            return true;
        }
    }

    /// <summary>
    /// Checks a chunk against the chunk table and decodes it into <see cref="Runtime"/>. More than one thread can call
    /// this method: the decode lock lets one decode run at a time. A chunk that is accepted already does not decode
    /// again, so two loaders of one cache can give the same chunk.
    /// </summary>
    /// <returns>True when this call decoded the chunk. False when the chunk was accepted before.</returns>
    /// <exception cref="GaussianCacheFormatException">
    /// The chunk is not in the chunk table or is after the next chunk
    /// (<see cref="GaussianCacheFormatException.ChunkOutOfOrderCode"/>), its SHA-256 is not the hash in the chunk table
    /// (<see cref="GaussianCacheFormatException.HashMismatchCode"/>), or its data is not valid. After a data error the
    /// runtime data can hold part of the chunk.
    /// </exception>
    public bool AcceptChunk(GaussianCacheChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        lock (_decodeLock)
        {
            GaussianCacheRuntimeData runtime = _runtime ?? DecodeRoot();
            GaussianCacheChunkInfo[] chunks = _header.Chunks;
            if (chunk.Index <= 0 || chunk.Index >= chunks.Length)
                throw new GaussianCacheFormatException(
                    GaussianCacheFormatException.ChunkOutOfOrderCode,
                    $"Chunk {chunk.Index} arrived, but the chunk table has chunks 1 to {chunks.Length - 1}.");
            if (chunk.Index > NextChunkIndex)
                throw new GaussianCacheFormatException(
                    GaussianCacheFormatException.ChunkOutOfOrderCode,
                    $"Chunk {chunk.Index} arrived, but the next chunk is {NextChunkIndex}.");

            VerifyHash(chunk.Data, chunks[chunk.Index]);
            if (chunk.Index < NextChunkIndex)
                return false;

            runtime.ApplyChunk(chunk.Data, MeshOptimizerBackendServices.Required);
            NextChunkIndex = chunk.Index + 1;
            return true;
        }
    }

    /// <summary>
    /// Returns true when a chunk asset path stays in the folder of the root asset: it is not empty, it is relative, it
    /// has no drive, share, or stream part (no colon), and no segment is empty, <c>.</c>, or <c>..</c>. Both
    /// <c>/</c> and <c>\</c> separate segments.
    /// </summary>
    public static bool IsValidChunkAssetPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains(':') || path[0] is '/' or '\\')
            return false;

        foreach (string segment in path.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }

        return true;
    }

    private GaussianCacheRuntimeData DecodeRoot()
    {
        _header.Validate();
        GaussianCacheChunkInfo[] chunks = _header.Chunks;
        if (chunks.Length == 0 || chunks[0].Kind != GaussianCacheChunkKind.Root)
            throw Malformed("The chunk table has no root chunk.");
        for (int index = 0; index < chunks.Length; index++)
        {
            if (chunks[index].Index != index)
                throw Malformed($"Entry {index} of the chunk table has the index {chunks[index].Index}.");
        }

        if (_chunkAssetPaths.Length != chunks.Length - 1)
            throw Malformed($"The cache has {_chunkAssetPaths.Length} chunk paths for {chunks.Length - 1} chunks.");
        for (int index = 0; index < _chunkAssetPaths.Length; index++)
        {
            if (!IsValidChunkAssetPath(_chunkAssetPaths[index]))
                throw new GaussianCacheFormatException(
                    GaussianCacheFormatException.InvalidChunkPathCode,
                    $"The asset path of chunk {index + 1} is not a relative path in the folder of the root asset: '{_chunkAssetPaths[index]}'.");
        }

        VerifyHash(_rootChunkData, chunks[0]);
        GaussianCacheRuntimeData runtime = GaussianCacheRuntimeData.Create(_header);
        runtime.ApplyChunk(_rootChunkData, MeshOptimizerBackendServices.Required);
        SetField(ref _runtime, runtime, nameof(Runtime));
        NextChunkIndex = 1;
        return runtime;
    }

    private static void VerifyHash(byte[] data, GaussianCacheChunkInfo info)
    {
        string actual = Convert.ToHexStringLower(SHA256.HashData(data));
        if (data.Length != info.ByteLength || !string.Equals(actual, info.Sha256, StringComparison.Ordinal))
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.HashMismatchCode,
                $"Chunk {info.Index} has {data.Length} bytes with SHA-256 {actual}, but the chunk table gives {info.ByteLength} bytes with SHA-256 {info.Sha256}.");
    }

    private static GaussianCacheFormatException Malformed(string message)
        => new(GaussianCacheFormatException.MalformedCode, message);
}

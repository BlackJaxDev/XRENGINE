using MemoryPack;
using System;
using System.Security.Cryptography;
using XREngine.Rendering.Meshlets;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Cache;

// Decodes chunk 0 and accepts the later chunks in order. The decode uses the installed meshoptimizer backend.
public sealed partial class GaussianCache
{
    private GaussianCacheRuntimeData? _runtime;
    private int _nextChunkIndex = 1;

    /// <summary>
    /// The decoded data. The first access checks the header and the hash of chunk 0, and decodes chunk 0. Set
    /// <see cref="Header"/> and <see cref="RootChunkData"/> before the first access.
    /// </summary>
    /// <exception cref="GaussianCacheFormatException">The header or chunk 0 is not valid.</exception>
    [YamlIgnore]
    [MemoryPackIgnore]
    public GaussianCacheRuntimeData Runtime => _runtime ?? DecodeRoot();

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
    /// Checks a chunk against the chunk table and decodes it into <see cref="Runtime"/>. Do not call this method from
    /// more than one thread at the same time.
    /// </summary>
    /// <exception cref="GaussianCacheFormatException">
    /// The chunk is not the next chunk (<see cref="GaussianCacheFormatException.ChunkOutOfOrderCode"/>), its SHA-256 is
    /// not the hash in the chunk table (<see cref="GaussianCacheFormatException.HashMismatchCode"/>), or its data is not
    /// valid. After a data error the runtime data can hold part of the chunk.
    /// </exception>
    public void AcceptChunk(GaussianCacheChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        GaussianCacheRuntimeData runtime = Runtime;
        GaussianCacheChunkInfo[] chunks = _header.Chunks;
        if (NextChunkIndex >= chunks.Length)
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.ChunkOutOfOrderCode,
                $"Chunk {chunk.Index} arrived, but all {chunks.Length} chunks are accepted.");
        if (chunk.Index != NextChunkIndex)
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.ChunkOutOfOrderCode,
                $"Chunk {chunk.Index} arrived, but the next chunk is {NextChunkIndex}.");

        VerifyHash(chunk.Data, chunks[chunk.Index]);
        runtime.ApplyChunk(chunk.Data, MeshOptimizerBackendServices.Required);
        NextChunkIndex = chunk.Index + 1;
    }

    private GaussianCacheRuntimeData DecodeRoot()
    {
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

using System.Security.Cryptography;
using XREngine.Core.Files;
using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Cache;

namespace XREngine.Rendering.Models.Gaussian.Baking.Output;

/// <summary>
/// Writes a built cache as cooked asset files: <c>cache/root.asset</c>, which holds the header and chunk 0, and one
/// <c>cache/chunk_XXXX.asset</c> file for each chunk after it.
/// </summary>
/// <remarks>
/// <para>Each file is a cooked asset envelope in the format <see cref="CookedAssetFormat.RuntimeBinaryV1"/>, with the
/// payload of the registered published codec of the asset type (the runtime cooked binary serializer).</para>
/// <para>A new asset gets a random ID, and the cooked payload holds that ID. So that the same bake always writes the
/// same bytes, the writer replaces the ID in the payload with an ID that it makes from the content hash of the cache
/// and the chunk index. The in-memory assets keep their IDs.</para>
/// </remarks>
public static class BakeCacheFiles
{
    /// <summary>The name of the cache folder in the output folder.</summary>
    public const string FolderName = "cache";

    /// <summary>The file name of the root asset in the cache folder.</summary>
    public const string RootFileName = "root.asset";

    private const int GuidByteCount = 16;

    /// <summary>Writes the cache files into <c>&lt;outputDirectory&gt;/cache/</c>, and returns the full path of that folder.</summary>
    /// <param name="build">The built cache.</param>
    /// <param name="outputDirectory">The folder that receives the cache folder.</param>
    /// <exception cref="GaussianBakeException">
    /// The files cannot be written, or the payload of an asset does not hold its ID one time
    /// (<see cref="GaussianBakeFailureCodes.EncodeFailed"/>).
    /// </exception>
    public static string Write(GaussianCacheBuildResult build, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string folder = Path.Combine(outputDirectory, FolderName);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, RootFileName), Cook(build.Root, StableId(build.ContentHash, 0)));

            string[] paths = build.Root.ChunkAssetPaths;
            if (paths.Length != build.Chunks.Length)
                throw new GaussianBakeException(GaussianBakeFailureCodes.EncodeFailed, $"The cache has {build.Chunks.Length} chunks but {paths.Length} chunk paths.");

            for (int i = 0; i < build.Chunks.Length; i++)
                File.WriteAllBytes(Path.Combine(folder, paths[i]), Cook(build.Chunks[i], StableId(build.ContentHash, build.Chunks[i].Index)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GaussianBakeException(GaussianBakeFailureCodes.EncodeFailed, $"The cache files cannot be written to '{folder}': {ex.Message}");
        }

        return folder;
    }

    /// <summary>
    /// Reads the cache files back through the runtime path, and checks that they hold the built cache. The runtime reads
    /// a cooked cache asset with <see cref="PublishedCookedAssetReader"/>, and accepts the chunks in order with
    /// <see cref="GaussianCacheChunkLoader.ResolveChunkPath"/> and <see cref="GaussianCache.AcceptChunk"/>. The check
    /// compares the header bytes, the data of chunk 0, the chunk paths, and the index and data of each chunk with the
    /// built cache, and decodes every chunk.
    /// </summary>
    /// <param name="build">The built cache.</param>
    /// <param name="outputDirectory">The folder that holds the cache folder.</param>
    /// <exception cref="GaussianBakeException">
    /// The files cannot be read, do not decode, or do not hold the built cache
    /// (<see cref="GaussianBakeFailureCodes.EncodeFailed"/>).
    /// </exception>
    public static void VerifyReadBack(GaussianCacheBuildResult build, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string folder = Path.Combine(outputDirectory, FolderName);
        string rootPath = Path.Combine(folder, RootFileName);
        try
        {
            GaussianCache cache = PublishedCookedAssetReader.LoadAsset<GaussianCache>(File.ReadAllBytes(rootPath))
                ?? throw ReadBackFailed(rootPath, "the file is not a gaussian cache asset");
            if (!HeaderBytes(cache.Header).AsSpan().SequenceEqual(HeaderBytes(build.Root.Header)))
                throw ReadBackFailed(rootPath, "the header is not the built header");
            if (!cache.RootChunkData.AsSpan().SequenceEqual(build.Root.RootChunkData))
                throw ReadBackFailed(rootPath, "chunk 0 is not the built chunk 0");
            if (!cache.ChunkAssetPaths.SequenceEqual(build.Root.ChunkAssetPaths, StringComparer.Ordinal))
                throw ReadBackFailed(rootPath, "the chunk paths are not the built chunk paths");

            for (int i = 0; i < build.Chunks.Length; i++)
            {
                string chunkPath = GaussianCacheChunkLoader.ResolveChunkPath(rootPath, cache.ChunkAssetPaths[i]);
                GaussianCacheChunk chunk = PublishedCookedAssetReader.LoadAsset<GaussianCacheChunk>(File.ReadAllBytes(chunkPath))
                    ?? throw ReadBackFailed(chunkPath, "the file is not a gaussian cache chunk asset");
                if (chunk.Index != build.Chunks[i].Index || !chunk.Data.AsSpan().SequenceEqual(build.Chunks[i].Data))
                    throw ReadBackFailed(chunkPath, $"the chunk is not built chunk {build.Chunks[i].Index}");
                if (!cache.AcceptChunk(chunk))
                    throw ReadBackFailed(chunkPath, $"chunk {chunk.Index} was accepted before");
            }

            if (!cache.IsComplete)
                throw ReadBackFailed(rootPath, "the cache is not complete after its last chunk");
        }
        catch (Exception ex) when (ex is not GaussianBakeException and not OutOfMemoryException)
        {
            throw ReadBackFailed(rootPath, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Returns the ID of the cache asset with the given index: the first 16 bytes of SHA-256 of the content hash and the index.</summary>
    /// <param name="contentHash">The content hash of the cache.</param>
    /// <param name="index">0 for the root asset, otherwise the chunk index.</param>
    public static Guid StableId(string contentHash, int index)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"gaussian-cache:{contentHash}:{index}"));
        return new Guid(hash.AsSpan(0, GuidByteCount));
    }

#pragma warning disable IL2026, IL3050 // The cache types use the runtime cooked binary serializer, as their registered codec does.
    private static byte[] Cook(XRAsset asset, Guid stableId)
    {
        byte[] payload = PublishedCookedAssetRegistry.TrySerialize(asset, out byte[] registered)
            ? registered
            : RuntimeCookedBinarySerializer.Serialize(asset);
        ReplaceId(payload, asset.ID, stableId, asset.GetType());
        string typeReference = CookedAssetTypeReference.Encode(asset.GetType());
        return CookedAssetEnvelope.Serialize(new CookedAssetBlob(typeReference, CookedAssetFormat.RuntimeBinaryV1, payload));
    }
#pragma warning restore IL2026, IL3050

    private static byte[] HeaderBytes(GaussianCacheHeader header)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            header.Write(writer);
        return stream.ToArray();
    }

    private static GaussianBakeException ReadBackFailed(string path, string reason)
        => new(GaussianBakeFailureCodes.EncodeFailed, $"The cache file '{path}' does not read back through the runtime path: {reason}.");

    /// <summary>
    /// Replaces the bytes of the asset ID in the payload. The bytes must occur exactly one time, so that no other data
    /// changes.
    /// </summary>
    private static void ReplaceId(byte[] payload, Guid currentId, Guid stableId, Type assetType)
    {
        byte[] current = currentId.ToByteArray();
        int first = payload.AsSpan().IndexOf(current);
        int last = payload.AsSpan().LastIndexOf(current);
        if (first < 0 || first != last)
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.EncodeFailed,
                $"The cooked payload of {assetType.Name} holds its ID {(first < 0 ? "zero times" : "more than one time")}, so the ID cannot be made stable.");
        }

        if (!stableId.TryWriteBytes(payload.AsSpan(first, GuidByteCount)))
            throw new GaussianBakeException(GaussianBakeFailureCodes.EncodeFailed, $"The stable ID of {assetType.Name} cannot be written.");
    }
}

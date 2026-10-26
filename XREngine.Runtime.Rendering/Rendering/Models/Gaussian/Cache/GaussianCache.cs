using MemoryPack;
using System;
using XREngine.Core.Files;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>
/// The root asset of an animated gaussian cache. It holds the header, the data of chunk 0,
/// and the asset paths of chunks 1 to n.
/// </summary>
[Serializable]
[MemoryPackable(GenerateType.NoGenerate)]
public sealed partial class GaussianCache : XRAsset
{
    private GaussianCacheHeader _header = new();
    private byte[] _rootChunkData = [];
    private string[] _chunkAssetPaths = [];

    public GaussianCacheHeader Header
    {
        get => _header;
        set => SetField(ref _header, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>The data of chunk 0.</summary>
    public byte[] RootChunkData
    {
        get => _rootChunkData;
        set => SetField(ref _rootChunkData, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>The asset paths of chunks 1 to n, in chunk order.</summary>
    public string[] ChunkAssetPaths
    {
        get => _chunkAssetPaths;
        set => SetField(ref _chunkAssetPaths, value ?? throw new ArgumentNullException(nameof(value)));
    }
}

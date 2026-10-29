using MemoryPack;
using System;
using XREngine.Core.Files;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>One data chunk of a cache, for chunk 1 and above.</summary>
[Serializable]
[MemoryPackable(GenerateType.NoGenerate)]
public sealed partial class GaussianCacheChunk : XRAsset
{
    private int _index;
    private byte[] _data = [];

    /// <summary>The chunk position in the cache.</summary>
    public int Index
    {
        get => _index;
        set => SetField(ref _index, value);
    }

    public byte[] Data
    {
        get => _data;
        set => SetField(ref _data, value ?? throw new ArgumentNullException(nameof(value)));
    }
}

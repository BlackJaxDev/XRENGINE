namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable resource ceilings applied before model-cache allocation or seeking.
/// </summary>
internal sealed class ModelCacheReadLimits
{
    public const ulong HardMaxFileBytes = 64UL * 1024 * 1024 * 1024;
    public const ulong HardMaxStringPoolBytes = 256UL * 1024 * 1024;
    public const int HardMaxStringBytes = 16 * 1024 * 1024;
    public const uint HardMaxStringCount = 4_000_000;
    public const uint HardMaxChunkCount = 1_000_000;
    public const ulong HardMaxChunkBytes = int.MaxValue;
    public const ulong HardMaxAggregateDecodedBytes = 64UL * 1024 * 1024 * 1024;
    public const ulong HardMaxElementCount = int.MaxValue;

    public static ModelCacheReadLimits Default { get; } = new();

    public ModelCacheReadLimits(
        ulong maxFileBytes = 16UL * 1024 * 1024 * 1024,
        ulong maxStringPoolBytes = 64UL * 1024 * 1024,
        int maxStringBytes = 1024 * 1024,
        uint maxStringCount = 262_144,
        uint maxChunkCount = 65_536,
        ulong maxChunkBytes = 1024UL * 1024 * 1024,
        ulong maxAggregateDecodedBytes = 16UL * 1024 * 1024 * 1024,
        ulong maxElementCount = 100_000_000,
        ulong maxNodeCount = 10_000_000,
        ulong maxModelCount = 1_000_000,
        ulong maxSubMeshCount = 10_000_000,
        ulong maxMeshCount = 1_000_000,
        ulong maxVertexCount = 500_000_000,
        ulong maxIndexCount = 1_500_000_000,
        ulong maxBoneCount = 10_000_000,
        ulong maxMorphTargetCount = 10_000_000,
        ulong maxLodCount = 10_000_000,
        ulong maxMeshletCount = 500_000_000)
    {
        ValidateRange(maxFileBytes, 1, HardMaxFileBytes, nameof(maxFileBytes));
        ValidateRange(maxStringPoolBytes, 4, HardMaxStringPoolBytes, nameof(maxStringPoolBytes));
        ValidateRange((ulong)maxStringBytes, 1, HardMaxStringBytes, nameof(maxStringBytes));
        ValidateRange(maxStringCount, 1, HardMaxStringCount, nameof(maxStringCount));
        ValidateRange(maxChunkCount, 1, HardMaxChunkCount, nameof(maxChunkCount));
        ValidateRange(maxChunkBytes, 1, HardMaxChunkBytes, nameof(maxChunkBytes));
        ValidateRange(maxAggregateDecodedBytes, 1, HardMaxAggregateDecodedBytes, nameof(maxAggregateDecodedBytes));
        ValidateRange(maxElementCount, 1, HardMaxElementCount, nameof(maxElementCount));

        MaxFileBytes = maxFileBytes;
        MaxStringPoolBytes = maxStringPoolBytes;
        MaxStringBytes = maxStringBytes;
        MaxStringCount = maxStringCount;
        MaxChunkCount = maxChunkCount;
        MaxChunkBytes = maxChunkBytes;
        MaxAggregateDecodedBytes = maxAggregateDecodedBytes;
        MaxElementCount = maxElementCount;
        MaxNodeCount = ValidateElementLimit(maxNodeCount, maxElementCount, nameof(maxNodeCount));
        MaxModelCount = ValidateElementLimit(maxModelCount, maxElementCount, nameof(maxModelCount));
        MaxSubMeshCount = ValidateElementLimit(maxSubMeshCount, maxElementCount, nameof(maxSubMeshCount));
        MaxMeshCount = ValidateElementLimit(maxMeshCount, maxElementCount, nameof(maxMeshCount));
        MaxVertexCount = ValidateElementLimit(maxVertexCount, HardMaxElementCount, nameof(maxVertexCount));
        MaxIndexCount = ValidateElementLimit(maxIndexCount, HardMaxElementCount, nameof(maxIndexCount));
        MaxBoneCount = ValidateElementLimit(maxBoneCount, maxElementCount, nameof(maxBoneCount));
        MaxMorphTargetCount = ValidateElementLimit(maxMorphTargetCount, maxElementCount, nameof(maxMorphTargetCount));
        MaxLodCount = ValidateElementLimit(maxLodCount, maxElementCount, nameof(maxLodCount));
        MaxMeshletCount = ValidateElementLimit(maxMeshletCount, HardMaxElementCount, nameof(maxMeshletCount));
    }

    public ulong MaxFileBytes { get; }
    public ulong MaxStringPoolBytes { get; }
    public int MaxStringBytes { get; }
    public uint MaxStringCount { get; }
    public uint MaxChunkCount { get; }
    public ulong MaxChunkBytes { get; }
    public ulong MaxAggregateDecodedBytes { get; }
    public ulong MaxElementCount { get; }
    public ulong MaxNodeCount { get; }
    public ulong MaxModelCount { get; }
    public ulong MaxSubMeshCount { get; }
    public ulong MaxMeshCount { get; }
    public ulong MaxVertexCount { get; }
    public ulong MaxIndexCount { get; }
    public ulong MaxBoneCount { get; }
    public ulong MaxMorphTargetCount { get; }
    public ulong MaxLodCount { get; }
    public ulong MaxMeshletCount { get; }

    private static ulong ValidateElementLimit(ulong value, ulong maximum, string parameterName)
    {
        ValidateRange(value, 1, maximum, parameterName);
        return value;
    }

    private static void ValidateRange(ulong value, ulong minimum, ulong maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be between {minimum} and {maximum}.");
    }
}

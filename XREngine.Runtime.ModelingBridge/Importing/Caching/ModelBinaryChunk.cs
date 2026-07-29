namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable canonical bytes supplied to the container writer.
/// </summary>
internal sealed class ModelBinaryChunk
{
    private readonly byte[] _decodedBytes;

    public ModelBinaryChunk(
        uint typeId,
        uint version,
        ModelBinaryChunkFlags flags,
        ulong instanceId,
        ReadOnlySpan<byte> decodedBytes,
        ulong elementCount = 0,
        ModelBinaryChunkCodec codec = ModelBinaryChunkCodec.None)
    {
        if (typeId == 0)
            throw new ArgumentOutOfRangeException(nameof(typeId));

        TypeId = typeId;
        Version = version;
        Flags = flags;
        Codec = codec;
        InstanceId = instanceId;
        _decodedBytes = decodedBytes.ToArray();
        ElementCount = elementCount;
    }

    public ModelBinaryChunk(
        ModelBinaryChunkType type,
        ModelBinaryChunkFlags flags,
        ulong instanceId,
        ReadOnlySpan<byte> decodedBytes,
        ulong elementCount = 0)
        : this(
            (uint)type,
            ModelBinaryCacheFormat.GetChunkVersion((uint)type),
            flags,
            instanceId,
            decodedBytes,
            elementCount)
    {
    }

    public uint TypeId { get; }
    public uint Version { get; }
    public ModelBinaryChunkFlags Flags { get; }
    public ModelBinaryChunkCodec Codec { get; }
    public ulong InstanceId { get; }
    public ReadOnlyMemory<byte> DecodedBytes => _decodedBytes;
    public ulong ElementCount { get; }
    public ModelBinaryChunkKey Key => new(TypeId, InstanceId);
}

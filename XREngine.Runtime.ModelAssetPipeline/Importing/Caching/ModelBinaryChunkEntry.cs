namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Validated fixed-size directory record for one chunk.
/// </summary>
internal sealed class ModelBinaryChunkEntry
{
    public ModelBinaryChunkEntry(
        uint typeId,
        uint version,
        ModelBinaryChunkFlags flags,
        ModelBinaryChunkCodec codec,
        ulong instanceId,
        ulong offset,
        ulong storedLength,
        ulong decodedLength,
        ulong decodedChecksum,
        ulong elementCount)
    {
        TypeId = typeId;
        Version = version;
        Flags = flags;
        Codec = codec;
        InstanceId = instanceId;
        Offset = offset;
        StoredLength = storedLength;
        DecodedLength = decodedLength;
        DecodedChecksum = decodedChecksum;
        ElementCount = elementCount;
    }

    public uint TypeId { get; }
    public uint Version { get; }
    public ModelBinaryChunkFlags Flags { get; }
    public ModelBinaryChunkCodec Codec { get; }
    public ulong InstanceId { get; }
    public ulong Offset { get; }
    public ulong StoredLength { get; }
    public ulong DecodedLength { get; }
    public ulong DecodedChecksum { get; }
    public ulong ElementCount { get; }
    public bool IsRequired => (Flags & ModelBinaryChunkFlags.Required) != 0;
    public ModelBinaryChunkKey Key => new(TypeId, InstanceId);
}

namespace XREngine;

public readonly record struct RvcVisibilityPayload(ulong Packed)
{
    private const int FieldBits = 20;
    private const uint FieldMask = (1u << FieldBits) - 1u;

    public uint InstanceId => (uint)(Packed & FieldMask);
    public uint DrawOrMeshletId => (uint)((Packed >> FieldBits) & FieldMask);
    public uint PrimitiveId => (uint)((Packed >> (FieldBits * 2)) & FieldMask);
    public uint Flags => (uint)(Packed >> 60);
    public bool HasOverflow => (Flags & 0x1u) != 0u;

    public static bool TryPack(
        uint instanceId,
        uint drawOrMeshletId,
        uint primitiveId,
        uint flags,
        out RvcVisibilityPayload payload)
    {
        bool overflow =
            instanceId > FieldMask ||
            drawOrMeshletId > FieldMask ||
            primitiveId > FieldMask ||
            flags > 0xFu;

        uint packedFlags = (flags & 0xFu) | (overflow ? 0x1u : 0u);
        ulong packed =
            ((ulong)(instanceId & FieldMask)) |
            ((ulong)(drawOrMeshletId & FieldMask) << FieldBits) |
            ((ulong)(primitiveId & FieldMask) << (FieldBits * 2)) |
            ((ulong)packedFlags << 60);

        payload = new RvcVisibilityPayload(packed);
        return !overflow;
    }
}

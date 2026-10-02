using MemoryPack;

namespace XREngine;

/// <summary>Key and opaque value carried by property and data replication frames.</summary>
[MemoryPackable]
internal partial record struct IdValue(string key, byte[] value)
{
    public static implicit operator (string idStr, byte[] value)(IdValue value)
        => (value.key, value.value);

    public static implicit operator IdValue((string idStr, byte[] value) value)
        => new(value.idStr, value.value);
}

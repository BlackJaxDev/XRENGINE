namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Small deterministic hash builder for identities that must survive process
/// restarts. Unlike <see cref="HashCode"/>, it has no randomized process seed.
/// </summary>
internal struct VulkanStableHash64
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong _value;

    public VulkanStableHash64(uint schemaVersion)
    {
        _value = OffsetBasis;
        Add(schemaVersion);
    }

    public readonly ulong Value => _value;

    public void Add(bool value)
        => Add(value ? 1u : 0u);

    public void Add(int value)
        => Add(unchecked((uint)value));

    public void Add(uint value)
    {
        Mix((byte)value);
        Mix((byte)(value >> 8));
        Mix((byte)(value >> 16));
        Mix((byte)(value >> 24));
    }

    public void Add(long value)
        => Add(unchecked((ulong)value));

    public void Add(ulong value)
    {
        Mix((byte)value);
        Mix((byte)(value >> 8));
        Mix((byte)(value >> 16));
        Mix((byte)(value >> 24));
        Mix((byte)(value >> 32));
        Mix((byte)(value >> 40));
        Mix((byte)(value >> 48));
        Mix((byte)(value >> 56));
    }

    public void Add(string? value)
    {
        if (value is null)
        {
            Add(uint.MaxValue);
            return;
        }

        Add((uint)value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            Mix((byte)character);
            Mix((byte)(character >> 8));
        }
    }

    private void Mix(byte value)
    {
        _value ^= value;
        _value *= Prime;
    }
}

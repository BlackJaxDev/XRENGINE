namespace XREngine.Rendering;

/// <summary>A generation-checked browser resource reference; the backend also verifies its resource kind.</summary>
public readonly record struct BrowserResourceHandle
{
    public BrowserResourceHandle(int packed)
    {
        if ((packed & 0xFFFF) == 0 || packed <= 0)
            throw new ArgumentOutOfRangeException(nameof(packed), "A resource handle needs a nonzero slot and a generation from 1 through 32767.");

        Packed = packed;
    }

    public int Packed { get; }

    public int Slot => Packed & 0xFFFF;

    public int Generation => Packed >> 16;

    public static BrowserResourceHandle FromPacked(int packed) => new(packed);

    public static BrowserResourceHandle Create(int slot, int generation)
    {
        if (slot is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (generation is < 1 or > 32767)
            throw new ArgumentOutOfRangeException(nameof(generation));

        return new BrowserResourceHandle((generation << 16) | slot);
    }
}

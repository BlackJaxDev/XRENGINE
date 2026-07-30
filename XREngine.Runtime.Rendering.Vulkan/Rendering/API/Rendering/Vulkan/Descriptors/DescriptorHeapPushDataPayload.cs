namespace XREngine.Rendering.Vulkan;

internal sealed class DescriptorHeapPushDataPayload(uint[] dwords)
{
    public static DescriptorHeapPushDataPayload Empty { get; } = new([]);

    public uint[] Dwords { get; } = dwords;

    public void SetDword(uint byteOffset, uint value)
    {
        if (byteOffset == uint.MaxValue)
            return;

        Dwords[checked((int)(byteOffset / sizeof(uint)))] = value;
    }

    public bool IsValidFor(DescriptorHeapProgramLayout layout)
        => Dwords.Length >= layout.PushDwordCount;
}
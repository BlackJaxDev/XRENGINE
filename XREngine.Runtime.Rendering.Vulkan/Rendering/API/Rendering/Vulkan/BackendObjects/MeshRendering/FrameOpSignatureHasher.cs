using System.Collections.Concurrent;

namespace XREngine.Rendering.Vulkan;

internal struct FrameOpSignatureHasher
{
    private const int MaxCachedStringSignatures = 4096;
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;
    private static readonly ConcurrentDictionary<string, ulong> StringSignatures =
        new(ReferenceEqualityComparer.Instance);
    private ulong _value;

    public FrameOpSignatureHasher()
    {
        _value = OffsetBasis;
    }

    public void Add(bool value) => Add(value ? 1 : 0);
    public void Add(int value) => Mix(unchecked((uint)value));
    public void Add(uint value) => Mix(value);
    public void Add(ulong value) => Mix(value);
    public void Add(float value) => Add(BitConverter.SingleToUInt32Bits(value));

    public void Add(string? value)
    {
        if (value is null)
        {
            Add(-1);
            return;
        }

        Add(value.Length);
        Add(GetStableStringSignature(value));
    }

    public ulong ToHash() => _value;

    private static ulong GetStableStringSignature(string value)
    {
        if (StringSignatures.TryGetValue(value, out ulong signature))
            return signature;

        signature = ComputeStableStringSignature(value);
        if (StringSignatures.Count >= MaxCachedStringSignatures)
            StringSignatures.Clear();

        return StringSignatures.GetOrAdd(value, signature);
    }

    private static ulong ComputeStableStringSignature(string value)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(value.Length);
        for (int i = 0; i < value.Length; i++)
            hash.Add((uint)value[i]);

        return hash.ToHash();
    }

    private void Mix(ulong value)
    {
        unchecked
        {
            _value ^= value;
            _value *= Prime;
            _value ^= value >> 32;
            _value *= Prime;
        }
    }
}

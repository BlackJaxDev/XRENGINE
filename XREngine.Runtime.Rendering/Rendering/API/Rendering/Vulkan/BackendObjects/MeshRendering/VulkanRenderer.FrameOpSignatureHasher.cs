namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private struct FrameOpSignatureHasher
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
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
            for (int i = 0; i < value.Length; i++)
                Add(value[i]);
        }

        public ulong ToHash() => _value;

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
}

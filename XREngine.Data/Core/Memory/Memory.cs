using System.Runtime.InteropServices;

namespace XREngine.Data
{
    /// <summary>Provides overlap-safe native memory copies and byte fills on supported runtime platforms.</summary>
    public static unsafe class Memory
    {
        public static bool MacOSXCheck { get; }

        public static void Move(VoidPtr dst, VoidPtr src, uint size)
            => NativeMemory.Copy(src, dst, size);

        public static void Fill(VoidPtr dest, uint length, byte value)
            => NativeMemory.Fill(dest, length, value);
    }
}

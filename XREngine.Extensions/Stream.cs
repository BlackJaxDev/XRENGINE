using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace XREngine.Extensions
{
    public static class StreamExtensions
    {
        public static async Task<T> ReadAsync<T>(this Stream stream) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            byte[] bytes = new byte[size];
            await ReadExactlyAsync(stream, bytes);
            return MemoryMarshal.Read<T>(bytes);
        }
        public static T Read<T>(this Stream stream) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            byte[] bytes = new byte[size];
            ReadExactly(stream, bytes);
            return MemoryMarshal.Read<T>(bytes);
        }
        public static async Task WriteAsync<T>(this Stream stream, T value) where T : unmanaged
        {
            byte[] arr = new byte[Unsafe.SizeOf<T>()];
            MemoryMarshal.Write(arr, in value);
            await stream.WriteAsync(arr, 0, arr.Length);
        }
        public static void Write<T>(this Stream stream, T value) where T : unmanaged
        {
            byte[] arr = new byte[Unsafe.SizeOf<T>()];
            MemoryMarshal.Write(arr, in value);
            stream.Write(arr, 0, arr.Length);
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int bytesRead = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (bytesRead == 0)
                    throw new EndOfStreamException($"Stream ended early: expected {buffer.Length} bytes, got {totalRead} bytes.");
                totalRead += bytesRead;
            }
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int bytesRead = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead).ConfigureAwait(false);
                if (bytesRead == 0)
                    throw new EndOfStreamException($"Stream ended early: expected {buffer.Length} bytes, got {totalRead} bytes.");
                totalRead += bytesRead;
            }
        }
    }
}

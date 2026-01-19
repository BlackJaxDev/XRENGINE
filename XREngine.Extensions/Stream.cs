using System.Runtime.InteropServices;

namespace Extensions
{
    public static class StreamExtension
    {
        public static async Task<T> ReadAsync<T>(this Stream stream) where T : unmanaged
        {
            int size = Marshal.SizeOf<T>();
            byte[] bytes = new byte[size];
            await ReadExactlyAsync(stream, bytes);
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            T result = Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            handle.Free();
            return result;
        }
        public static T Read<T>(this Stream stream) where T : unmanaged
        {
            int size = Marshal.SizeOf<T>();
            byte[] bytes = new byte[size];
            ReadExactly(stream, bytes);
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            T result = Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            handle.Free();
            return result;
        }
        public static async Task WriteAsync<T>(this Stream stream, T value) where T : unmanaged
        {
            int size = Marshal.SizeOf<T>();
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(value, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            await stream.WriteAsync(arr, 0, arr.Length);
        }
        public static void Write<T>(this Stream stream, T value) where T : unmanaged
        {
            int size = Marshal.SizeOf<T>();
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(value, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
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

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MemoryPack;
using SixLabors.ImageSharp.PixelFormats;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    //Stores a reference to unmanaged data
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class DataSource : XRBase, IDisposable
    {
        static DataSource()
        {
            MemoryPackFormatterProvider.Register(new DataSourceFormatter());
        }

        [MemoryPackConstructor]
        private DataSource() { }
        /// <summary>
        /// If true, this data source references memory that was allocated somewhere else.
        /// </summary>
        public bool External { get; }
        public uint Length { get; set; }

        /// <summary>
        /// Controls whether YAML serialization should store this payload compressed.
        /// Default is true to keep YAML assets small.
        /// </summary>
        [YamlIgnore]
        [MemoryPackIgnore]
        public bool PreferCompressedYaml { get; set; } = true;
        [YamlIgnore]
        [MemoryPackIgnore]
        public VoidPtr Address { get; set; }

        public static DataSource Allocate<T>(uint count, bool zeroMemory = false) where T : unmanaged
            => new(count * (uint)Marshal.SizeOf<T>(), zeroMemory);
        public static unsafe DataSource FromArray<T>(T[] data) where T : unmanaged
        {
            DataSource source = new((uint)(data.Length * sizeof(T)));
            fixed (void* ptr = data)
                Memory.Move(source.Address, ptr, source.Length);
            return source;
        }

        public DataSource(byte[] data)
        {
            External = false;
            Length = (uint)data.Length;
            Address = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, Address, data.Length);
        }
        public DataSource(byte[] data, int offset, int length)
        {
            External = false;
            int len = Math.Min(data.Length, length);
            Length = (uint)len;
            Address = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, offset, Address, len);
        }
        public DataSource(VoidPtr address, uint length, bool copyInternal = false)
        {
            Length = length;
            if (copyInternal)
            {
                Address = Marshal.AllocHGlobal((int)Length);
                Memory.Move(Address, address, length);
                External = false;
            }
            else
            {
                Address = address;
                External = true;
            }
        }

        public DataSource(uint length, bool zeroMemory = false)
        {
            Length = length;
            Address = Marshal.AllocHGlobal((int)Length);
            if (zeroMemory)
                Memory.Fill(Address, (uint)Length, 0);
            External = false;
        }

        public static DataSource Allocate(uint size, bool zeroMemory = false)
            => new(size, zeroMemory);

        public unsafe UnmanagedMemoryStream AsStream()
            => new((byte*)Address, Length);

        #region IDisposable Support
        private bool _disposedValue = false;
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                try
                {
                    if (!External && Address != null)
                    {
                        Marshal.FreeHGlobal(Address);
                        Address = null;
                        Length = 0;
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.ToString());
                }

                _disposedValue = true;
            }
        }

        ~DataSource()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(false);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public byte[] GetBytes()
        {
            byte[] bytes = new byte[Length];
            Marshal.Copy(Address, bytes, 0, (int)Length);
            return bytes;
        }

        public short[] GetShorts()
        {
            short[] shorts = new short[Length / 2];
            Marshal.Copy(Address, shorts, 0, (int)(Length / 2));
            return shorts;
        }

        public float[] GetFloats()
        {
            float[] floats = new float[Length / 4];
            Marshal.Copy(Address, floats, 0, (int)(Length / 4));
            return floats;
        }

        public DataSource Clone()
        {
            if (External)
                return new DataSource(Address, Length, false);

            DataSource clone = new(Length);
            Memory.Move(clone.Address, Address, Length);
            return clone;
        }

        public static unsafe DataSource FromStream(Stream s)
        {
            s.Seek(0, SeekOrigin.Begin);
            s.Position = 0;
            DataSource source = new((uint)s.Length);
            byte* ptr = (byte*)source.Address;
            s.Read(new Span<byte>(ptr, (int)source.Length));
            return source;
        }

        public static DataSource FromStruct<T>(T structObj) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            DataSource source = new((uint)size);
            Marshal.StructureToPtr(structObj, source.Address, false);
            return source;
        }

        public T ToStruct<T>() where T : struct
        {
            return Marshal.PtrToStructure<T>(Address);
        }
        public unsafe T* ToStructPtr<T>() where T : unmanaged
        {
            return (T*)Address;
        }

        public static unsafe DataSource? FromSpan<T>(Span<T> data) where T : unmanaged
        {
            DataSource source = new((uint)(data.Length * sizeof(T)));
            fixed (void* ptr = data)
                Memory.Move(source.Address, ptr, source.Length);
            return source;
        }

        #endregion

        private sealed class DataSourceFormatter : MemoryPackFormatter<DataSource>
        {
            public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref DataSource? value)
            {
                if (value is null)
                {
                    writer.WriteNullObjectHeader();
                    return;
                }

                writer.WriteObjectHeader((byte)2);
                writer.WriteUnmanaged(value.External);
                writer.WriteUnmanaged(value.Length);

                if (value.Length == 0 || value.Address == IntPtr.Zero)
                    return;

                writer.WriteUnmanagedArray(value.GetBytes());
            }

            public override void Deserialize(ref MemoryPackReader reader, scoped ref DataSource? value)
            {
                if (!reader.TryReadObjectHeader(out byte count))
                {
                    value = null;
                    return;
                }

                if (count != 2)
                {
                    MemoryPackSerializationException.ThrowInvalidPropertyCount(2, count);
                }

                reader.ReadUnmanaged(out bool external);

                byte[]? payload = reader.ReadUnmanagedArray<byte>();

                if (payload is null || payload.Length == 0)
                {
                    value?.Dispose();
                    value = new DataSource(0);
                    return;
                }

                value?.Dispose();
                value = new DataSource(payload);
                // External flag cannot be preserved without exposing a setter; deserialized buffers are owned
            }
        }
    }
}

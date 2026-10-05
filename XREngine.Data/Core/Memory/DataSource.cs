using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MemoryPack;
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

        /// <summary>Whether this wrapper has been disposed, including when its memory is externally owned.</summary>
        [YamlIgnore]
        [MemoryPackIgnore]
        public bool IsDisposed => Volatile.Read(ref _disposedValue);

        /// <summary>
        /// Controls whether YAML serialization should store this payload compressed.
        /// Default is false.
        /// </summary>
        [YamlIgnore]
        [MemoryPackIgnore]
        public bool PreferCompressedYaml { get; set; } = false;

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
            Address = AllocateOwned(data.Length);
            Marshal.Copy(data, 0, Address, data.Length);
        }
        public unsafe DataSource(ReadOnlySpan<byte> data)
        {
            External = false;
            Length = (uint)data.Length;
            Address = AllocateOwned(data.Length);
            data.CopyTo(new Span<byte>((void*)Address, data.Length));
        }
        public DataSource(byte[] data, int offset, int length)
        {
            External = false;
            int len = Math.Min(data.Length - offset, length);
            Length = (uint)len;
            Address = AllocateOwned(len);
            Marshal.Copy(data, offset, Address, len);
        }
        public DataSource(VoidPtr address, uint length, bool copyInternal = false)
        {
            Length = length;
            if (copyInternal)
            {
                Address = AllocateOwned((int)Length);
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
            Address = AllocateOwned((int)Length);
            if (zeroMemory)
                Memory.Fill(Address, (uint)Length, 0);
            External = false;
        }

        public static DataSource Allocate(uint size, bool zeroMemory = false)
            => new(size, zeroMemory);

        public unsafe UnmanagedMemoryStream AsStream()
            => new((byte*)Address, Length);

        /// <summary>
        /// Size of the native block this source allocated and must free; zero for
        /// external sources. Kept apart from <see cref="Length"/>, which callers may
        /// shrink, so the accounting in <see cref="DataSourceMemoryStatistics"/>
        /// releases exactly what was recorded.
        /// </summary>
        private long _ownedBytes;

        private IntPtr AllocateOwned(int byteCount)
        {
            IntPtr address = Marshal.AllocHGlobal(byteCount);
            _ownedBytes = byteCount;
            DataSourceMemoryStatistics.RecordAllocation(byteCount);
            return address;
        }

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
                        DataSourceMemoryStatistics.RecordRelease(_ownedBytes, finalized: !disposing);
                        _ownedBytes = 0;
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

        /// <summary>Read-only view over the payload without copying. Valid while this data source is alive.</summary>
        public unsafe ReadOnlySpan<byte> AsReadOnlySpan()
            => new(Address.Pointer, checked((int)Length));

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

        /// <summary>
        /// Copies an owned source; an external source returns another view of the same
        /// memory. Subclasses whose memory is owned but not allocated here override it.
        /// </summary>
        public virtual DataSource Clone()
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
            ReadExactly(s, new Span<byte>(ptr, (int)source.Length));
            return source;
        }

        public static unsafe DataSource FromStruct<T>(T structObj) where T : unmanaged
        {
            var size = Unsafe.SizeOf<T>();
            DataSource source = new((uint)size);
            Unsafe.WriteUnaligned((void*)source.Address, structObj);
            return source;
        }

        public unsafe T ToStruct<T>() where T : unmanaged
        {
            return Unsafe.ReadUnaligned<T>((void*)Address);
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

        private static void ReadExactly(Stream stream, Span<byte> buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int bytesRead = stream.Read(buffer[totalRead..]);
                if (bytesRead == 0)
                    throw new EndOfStreamException($"Stream ended early: expected {buffer.Length} bytes, got {totalRead} bytes.");
                totalRead += bytesRead;
            }
        }

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

using MemoryPack;
using System;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// Lightweight view over a region of an <see cref="XRDataBuffer"/> so render backends
    /// can bind subranges or reinterpret the buffer with a specific sized internal format.
    /// </summary>
    [MemoryPackable]
    public partial class XRDataBufferView : GenericRenderObject, IDisposable
    {
        private XRDataBuffer _buffer;
        private ESizedInternalFormat _internalFormat;
        private uint _offsetBytes;
        private uint _lengthBytes;
        private bool _disposed;

        public XRDataBufferView(
            XRDataBuffer buffer,
            ESizedInternalFormat internalFormat,
            uint offsetBytes = 0u,
            uint lengthBytes = 0u)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _internalFormat = internalFormat;
            _offsetBytes = offsetBytes;
            _lengthBytes = NormalizeLength(lengthBytes);
        }

        /// <summary>
        /// Underlying buffer being viewed.
        /// </summary>
        public XRDataBuffer Buffer
        {
            get => _buffer;
            set => SetField(ref _buffer, value ?? throw new ArgumentNullException(nameof(value)));
        }

        /// <summary>
        /// Sized format used when the API creates the backing buffer view.
        /// </summary>
        public ESizedInternalFormat InternalFormat
        {
            get => _internalFormat;
            set => SetField(ref _internalFormat, value);
        }

        /// <summary>
        /// Byte offset into the buffer where this view begins.
        /// </summary>
        public uint OffsetBytes
        {
            get => _offsetBytes;
            set => SetField(ref _offsetBytes, value);
        }

        /// <summary>
        /// Requested length of the view in bytes. If set to 0, the view spans to the end of the buffer.
        /// </summary>
        public uint LengthBytes
        {
            get => _lengthBytes;
            set => SetField(ref _lengthBytes, NormalizeLength(value));
        }

        /// <summary>
        /// Length clamped against the current buffer size so renderers never walk past the end.
        /// </summary>
        public uint EffectiveLengthBytes
        {
            get
            {
                uint available = Buffer is not null && Buffer.Length > OffsetBytes
                    ? Buffer.Length - OffsetBytes
                    : 0u;
                return LengthBytes == 0u ? available : Math.Min(LengthBytes, available);
            }
        }

        private uint NormalizeLength(uint requestedLength)
        {
            uint available = Buffer?.Length ?? 0u;
            if (OffsetBytes > available)
                return 0u;

            if (requestedLength == 0u)
                return available - OffsetBytes;

            return Math.Min(requestedLength, available - OffsetBytes);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || _disposed)
                return;

            _disposed = true;
        }
    }
}
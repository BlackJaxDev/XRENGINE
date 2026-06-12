using MemoryPack;
using System;
using System.Runtime.CompilerServices;
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
        private uint _strideBytes;
        private uint _alignmentBytes = 1u;
        private string _viewName = string.Empty;
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
            _strideBytes = buffer.ElementSize;
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

        public string ViewName
        {
            get => _viewName;
            set => SetField(ref _viewName, value ?? string.Empty);
        }

        public uint StrideBytes
        {
            get => _strideBytes;
            set => SetField(ref _strideBytes, Math.Max(1u, value));
        }

        public uint AlignmentBytes
        {
            get => _alignmentBytes;
            set => SetField(ref _alignmentBytes, Math.Max(1u, value));
        }

        public uint ElementOffset => StrideBytes == 0u ? 0u : OffsetBytes / StrideBytes;

        public uint ElementCount => StrideBytes == 0u ? 0u : EffectiveLengthBytes / StrideBytes;

        public string DebugDisplayName
            => $"{Buffer?.AttributeName ?? "<buffer>"}:{(string.IsNullOrWhiteSpace(ViewName) ? InternalFormat.ToString() : ViewName)} stride={StrideBytes} offset={OffsetBytes} count={ElementCount}";

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

        public XRBufferWriter<T> Alloc<T>(uint count) where T : unmanaged
            => Alloc<T>(count, XRBufferWriteOptions.FromBuffer(Buffer));

        public XRBufferWriter<T> Alloc<T>(uint count, XRBufferWriteMode mode) where T : unmanaged
            => Alloc<T>(count, XRBufferWriteOptions.FromBuffer(Buffer).WithWriteMode(mode));

        public XRBufferWriter<T> Alloc<T>(uint count, XRBufferWriteOptions options) where T : unmanaged
        {
            uint typedStride = (uint)Unsafe.SizeOf<T>();
            if (OffsetBytes % typedStride != 0u)
                throw new InvalidOperationException($"Buffer view '{DebugDisplayName}' offset is not aligned to {typedStride}-byte elements.");

            if (options.AlignmentBytes > 1u && OffsetBytes % options.AlignmentBytes != 0u)
                throw new InvalidOperationException($"Buffer view '{DebugDisplayName}' offset is not aligned to {options.AlignmentBytes} bytes.");

            StrideBytes = typedStride;
            uint elementOffset = OffsetBytes / typedStride;
            return Buffer.AllocAt<T>(elementOffset, count, options);
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

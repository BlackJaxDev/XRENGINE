using System;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// Basic container for 1D texture mip levels.
    /// </summary>
    public sealed class Mipmap1D : XRBase
    {
        private DataSource? _data;
        private uint _width = 1;
        private EPixelInternalFormat _internalFormat = EPixelInternalFormat.Rgba8;
        private EPixelFormat _pixelFormat = EPixelFormat.Rgba;
        private EPixelType _pixelType = EPixelType.UnsignedByte;
        private XRDataBuffer? _streamingPbo;

        public Mipmap1D() { }

        public Mipmap1D(uint width, EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType, bool allocateData = false)
        {
            _width = width;
            _internalFormat = internalFormat;
            _pixelFormat = pixelFormat;
            _pixelType = pixelType;
            if (allocateData)
                AllocateDataBuffer();
        }

        public DataSource? Data
        {
            get => _data;
            set => SetField(ref _data, value);
        }

        public uint Width
        {
            get => _width;
            set => SetField(ref _width, value);
        }

        public EPixelInternalFormat InternalFormat
        {
            get => _internalFormat;
            set => SetField(ref _internalFormat, value);
        }

        public EPixelFormat PixelFormat
        {
            get => _pixelFormat;
            set => SetField(ref _pixelFormat, value);
        }

        public EPixelType PixelType
        {
            get => _pixelType;
            set => SetField(ref _pixelType, value);
        }

        public XRDataBuffer? StreamingPBO
        {
            get => _streamingPbo;
            set => SetField(ref _streamingPbo, value);
        }

        public uint GetDataLength() => Data?.Length ?? 0u;

        public bool HasData() => Data is not null && Data.Length != 0u;

        public void Resize(uint width, bool preserveContents)
        {
            if (width == Width)
                return;

            byte[]? oldBytes = null;
            if (preserveContents && Data is not null)
                oldBytes = Data.GetBytes();

            Width = width;
            AllocateDataBuffer(oldBytes);
        }

        public Mipmap1D Clone(bool cloneData)
            => new()
            {
                Width = Width,
                InternalFormat = InternalFormat,
                PixelFormat = PixelFormat,
                PixelType = PixelType,
                Data = cloneData ? Data?.Clone() : Data,
            };

        public unsafe void FillData(void* target)
        {
            if (Data is null)
                return;

            uint len = GetDataLength();
            Buffer.MemoryCopy(Data.Address.Pointer, target, len, len);
        }

        private void AllocateDataBuffer(byte[]? copyFrom = null)
        {
            byte[] data = XRTexture.AllocateBytes(Width, 1u, PixelFormat, PixelType);
            if (copyFrom is not null && copyFrom.Length > 0)
                Array.Copy(copyFrom, data, Math.Min(copyFrom.Length, data.Length));
            Data = new DataSource(data);
        }
    }
}

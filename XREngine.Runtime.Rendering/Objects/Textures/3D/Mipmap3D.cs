using System;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// Basic container for 3D texture mip levels.
    /// </summary>
    public sealed class Mipmap3D : XRBase
    {
        private DataSource? _data;
        private uint _width = 1;
        private uint _height = 1;
        private uint _depth = 1;
        private EPixelInternalFormat _internalFormat = EPixelInternalFormat.Rgba8;
        private EPixelFormat _pixelFormat = EPixelFormat.Rgba;
        private EPixelType _pixelType = EPixelType.UnsignedByte;
        private XRDataBuffer? _streamingPbo;

        public Mipmap3D() { }

        public Mipmap3D(uint width, uint height, uint depth, EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType, bool allocateData = false)
        {
            _width = width;
            _height = height;
            _depth = depth;
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

        public uint Height
        {
            get => _height;
            set => SetField(ref _height, value);
        }

        public uint Depth
        {
            get => _depth;
            set => SetField(ref _depth, value);
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

        public uint GetTexelCount() => Width * Height * Depth;

        public uint GetDataLength() => Data?.Length ?? 0u;

        public bool HasData() => Data is not null && Data.Length > 0u;

        public void Resize(uint width, uint height, uint depth, bool preserveContents)
        {
            if (width == Width && height == Height && depth == Depth)
                return;

            byte[]? oldBytes = null;
            if (preserveContents && Data is not null)
                oldBytes = Data.GetBytes();

            Width = width;
            Height = height;
            Depth = depth;
            AllocateDataBuffer(oldBytes);
        }

        public Mipmap3D Clone(bool cloneData)
            => new()
            {
                Width = Width,
                Height = Height,
                Depth = Depth,
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
            byte[] data = XRTexture.AllocateBytes(Width, Height, Depth, PixelFormat, PixelType);
            if (copyFrom is not null && copyFrom.Length > 0)
                Array.Copy(copyFrom, data, Math.Min(copyFrom.Length, data.Length));
            Data = new DataSource(data);
        }
    }
}

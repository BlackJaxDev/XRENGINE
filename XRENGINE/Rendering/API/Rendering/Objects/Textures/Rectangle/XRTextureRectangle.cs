using MemoryPack;
using System;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// Simple non-power-of-two rectangle texture wrapper.
    /// </summary>
    [MemoryPackable]
    public partial class XRTextureRectangle : XRTexture
    {
        private uint _width;
        private uint _height;
        private ESizedInternalFormat _sizedInternalFormat;
        private EPixelFormat _pixelFormat;
        private EPixelType _pixelType;
        private ETexMagFilter _magFilter = ETexMagFilter.Linear;
        private ETexMinFilter _minFilter = ETexMinFilter.Linear;
        private ETexWrapMode _uWrap = ETexWrapMode.ClampToEdge;
        private ETexWrapMode _vWrap = ETexWrapMode.ClampToEdge;
        private float _lodBias;
        private bool _resizable = true;
        private DataSource? _data;
        private XRDataBuffer? _streamingPbo;

        [MemoryPackConstructor]
        public XRTextureRectangle(
            uint width = 1,
            uint height = 1,
            ESizedInternalFormat sizedInternalFormat = ESizedInternalFormat.Rgba8,
            EPixelFormat pixelFormat = EPixelFormat.Rgba,
            EPixelType pixelType = EPixelType.UnsignedByte)
        {
            _width = Math.Max(1u, width);
            _height = Math.Max(1u, height);
            _sizedInternalFormat = sizedInternalFormat;
            _pixelFormat = pixelFormat;
            _pixelType = pixelType;
        }

        public uint Width
        {
            get => _width;
            set => SetField(ref _width, Math.Max(1u, value));
        }

        public uint Height
        {
            get => _height;
            set => SetField(ref _height, Math.Max(1u, value));
        }

        public ESizedInternalFormat SizedInternalFormat
        {
            get => _sizedInternalFormat;
            set => SetField(ref _sizedInternalFormat, value);
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

        public ETexMagFilter MagFilter
        {
            get => _magFilter;
            set => SetField(ref _magFilter, value);
        }

        public ETexMinFilter MinFilter
        {
            get => _minFilter;
            set => SetField(ref _minFilter, value);
        }

        public ETexWrapMode UWrap
        {
            get => _uWrap;
            set => SetField(ref _uWrap, value);
        }

        public ETexWrapMode VWrap
        {
            get => _vWrap;
            set => SetField(ref _vWrap, value);
        }

        public float LodBias
        {
            get => _lodBias;
            set => SetField(ref _lodBias, value);
        }

        public bool Resizable
        {
            get => _resizable;
            set => SetField(ref _resizable, value);
        }

        /// <summary>
        /// Optional CPU-side data for the rectangle texture. If provided, it will be uploaded on the next push.
        /// </summary>
        public DataSource? Data
        {
            get => _data;
            set => SetField(ref _data, value);
        }

        /// <summary>
        /// Optional pixel-unpack buffer used when streaming rectangle texture data from the GPU side.
        /// </summary>
        public XRDataBuffer? StreamingPBO
        {
            get => _streamingPbo;
            set => SetField(ref _streamingPbo, value);
        }

        public override bool HasAlphaChannel => HasAlpha(PixelFormat);

        public override bool IsResizeable => Resizable;

        public override uint MaxDimension => Math.Max(Width, Height);

        public override Vector3 WidthHeightDepth => new(Width, Height, 1);

        public void Resize(uint width, uint height)
        {
            if (!Resizable)
                return;

            Width = width;
            Height = height;
        }
    }
}

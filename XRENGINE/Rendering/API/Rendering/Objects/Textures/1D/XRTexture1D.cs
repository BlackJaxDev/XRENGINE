using System;
using System.Linq;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTexture1D : XRTexture
    {
        private Mipmap1D[] _mipmaps = [];
        private ESizedInternalFormat _sizedInternalFormat = ESizedInternalFormat.Rgba8;
        private ETexMagFilter _magFilter = ETexMagFilter.Linear;
        private ETexMinFilter _minFilter = ETexMinFilter.Linear;
        private ETexWrapMode _uWrap = ETexWrapMode.Repeat;
        private float _lodBias = 0.0f;
        private bool _resizable = true;
        private bool _exclusiveSharing = true;

        public XRTexture1D()
            : this(1u, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true)
        {
        }

        public XRTexture1D(uint width, EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType, bool allocateData = false, int mipCount = 1)
        {
            _sizedInternalFormat = ESizedInternalFormat.Rgba8;
            Mipmaps = CreateMipmaps(width, internalFormat, pixelFormat, pixelType, allocateData, mipCount);
        }

        private static Mipmap1D[] CreateMipmaps(uint width, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData, int mipCount)
        {
            mipCount = Math.Max(1, mipCount);
            Mipmap1D[] mips = new Mipmap1D[mipCount];
            uint currentWidth = width;
            for (int i = 0; i < mipCount; ++i)
            {
                mips[i] = new Mipmap1D(Math.Max(1u, currentWidth), internalFormat, format, type, allocateData);
                if (currentWidth > 1)
                    currentWidth >>= 1;
            }
            return mips;
        }

        public Mipmap1D[] Mipmaps
        {
            get => _mipmaps;
            set => SetField(ref _mipmaps, value ?? Array.Empty<Mipmap1D>());
        }

        public uint Width => Mipmaps.Length > 0 ? Mipmaps[0].Width : 0u;

        public ESizedInternalFormat SizedInternalFormat
        {
            get => _sizedInternalFormat;
            set => SetField(ref _sizedInternalFormat, value);
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

        public bool ExclusiveSharing
        {
            get => _exclusiveSharing;
            set => SetField(ref _exclusiveSharing, value);
        }

        public override bool HasAlphaChannel
            => Mipmaps.Any(HasAlphaFormat);

        public override bool IsResizeable => Resizable;

        public override uint MaxDimension => Width;

        public override Vector3 WidthHeightDepth => new(Width, 1, 1);

        private static bool HasAlphaFormat(Mipmap1D mip)
            => mip.PixelFormat switch
            {
                EPixelFormat.Rgba or
                EPixelFormat.Bgra or
                EPixelFormat.LuminanceAlpha or
                EPixelFormat.Alpha => true,
                _ => false,
            };

        public event Action? Resized;

        public void Resize(uint width)
        {
            if (!Resizable || Width == width)
                return;

            uint currentWidth = width;
            for (int i = 0; i < _mipmaps.Length && currentWidth > 0u; ++i)
            {
                _mipmaps[i]?.Resize(Math.Max(1u, currentWidth), true);
                if (currentWidth > 1u)
                    currentWidth >>= 1;
            }

            Resized?.Invoke();
        }

        public void GenerateMipmapsCPU()
        {
            if (Mipmaps.Length == 0)
                return;

            int desiredLevels = Math.Max(1, XRTexture.GetSmallestMipmapLevel(Width, 1));
            if (desiredLevels <= Mipmaps.Length)
                return;

            var baseMip = Mipmaps[0];
            Mipmap1D[] newMipmaps = new Mipmap1D[desiredLevels];
            newMipmaps[0] = baseMip;

            uint currentWidth = Width;
            for (int i = 1; i < desiredLevels; ++i)
            {
                currentWidth = Math.Max(1u, currentWidth >> 1);
                newMipmaps[i] = new Mipmap1D(currentWidth, baseMip.InternalFormat, baseMip.PixelFormat, baseMip.PixelType, false);
            }

            Mipmaps = newMipmaps;
        }
    }
}
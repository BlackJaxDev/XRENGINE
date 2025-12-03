using System;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTexture3D : XRTexture
    {
        private Mipmap3D[] _mipmaps = [];
        private ESizedInternalFormat _sizedInternalFormat = ESizedInternalFormat.Rgba8;
        private ETexMagFilter _magFilter = ETexMagFilter.Nearest;
        private ETexMinFilter _minFilter = ETexMinFilter.Nearest;
        private ETexWrapMode _uWrap = ETexWrapMode.Repeat;
        private ETexWrapMode _vWrap = ETexWrapMode.Repeat;
        private ETexWrapMode _wWrap = ETexWrapMode.Repeat;
        private float _lodBias = 0.0f;
        private bool _resizable = true;
        private bool _exclusiveSharing = true;

        public override bool IsResizeable => Resizable;

        public bool Resizable
        {
            get => _resizable;
            set => SetField(ref _resizable, value);
        }

        public override Vector3 WidthHeightDepth => new(Width, Height, Depth);

        public override bool HasAlphaChannel => Mipmaps.Any(HasAlphaFormat);

        public EDepthStencilFmt DepthStencilFormat { get; set; } = EDepthStencilFmt.None;

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

        public ETexWrapMode WWrap
        {
            get => _wWrap;
            set => SetField(ref _wWrap, value);
        }

        public float LodBias
        {
            get => _lodBias;
            set => SetField(ref _lodBias, value);
        }

        public ESizedInternalFormat SizedInternalFormat
        {
            get => _sizedInternalFormat;
            set => SetField(ref _sizedInternalFormat, value);
        }

        public bool ExclusiveSharing
        {
            get => _exclusiveSharing;
            set => SetField(ref _exclusiveSharing, value);
        }

        public Mipmap3D[] Mipmaps
        {
            get => _mipmaps;
            set => SetField(ref _mipmaps, value ?? Array.Empty<Mipmap3D>());
        }

        public uint Width => Mipmaps.Length > 0 ? Mipmaps[0].Width : 0u;
        public uint Height => Mipmaps.Length > 0 ? Mipmaps[0].Height : 0u;
        public uint Depth => Mipmaps.Length > 0 ? Mipmaps[0].Depth : 0u;

        public event Action? Resized;

        public XRTexture3D()
            : this(1, 1, 1, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, true)
        {
        }

        public XRTexture3D(uint width, uint height, uint depth)
            : this(width, height, depth, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, true)
        {
        }

        public XRTexture3D(uint width, uint height, uint depth, ColorF4 color)
            : this(width, height, depth, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true)
        {
            FillWithColor(color);
        }

        public XRTexture3D(uint width, uint height, uint depth, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData = false)
            : this(width, height, depth, internalFormat, format, type, allocateData, 1)
        {
        }

        public XRTexture3D(uint width, uint height, uint depth, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, int mipmapCount)
            : this(width, height, depth, internalFormat, format, type, allocateData: true, mipmapCount)
        {
        }

        private XRTexture3D(uint width, uint height, uint depth, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData, int mipmapCount)
        {
            _sizedInternalFormat = GuessSizedFormat(internalFormat);
            Mipmaps = CreateMipChain(width, height, depth, internalFormat, format, type, allocateData, mipmapCount);
        }

        private static Mipmap3D[] CreateMipChain(uint width, uint height, uint depth, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData, int mipmapCount)
        {
            mipmapCount = Math.Max(1, mipmapCount);
            Mipmap3D[] mips = new Mipmap3D[mipmapCount];
            uint w = width;
            uint h = height;
            uint d = depth;
            for (int i = 0; i < mipmapCount; ++i)
            {
                mips[i] = new Mipmap3D(Math.Max(1u, w), Math.Max(1u, h), Math.Max(1u, d), internalFormat, format, type, allocateData);
                if (w > 1u) w >>= 1;
                if (h > 1u) h >>= 1;
                if (d > 1u) d >>= 1;
            }
            return mips;
        }

        /// <summary>
        /// Resizes the textures stored in memory.
        /// Does nothing if Resizeable is false.
        /// </summary>
        public void Resize(uint width, uint height, uint depth, bool resizeRenderTexture = true)
        {
            if (!Resizable)
                return;

            if (Width == width && Height == height && Depth == depth)
                return;

            uint w = width;
            uint h = height;
            uint d = depth;
            for (int i = 0; i < _mipmaps.Length && w > 0u && h > 0u && d > 0u; ++i)
            {
                _mipmaps[i]?.Resize(Math.Max(1u, w), Math.Max(1u, h), Math.Max(1u, d), true);
                if (w > 1u) w >>= 1;
                if (h > 1u) h >>= 1;
                if (d > 1u) d >>= 1;
            }

            Resized?.Invoke();
        }

        public void GenerateMipmapsCPU()
        {
            if (Mipmaps.Length == 0)
                return;

            var baseMip = Mipmaps[0];
            int desiredLevels = Math.Max(1, (int)Math.Floor(Math.Log(Math.Max(1u, MaxDimension), 2)) + 1);
            desiredLevels = Math.Min(desiredLevels, SmallestAllowedMipmapLevel);

            if (desiredLevels <= Mipmaps.Length)
                return;

            Mipmap3D[] newMipmaps = new Mipmap3D[desiredLevels];
            Array.Copy(Mipmaps, newMipmaps, Math.Min(Mipmaps.Length, newMipmaps.Length));

            uint w = baseMip.Width;
            uint h = baseMip.Height;
            uint d = baseMip.Depth;
            for (int i = 1; i < desiredLevels; ++i)
            {
                if (newMipmaps[i] != null)
                    continue;

                w = Math.Max(1u, w >> 1);
                h = Math.Max(1u, h >> 1);
                d = Math.Max(1u, d >> 1);
                newMipmaps[i] = new Mipmap3D(w, h, d, baseMip.InternalFormat, baseMip.PixelFormat, baseMip.PixelType, false);
            }

            Mipmaps = newMipmaps;
        }

        public override uint MaxDimension => (uint)Math.Max(Math.Max(Width, Height), Depth);

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="depth">The texture's depth.</param>
        /// <param name="internalFmt">The internal texture storage format.</param>
        /// <param name="format">The format of the texture's pixels.</param>
        /// <param name="pixelType">How pixels are stored.</param>
        /// <param name="bufAttach">Where to attach to the framebuffer for rendering to.</param>
        /// <returns>A new 3D texture reference.</returns>
        public static XRTexture3D CreateFrameBufferTexture(uint width, uint height, uint depth,
            EPixelInternalFormat internalFmt, EPixelFormat format, EPixelType type, EFrameBufferAttachment bufAttach)
            => new(width, height, depth, internalFmt, format, type, false)
            {
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
                FrameBufferAttachment = bufAttach,
            };

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="depth">The texture's depth.</param>
        /// <param name="internalFmt">The internal texture storage format.</param>
        /// <param name="format">The format of the texture's pixels.</param>
        /// <param name="pixelType">How pixels are stored.</param>
        /// <returns>A new 3D texture reference.</returns>
        public static XRTexture3D CreateFrameBufferTexture(uint width, uint height, uint depth, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type)
            => new(width, height, depth, internalFormat, format, type, false)
            {
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
            };

        /// <summary>
        /// Creates a new 3D texture filled with a specific color.
        /// </summary>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="depth">The texture's depth.</param>
        /// <param name="color">The color to fill the texture with.</param>
        /// <returns>A new 3D texture reference.</returns>
        public static XRTexture3D CreateColorTexture(uint width, uint height, uint depth, ColorF4 color)
            => new(width, height, depth, color)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = true,
            };

        /// <summary>
        /// Creates a new 3D texture with default settings.
        /// </summary>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="depth">The texture's depth.</param>
        /// <param name="internalFormat">The internal texture storage format.</param>
        /// <param name="format">The format of the texture's pixels.</param>
        /// <param name="type">How pixels are stored.</param>
        /// <returns>A new 3D texture reference.</returns>
        public static XRTexture3D Create(uint width, uint height, uint depth, EPixelInternalFormat internalFormat = EPixelInternalFormat.Rgba8, EPixelFormat format = EPixelFormat.Rgba, EPixelType type = EPixelType.UnsignedByte)
            => new(width, height, depth, internalFormat, format, type, true)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.Repeat,
                VWrap = ETexWrapMode.Repeat,
                WWrap = ETexWrapMode.Repeat,
                AutoGenerateMipmaps = true,
            };

        private static ESizedInternalFormat GuessSizedFormat(EPixelInternalFormat internalFormat)
            => internalFormat switch
            {
                EPixelInternalFormat.Rgb8 => ESizedInternalFormat.Rgb8,
                EPixelInternalFormat.Rgba8 => ESizedInternalFormat.Rgba8,
                EPixelInternalFormat.Rgb16 => ESizedInternalFormat.Rgb16i,
                EPixelInternalFormat.Rgba16 => ESizedInternalFormat.Rgba16,
                EPixelInternalFormat.Rgb32f => ESizedInternalFormat.Rgb32f,
                EPixelInternalFormat.Rgba32f => ESizedInternalFormat.Rgba32f,
                _ => ESizedInternalFormat.Rgba8,
            };

        private static bool HasAlphaFormat(Mipmap3D mip)
            => mip.PixelFormat switch
            {
                EPixelFormat.Rgba or
                EPixelFormat.Bgra or
                EPixelFormat.LuminanceAlpha or
                EPixelFormat.Alpha => true,
                _ => false,
            };

        private void FillWithColor(ColorF4 color)
        {
            if (Mipmaps.Length == 0)
                return;

            var baseMip = Mipmaps[0];
            byte[] data = CreateSolidColorData(baseMip.Width, baseMip.Height, baseMip.Depth, color, baseMip.PixelFormat, baseMip.PixelType);
            baseMip.Data = new DataSource(data);
        }

        private static byte[] CreateSolidColorData(uint width, uint height, uint depth, ColorF4 color, EPixelFormat format, EPixelType type)
        {
            uint components = (uint)XRTexture.GetComponentCount(format);
            uint componentSize = XRTexture.ComponentSize(type);
            uint texels = Math.Max(1u, width * height * depth);
            byte[] bytes = new byte[texels * components * componentSize];

            if (type != EPixelType.UnsignedByte)
                return bytes;

            byte r = (byte)(color.R * 255f);
            byte g = (byte)(color.G * 255f);
            byte b = (byte)(color.B * 255f);
            byte a = (byte)(color.A * 255f);

            for (uint i = 0; i < texels; ++i)
            {
                uint offset = i * components;
                switch (format)
                {
                    case EPixelFormat.Red:
                    case EPixelFormat.Luminance:
                        bytes[offset] = r;
                        break;
                    case EPixelFormat.Rg:
                        bytes[offset] = r;
                        bytes[offset + 1] = g;
                        break;
                    case EPixelFormat.Bgr:
                        bytes[offset] = b;
                        bytes[offset + 1] = g;
                        bytes[offset + 2] = r;
                        break;
                    case EPixelFormat.Bgra:
                        bytes[offset] = b;
                        bytes[offset + 1] = g;
                        bytes[offset + 2] = r;
                        bytes[offset + 3] = a;
                        break;
                    case EPixelFormat.LuminanceAlpha:
                        bytes[offset] = r;
                        bytes[offset + 1] = a;
                        break;
                    case EPixelFormat.Rgba:
                    default:
                        bytes[offset] = r;
                        if (components > 1)
                            bytes[offset + 1] = g;
                        if (components > 2)
                            bytes[offset + 2] = b;
                        if (components > 3)
                            bytes[offset + 3] = a;
                        break;
                }
            }

            return bytes;
        }
    }
}

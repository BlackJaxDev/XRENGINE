using ImageMagick;
using MemoryPack;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    [MemoryPackable]
    [MemoryPackUnion(0, typeof(XRTexture2D))]
    [MemoryPackUnion(1, typeof(XRTexture2DArray))]
    [MemoryPackUnion(2, typeof(XRTexture3D))]
    [MemoryPackUnion(3, typeof(XRTextureCube))]
    [MemoryPackUnion(4, typeof(XRTextureCubeArray))]
    [MemoryPackUnion(5, typeof(XRTexture1D))]
    [MemoryPackUnion(6, typeof(XRTexture1DArray))]
    [MemoryPackUnion(7, typeof(XRTextureBuffer))]
    public abstract partial class XRTexture : GenericRenderObject
    {
        /// <summary>
        /// Allocates a new empty image with the specified dimensions, format and type.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="format"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        public static MagickImage NewImage(uint width, uint height, EPixelFormat format, EPixelType type)
        {
            byte[] data = AllocateBytes(width, height, format, type);
            MagickReadSettings settings = new()
            {
                Width = width,
                Height = height,
                FillColor = HasAlpha(format) ? MagickColor.FromRgba(0, 0, 0, 1) : MagickColor.FromRgb(0, 0, 0),
                Format = GetMagickFormat(format),
                ColorSpace = IsSigned(type) ? ColorSpace.sRGB : ColorSpace.RGB,
                Depth = type switch
                {
                    EPixelType.Byte => 8,
                    EPixelType.Short => 16,
                    EPixelType.Int => 32,
                    EPixelType.Float => 32,
                    EPixelType.HalfFloat => 16,
                    _ => 8,
                },
            };
            return new(data, settings);
        }
        /// <summary>
        /// Creates a new image with the specified dimensions, format, type and data.
        /// Allocate the data array parameter with AllocateBytes.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="format"></param>
        /// <param name="type"></param>
        /// <param name="dataFactory"></param>
        /// <returns></returns>
        public static MagickImage NewImage(uint width, uint height, EPixelFormat format, EPixelType type, byte[] data)
        {
            MagickReadSettings settings = new()
            {
                Width = width,
                Height = height,
                FillColor = HasAlpha(format) ? MagickColor.FromRgba(0, 0, 0, 255) : MagickColor.FromRgb(0, 0, 0),
                Format = GetMagickFormat(format),
                ColorSpace = IsSigned(type) ? ColorSpace.sRGB : ColorSpace.RGB,
                Depth = type switch
                {
                    EPixelType.Byte => 8,
                    EPixelType.Short => 16,
                    EPixelType.Int => 32,
                    EPixelType.Float => 32,
                    EPixelType.HalfFloat => 16,
                    _ => 8,
                },
            };
            return new(data, settings);
        }
        /// <summary>
        /// Allocates and populates a new image with the specified dimensions, format, type and data.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="format"></param>
        /// <param name="type"></param>
        /// <param name="dataFactory"></param>
        /// <returns></returns>
        public static MagickImage NewImage(uint width, uint height, EPixelFormat format, EPixelType type, Action<byte[]> dataFactory)
        {
            byte[] data = AllocateBytes(width, height, format, type);
            MagickReadSettings settings = new()
            {
                Width = width,
                Height = height,
                FillColor = HasAlpha(format) ? MagickColor.FromRgba(0, 0, 0, 255) : MagickColor.FromRgb(0, 0, 0),
                Format = GetMagickFormat(format),
                ColorSpace = IsSigned(type) ? ColorSpace.sRGB : ColorSpace.RGB,
                Depth = type switch
                {
                    EPixelType.Byte => 8,
                    EPixelType.Short => 16,
                    EPixelType.Int => 32,
                    EPixelType.Float => 32,
                    EPixelType.HalfFloat => 16,
                    _ => 8,
                },
            };
            dataFactory(data);
            return new(data, settings);
        }

        public static byte[] AllocateBytes(uint width, uint height, EPixelFormat format, EPixelType type)
            => AllocateBytes(width, height, 1u, format, type);

        public static byte[] AllocateBytes(uint width, uint height, uint depth, EPixelFormat format, EPixelType type)
            => new byte[width * height * depth * ComponentSize(type) * GetComponentCount(format)];

        public static void GetFormat(
            MagickImage bmp,
            bool internalCompression,
            //out ESizedInternalFormat sizedFormat,
            out EPixelInternalFormat internalPixelFormat,
            out EPixelFormat pixelFormat,
            out EPixelType pixelType)
        {
            //Internal format must match pixel format
            //GL_ALPHA, GL_LUMINANCE, GL_LUMINANCE_ALPHA, GL_RGB, GL_RGBA
            //bool hasAlpha = bmp.HasAlpha;
            uint channels = bmp.ChannelCount;
            bool signed = bmp.Settings.ColorSpace == ColorSpace.sRGB;
            uint depth = bmp.Depth; //8 is s/byte, 16 is u/short, 32 is float
            pixelType = bmp.Format switch
            {
                //MagickFormat.Hdr or MagickFormat.Exr => depth switch
                //{
                //    16 => EPixelType.HalfFloat,
                //    32 => EPixelType.Float,
                //    _ => throw new NotSupportedException($"Unsupported pixel depth: {depth}"),
                //},
                _ => depth switch
                {
                    8 => signed ? EPixelType.Byte : EPixelType.UnsignedByte,
                    16 => signed ? EPixelType.Short : EPixelType.UnsignedShort,
                    32 => EPixelType.Float,
                    _ => throw new NotSupportedException($"Unsupported pixel depth: {depth}"),
                },
            };
            switch (channels)
            {
                case 1:
                    internalPixelFormat = internalCompression ? EPixelInternalFormat.CompressedRed : EPixelInternalFormat.Red;
                    pixelFormat = EPixelFormat.Red;
                    break;
                case 2:
                    internalPixelFormat = internalCompression ? EPixelInternalFormat.CompressedRG : EPixelInternalFormat.RG;
                    pixelFormat = EPixelFormat.Rg;
                    break;
                case 3:
                    internalPixelFormat = internalCompression ? EPixelInternalFormat.CompressedRgb : EPixelInternalFormat.Rgb;
                    pixelFormat = EPixelFormat.Rgb;
                    break;
                default:
                case 4:
                    internalPixelFormat = internalCompression ? EPixelInternalFormat.CompressedRgba : EPixelInternalFormat.Rgba;
                    pixelFormat = EPixelFormat.Rgba;
                    break;
            }
        }

        public static bool IsSigned(EPixelType type)
            => type switch
            {
                EPixelType.Byte or
                EPixelType.Short or
                EPixelType.Int or
                EPixelType.Float or
                EPixelType.HalfFloat => true,
                _ => false,
            };

        public static bool HasAlpha(EPixelFormat fmt)
            => fmt switch
            {
                EPixelFormat.Rgba or
                EPixelFormat.Bgra or
                EPixelFormat.Alpha => true,
                _ => false,
            };

        public static uint ComponentSize(EPixelType type)
            => type switch
            {
                EPixelType.Byte => 1u,
                EPixelType.Short => 2u,
                EPixelType.Int => 4u,
                EPixelType.Float => 4u,
                EPixelType.HalfFloat => 2u,
                _ => 1u,
            };

        public static MagickFormat GetMagickFormat(EPixelFormat fmt)
            => fmt switch
            {
                EPixelFormat.Rgba => MagickFormat.Rgba,
                EPixelFormat.Bgra => MagickFormat.Bgra,
                EPixelFormat.Red => MagickFormat.R,
                EPixelFormat.Green => MagickFormat.G,
                EPixelFormat.Blue => MagickFormat.B,
                EPixelFormat.Alpha => MagickFormat.A,
                EPixelFormat.Rgb => MagickFormat.Rgb,
                EPixelFormat.Bgr => MagickFormat.Bgr,
                _ => MagickFormat.Rgba,
            };

        public static int GetComponentCount(EPixelFormat fmt)
            => fmt switch
            {
                EPixelFormat.Rgba or EPixelFormat.Bgra => 4,
                EPixelFormat.Rgb or EPixelFormat.Bgr => 3,
                EPixelFormat.Red or EPixelFormat.Green or EPixelFormat.Blue or EPixelFormat.Alpha => 1,
                EPixelFormat.DepthComponent => 1,
                EPixelFormat.StencilIndex => 1,
                EPixelFormat.Luminance => 1,
                _ => 4,
            };

        public delegate void DelAttachToFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel);
        public delegate void DelDetachFromFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel);
        
        [field: MemoryPackIgnore]
        public event DelAttachToFBO? AttachToFBORequested;
        [field: MemoryPackIgnore]
        public event DelDetachFromFBO? DetachFromFBORequested;

        private EFrameBufferAttachment? _frameBufferAttachment;
        public EFrameBufferAttachment? FrameBufferAttachment
        {
            get => _frameBufferAttachment;
            set => SetField(ref _frameBufferAttachment, value);
        }

        private string? _samplerName = null;
        /// <summary>
        /// This is the name the texture will use to bind to in the shader.
        /// If <see langword="null"/>, empty or whitespace, uses Texture# as the sampler name, where # is the texture's index in the material.
        /// </summary>
        public string? SamplerName
        {
            get => _samplerName;
            set => SetField(ref _samplerName, value);
        }

        /// <summary>
        /// Returns the mip level index of the 1x1 (or smallest) mip for this texture.
        /// Example: a 1920x1080 texture yields 11 mip levels indexed 0..10, so this returns 10.
        /// </summary>
        public int SmallestMipmapLevel
        {
            get
            {
                uint maxDim = MaxDimension;
                if (maxDim == 0)
                    return 0;

                int smallest = (int)Math.Floor(Math.Log2(maxDim));
                return Math.Min(smallest, SmallestAllowedMipmapLevel);
            }
        }

        //Note: 3.321928f is approx 1 / (log base 10 of 2)
        public static int GetSmallestMipmapLevel(uint width, uint height, int smallestAllowedMipmapLevel = 1000)
        {
            uint maxDim = Math.Max(width, height);
            if (maxDim == 0)
                return 0;

            int smallest = (int)Math.Floor(Math.Log10(maxDim) * 3.321928f);
            return Math.Min(smallest, smallestAllowedMipmapLevel);
        }

        public abstract uint MaxDimension { get; }

        private int _minLOD = -1000;
        public int MinLOD
        {
            get => _minLOD;
            set => SetField(ref _minLOD, value);
        }

        private int _maxLOD = 1000;
        public int MaxLOD
        {
            get => _maxLOD;
            set => SetField(ref _maxLOD, value);
        }

        private int _largestMipmapLevel = 0;
        public int LargestMipmapLevel
        {
            get => _largestMipmapLevel;
            set => SetField(ref _largestMipmapLevel, value);
        }

        private int _smallestAllowedMipmapLevel = 1000;
        public int SmallestAllowedMipmapLevel
        {
            get => _smallestAllowedMipmapLevel;
            set => SetField(ref _smallestAllowedMipmapLevel, value);
        }

        private bool _autoGenerateMipmaps = false;
        public bool AutoGenerateMipmaps
        {
            get => _autoGenerateMipmaps;
            set => SetField(ref _autoGenerateMipmaps, value);
        }

        private bool _alphaAsTransparency = false;
        public bool AlphaAsTransparency
        {
            get => _alphaAsTransparency;
            set => SetField(ref _alphaAsTransparency, value);
        }

        private bool _internalCompression = false;
        public bool InternalCompression
        {
            get => _internalCompression;
            set => SetField(ref _internalCompression, value);
        }

        public virtual bool IsResizeable { get; } = false;
        public virtual bool HasAlphaChannel { get; } = false;
        public abstract Vector3 WidthHeightDepth { get; }

        public void AttachToFBO(XRFrameBuffer target, int mipLevel = 0)
        {
            if (FrameBufferAttachment.HasValue)
                AttachToFBO(target, FrameBufferAttachment.Value, mipLevel);
        }

        public void DetachFromFBO(XRFrameBuffer target, int mipLevel = 0)
        {
            if (FrameBufferAttachment.HasValue)
                DetachFromFBO(target, FrameBufferAttachment.Value, mipLevel);
        }

        public void AttachToFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel = 0)
            => AttachToFBORequested?.Invoke(target, attachment, mipLevel);
        public void DetachFromFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel = 0)
            => DetachFromFBORequested?.Invoke(target, attachment, mipLevel);

        /// <summary>
        /// Returns the sampler name for this texture to bind into the shader.
        /// </summary>
        /// <param name="textureIndex">The index of the texture. Only used if the override parameter and the SamplerName property are null or invalid.</param>
        /// <param name="samplerNameOverride">The binding name to force bind to, if desired.</param>
        /// <returns></returns>
        public string ResolveSamplerName(int textureIndex, string? samplerNameOverride = null)
            => samplerNameOverride ?? SamplerName ?? $"Texture{textureIndex}";

        //public XREvent<XRTexture> SetParametersRequested { get; } = new XREvent<XRTexture>();
        //public void SetParameters() => SetParametersRequested.Invoke(this);

        public void SampleIn(XRRenderProgram program, int textureIndex)
            => program.Sampler(ResolveSamplerName(textureIndex, null), this, textureIndex);

        [field: MemoryPackIgnore]
        public event Action? PushDataRequested;
        [field: MemoryPackIgnore]
        public event Action? BindRequested;
        [field: MemoryPackIgnore]
        public event Action? UnbindRequested;
        [field: MemoryPackIgnore]
        public event DelClear? ClearRequested;
        [field: MemoryPackIgnore]
        public event Action? GenerateMipmapsRequested;

        public delegate void DelClear(ColorF4 color, int level = 0);

        public void PushData()
            => PushDataRequested?.Invoke();
        public void Bind()
            => BindRequested?.Invoke();
        public void Unbind()
            => UnbindRequested?.Invoke();
        public void Clear(ColorF4 color, int level = 0)
            => ClearRequested?.Invoke(color, level);

        /// <summary>
        /// Requests the GPU to generate mipmaps for this image.
        /// </summary>
        public void GenerateMipmapsGPU()
            => GenerateMipmapsRequested?.Invoke();
    }
}

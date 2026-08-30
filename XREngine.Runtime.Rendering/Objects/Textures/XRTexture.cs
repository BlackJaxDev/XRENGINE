using ImageMagick;
using MemoryPack;
using System.Numerics;
using System.Runtime.InteropServices;
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
    public abstract partial class XRTexture : GenericRenderObject, IRenderTextureResource
    {
        // Cached "Texture{N}" sampler strings — avoids per-call string interpolation on hot render paths.
        private static readonly string[] _indexedSamplerNames = CreateIndexedSamplerNames(32);
        private static string[] CreateIndexedSamplerNames(int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++)
                names[i] = $"Texture{i}";
            return names;
        }
        internal static string GetIndexedSamplerName(int textureIndex)
            => (uint)textureIndex < (uint)_indexedSamplerNames.Length
                ? _indexedSamplerNames[textureIndex]
                : $"Texture{textureIndex}";

        /// <summary>
        /// Allocates a new empty image with the specified dimensions, format and type.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="format"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        public static MagickImage NewImage(uint width, uint height, EPixelFormat format, EPixelType type)
            => NewImage(width, height, format, type, AllocateBytes(width, height, format, type));
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
            ArgumentNullException.ThrowIfNull(data);
            int expectedLength = GetCheckedByteLength(width, height, 1u, format, type);
            if (data.Length != expectedLength)
                throw new ArgumentException($"Expected {expectedLength} bytes but received {data.Length}.", nameof(data));

            string mapping = GetMagickPixelMapping(format);
            if (type == EPixelType.HalfFloat)
            {
                ReadOnlySpan<Half> source = MemoryMarshal.Cast<byte, Half>(data);
                float[] values = GC.AllocateUninitializedArray<float>(source.Length);
                for (int i = 0; i < source.Length; i++)
                    values[i] = (float)source[i] * Quantum.Max;

                MagickImage halfFloatImage = new();
                halfFloatImage.ReadPixels(values, new PixelReadSettings(width, height, StorageType.Quantum, mapping));
                return halfFloatImage;
            }

            StorageType storageType = GetMagickStorageType(type);
            return new MagickImage(data, new PixelReadSettings(width, height, storageType, mapping));
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
            dataFactory(data);
            return NewImage(width, height, format, type, data);
        }

        public static byte[] AllocateBytes(uint width, uint height, EPixelFormat format, EPixelType type)
            => AllocateBytes(width, height, 1u, format, type);

        public static byte[] AllocateBytes(uint width, uint height, uint depth, EPixelFormat format, EPixelType type)
            => new byte[GetCheckedByteLength(width, height, depth, format, type)];

        private static int GetCheckedByteLength(uint width, uint height, uint depth, EPixelFormat format, EPixelType type)
        {
            uint bytesPerPixel = IsPackedPixelType(type)
                ? ComponentSize(type)
                : checked(ComponentSize(type) * (uint)GetComponentCount(format));
            long byteLength = checked((long)width * height * depth * bytesPerPixel);
            if (byteLength > int.MaxValue)
                throw new NotSupportedException("Image data cannot exceed 2 GB.");

            return (int)byteLength;
        }

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
            uint depth = bmp.Depth;
            pixelType = bmp.Format switch
            {
                MagickFormat.Hdr or MagickFormat.Exr or MagickFormat.Pfm => EPixelType.Float,
                _ => depth switch
                {
                    1 => EPixelType.UnsignedByte,
                    8 => EPixelType.UnsignedByte,
                    16 => EPixelType.UnsignedShort,
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
                EPixelFormat.RgbaInteger or
                EPixelFormat.BgraInteger or
                EPixelFormat.LuminanceAlpha or
                EPixelFormat.Alpha => true,
                _ => false,
            };

        public static uint ComponentSize(EPixelType type)
            => type switch
            {
                EPixelType.Byte or EPixelType.UnsignedByte => 1u,
                EPixelType.Short or EPixelType.UnsignedShort => 2u,
                EPixelType.Int or EPixelType.UnsignedInt or EPixelType.Float => 4u,
                EPixelType.HalfFloat => 2u,
                EPixelType.UnsignedByte332 or EPixelType.UnsignedByte233Reversed => 1u,
                EPixelType.UnsignedShort4444 or
                    EPixelType.UnsignedShort5551 or
                    EPixelType.UnsignedShort565 or
                    EPixelType.UnsignedShort565Reversed or
                    EPixelType.UnsignedShort4444Reversed or
                    EPixelType.UnsignedShort1555Reversed => 2u,
                EPixelType.UnsignedInt8888 or
                    EPixelType.UnsignedInt1010102 or
                    EPixelType.UnsignedInt8888Reversed or
                    EPixelType.UnsignedInt2101010Reversed or
                    EPixelType.UnsignedInt248 or
                    EPixelType.UnsignedInt10F11F11FRev or
                    EPixelType.UnsignedInt5999Rev => 4u,
                EPixelType.Float32UnsignedInt248Rev => 8u,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported pixel type."),
            };

        private static bool IsPackedPixelType(EPixelType type)
            => type >= EPixelType.UnsignedByte332;

        private static StorageType GetMagickStorageType(EPixelType type)
            => type switch
            {
                EPixelType.UnsignedByte => StorageType.Char,
                EPixelType.UnsignedShort => StorageType.Short,
                EPixelType.UnsignedInt => StorageType.Int32,
                EPixelType.Float => StorageType.Float,
                EPixelType.Byte or EPixelType.Short or EPixelType.Int =>
                    throw new NotSupportedException($"ImageMagick raw-pixel conversion does not support signed {type} data."),
                _ => throw new NotSupportedException($"ImageMagick raw-pixel conversion does not support {type} data."),
            };

        private static string GetMagickPixelMapping(EPixelFormat format)
            => format switch
            {
                EPixelFormat.Rgba or EPixelFormat.RgbaInteger => "RGBA",
                EPixelFormat.Bgra or EPixelFormat.BgraInteger => "BGRA",
                EPixelFormat.Rgb or EPixelFormat.RgbInteger => "RGB",
                EPixelFormat.Bgr or EPixelFormat.BgrInteger => "BGR",
                EPixelFormat.Rg or EPixelFormat.RgInteger => "RG",
                EPixelFormat.Red or EPixelFormat.RedInteger => "R",
                EPixelFormat.Green or EPixelFormat.GreenInteger => "G",
                EPixelFormat.Blue or EPixelFormat.BlueInteger => "B",
                EPixelFormat.Alpha or EPixelFormat.AlphaInteger => "A",
                EPixelFormat.Luminance => "I",
                EPixelFormat.LuminanceAlpha => "IA",
                EPixelFormat.DepthComponent or
                    EPixelFormat.StencilIndex or
                    EPixelFormat.ColorIndex or
                    EPixelFormat.UnsignedShort or
                    EPixelFormat.UnsignedInt => "I",
                _ => throw new NotSupportedException($"ImageMagick raw-pixel conversion does not support {format} data."),
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
                EPixelFormat.Rgba or EPixelFormat.Bgra or
                EPixelFormat.RgbaInteger or EPixelFormat.BgraInteger => 4,
                EPixelFormat.Rgb or EPixelFormat.Bgr or
                EPixelFormat.RgbInteger or EPixelFormat.BgrInteger => 3,
                EPixelFormat.Rg or EPixelFormat.RgInteger or
                EPixelFormat.LuminanceAlpha or EPixelFormat.DepthStencil => 2,
                EPixelFormat.UnsignedShort or EPixelFormat.UnsignedInt or
                EPixelFormat.ColorIndex or EPixelFormat.StencilIndex or
                EPixelFormat.DepthComponent or EPixelFormat.Red or
                EPixelFormat.Green or EPixelFormat.Blue or EPixelFormat.Alpha or
                EPixelFormat.RedInteger or EPixelFormat.GreenInteger or
                EPixelFormat.BlueInteger or EPixelFormat.AlphaInteger or
                EPixelFormat.Luminance => 1,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, "Unsupported pixel format."),
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

                // Exact floor(log2(maxDim)) with no floating-point rounding issues.
                int smallest = BitOperations.Log2(maxDim);
                return Math.Min(smallest, SmallestAllowedMipmapLevel);
            }
        }

        //Note: 3.321928f is approx 1 / (log base 10 of 2)
        public static int GetSmallestMipmapLevel(uint width, uint height, int smallestAllowedMipmapLevel = 1000)
        {
            uint maxDim = Math.Max(width, height);
            if (maxDim == 0)
                return 0;

            // Exact floor(log2(maxDim)) with no floating-point rounding issues.
            int smallest = BitOperations.Log2(maxDim);
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

        internal void ApplyImportedTextureStreamingMipRangeMetadata(
            bool autoGenerateMipmaps,
            int largestMipmapLevel,
            int smallestAllowedMipmapLevel)
        {
            AutoGenerateMipmaps = autoGenerateMipmaps;
            LargestMipmapLevel = largestMipmapLevel;
            SmallestAllowedMipmapLevel = smallestAllowedMipmapLevel;
        }

        private ETextureColorSpace _importedColorSpace = ETextureColorSpace.Linear;
        /// <summary>
        /// Transfer function requested by the source texture importer.
        /// </summary>
        public ETextureColorSpace ImportedColorSpace
        {
            get => _importedColorSpace;
            set => SetField(ref _importedColorSpace, value);
        }

        private ETextureImportUsage _importedUsage = ETextureImportUsage.Data;
        /// <summary>
        /// Semantic pixel role requested by the source texture importer.
        /// </summary>
        public ETextureImportUsage ImportedUsage
        {
            get => _importedUsage;
            set => SetField(ref _importedUsage, value);
        }

        private bool _importedNormalMapFlipGreen;
        /// <summary>
        /// Whether imported normal-map sampling must invert the green channel.
        /// </summary>
        public bool ImportedNormalMapFlipGreen
        {
            get => _importedNormalMapFlipGreen;
            set => SetField(ref _importedNormalMapFlipGreen, value);
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

        /// <summary>
        /// When true, the Vulkan backend will include <c>VK_IMAGE_USAGE_STORAGE_BIT</c>
        /// on the VkImage so it can be bound as a storage image in compute shaders.
        /// Set this before the texture is generated.
        /// </summary>
        private bool _requiresStorageUsage;
        public bool RequiresStorageUsage
        {
            get => _requiresStorageUsage;
            set => SetField(ref _requiresStorageUsage, value);
        }

        [MemoryPackIgnore]
        private nint _openGlExternalMemoryImportHandle;
        [MemoryPackIgnore]
        public nint OpenGlExternalMemoryImportHandle
        {
            get => _openGlExternalMemoryImportHandle;
            set => SetField(ref _openGlExternalMemoryImportHandle, value);
        }

        [MemoryPackIgnore]
        private ulong _openGlExternalMemoryImportSize;
        [MemoryPackIgnore]
        public ulong OpenGlExternalMemoryImportSize
        {
            get => _openGlExternalMemoryImportSize;
            set => SetField(ref _openGlExternalMemoryImportSize, value);
        }

        [MemoryPackIgnore]
        private uint _openGlExternalMemoryImportMipLevels = 1;
        [MemoryPackIgnore]
        public uint OpenGlExternalMemoryImportMipLevels
        {
            get => _openGlExternalMemoryImportMipLevels;
            set => SetField(ref _openGlExternalMemoryImportMipLevels, value);
        }

        [MemoryPackIgnore]
        private string? _openGlExternalMemoryLabel;
        [MemoryPackIgnore]
        public string? OpenGlExternalMemoryLabel
        {
            get => _openGlExternalMemoryLabel;
            set => SetField(ref _openGlExternalMemoryLabel, value);
        }

        [MemoryPackIgnore]
        public bool UsesOpenGlExternalMemoryImport
            => OpenGlExternalMemoryImportHandle != 0 && OpenGlExternalMemoryImportSize > 0;

        public void ClearOpenGlExternalMemoryImport()
        {
            OpenGlExternalMemoryImportHandle = 0;
            OpenGlExternalMemoryImportSize = 0;
            OpenGlExternalMemoryImportMipLevels = 1;
            OpenGlExternalMemoryLabel = null;
        }

        public void SetOpenGlExternalMemoryImport(nint handle, ulong size, string? label = null, uint mipLevels = 1)
        {
            OpenGlExternalMemoryImportHandle = handle;
            OpenGlExternalMemoryImportSize = size;
            OpenGlExternalMemoryImportMipLevels = Math.Max(1u, mipLevels);
            OpenGlExternalMemoryLabel = label;
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
            => samplerNameOverride ?? SamplerName ?? GetIndexedSamplerName(textureIndex);

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

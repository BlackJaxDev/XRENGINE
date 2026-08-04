using System;
using System.ComponentModel;
using ImageMagick;
using MemoryPack;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Rendering
{
    /// <summary>
    /// Defines raw image data for a 2D texture mipmap.
    /// Has support for resizing with and converting to/from MagickImage.
    /// </summary>
    [MemoryPackable]
    public partial class Mipmap2D : XRBase
    {
        //private static object _lock = new();
        
        [field: MemoryPackIgnore]
        public event Action? Invalidated;
        public void Invalidate() => Invalidated?.Invoke();

        [MemoryPackConstructor]
        public Mipmap2D() { }
        public Mipmap2D(MagickImage? image)
        {
            if (image != null)
                SetFromImage(image);
        }
        public Mipmap2D(Mipmap2D mipmap)
        {
            //lock (_lock)
            //{
                InternalFormat = mipmap.InternalFormat;
                PixelFormat = mipmap.PixelFormat;
                PixelType = mipmap.PixelType;
                Data = mipmap.Data;
                Width = mipmap.Width;
                Height = mipmap.Height;
            //}
        }
        public Mipmap2D(uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType, bool allocateData)
        {
            //lock (_lock)
            //{
                Width = width;
                Height = height;
                InternalFormat = internalFormat;
                PixelFormat = pixelFormat;
                PixelType = pixelType;
                Data = allocateData ? new DataSource(XRTexture.AllocateBytes(width, height, pixelFormat, pixelType)) : null;
            //}
        }
        public Mipmap2D(uint width, uint height, ReadOnlySpan<byte> rgbaPixels)
            => SetFromRgba32(width, height, rgbaPixels);

        [MemoryPackIgnore]
        public DataSource? Data
        {
            get => _bytes;
            set => SetField(ref _bytes, value);
        }

        [Browsable(false)]
        [MemoryPackInclude]
        [YamlIgnore]
        public byte[]? DataBytes
        {
            get => _bytes?.GetBytes();
            set => SetField(ref _bytes, value is null ? null : new DataSource(value));
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

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change && propName == nameof(Data) && Data is not null)
                Data.Dispose();
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {

        }

        public static explicit operator Mipmap2D(MagickImage image)
        {
            Mipmap2D mip = new();
            mip.SetFromImage(image);
            return mip;
        }

        public static explicit operator MagickImage(Mipmap2D mipmap)
            => mipmap.GetImage();

        /// <summary>
        /// Copies tightly packed, row-major RGBA8 pixels into this mipmap.
        /// </summary>
        public void SetFromRgba32(uint width, uint height, ReadOnlySpan<byte> rgbaPixels)
        {
            long requiredLength = checked((long)width * height * 4L);
            if (requiredLength > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(width), "RGBA32 pixel data cannot exceed 2 GB.");
            if (rgbaPixels.Length != (int)requiredLength)
                throw new ArgumentException($"Expected {requiredLength} RGBA32 bytes but received {rgbaPixels.Length}.", nameof(rgbaPixels));

            InternalFormat = EPixelInternalFormat.Rgba8;
            PixelFormat = EPixelFormat.Rgba;
            PixelType = EPixelType.UnsignedByte;
            Data = new DataSource(rgbaPixels);
            Width = width;
            Height = height;
        }
        public void SetFromImage(MagickImage image)
        {
            ArgumentNullException.ThrowIfNull(image);

            if (IsHighDynamicRange(image.Format))
                SetFromHighDynamicRangeImage(image);
            else
                SetFromLowDynamicRangeImage(image);

            Width = image.Width;
            Height = image.Height;
        }

        private static bool IsHighDynamicRange(MagickFormat format)
            => format is MagickFormat.Exr or MagickFormat.Hdr or MagickFormat.Pfm;

        private unsafe void SetFromLowDynamicRangeImage(MagickImage image)
        {
            const int targetChunkBytes = 1024 * 1024;
            int totalBytes = GetCheckedRgbaByteLength(image.Width, image.Height, sizeof(byte));
            int rowBytes = checked((int)image.Width * 4);
            uint rowsPerChunk = (uint)Math.Max(1, targetChunkBytes / Math.Max(1, rowBytes));
            DataSource decodedData = DataSource.Allocate((uint)totalBytes);
            MagickImage? convertedImage = null;
            bool dataAssigned = false;

            try
            {
                MagickImage pixelSource = image;
                if (RequiresSrgbConversion(image.ColorSpace))
                {
                    convertedImage = (MagickImage)image.Clone();
                    convertedImage.ColorSpace = ColorSpace.sRGB;
                    pixelSource = convertedImage;
                }

                using IPixelCollection<float> pixels = pixelSource.GetPixels()
                    ?? throw new InvalidOperationException("ImageMagick could not expose the decoded pixels.");
                for (uint y = 0; y < image.Height; y += rowsPerChunk)
                {
                    uint chunkHeight = Math.Min(rowsPerChunk, image.Height - y);
                    byte[] chunk = pixels.ToByteArray(0, checked((int)y), image.Width, chunkHeight, PixelMapping.RGBA)
                        ?? throw new InvalidDataException("ImageMagick returned no RGBA pixel data.");
                    int expectedChunkBytes = checked((int)(image.Width * chunkHeight * 4u));
                    if (chunk.Length != expectedChunkBytes)
                        throw new InvalidDataException($"ImageMagick returned {chunk.Length} RGBA bytes; expected {expectedChunkBytes}.");

                    int destinationOffset = checked((int)(y * image.Width * 4u));
                    chunk.CopyTo(new Span<byte>((byte*)decodedData.Address + destinationOffset, chunk.Length));
                }

                InternalFormat = EPixelInternalFormat.Rgba8;
                PixelFormat = EPixelFormat.Rgba;
                PixelType = EPixelType.UnsignedByte;
                Data = decodedData;
                dataAssigned = true;
            }
            finally
            {
                if (!dataAssigned)
                    decodedData.Dispose();
                convertedImage?.Dispose();
            }
        }

        private unsafe void SetFromHighDynamicRangeImage(MagickImage image)
        {
            const int targetChunkBytes = 1024 * 1024;
            int totalBytes = GetCheckedRgbaByteLength(image.Width, image.Height, sizeof(float));
            int rowBytes = checked((int)image.Width * 4 * sizeof(float));
            uint rowsPerChunk = (uint)Math.Max(1, targetChunkBytes / Math.Max(1, rowBytes));
            DataSource decodedData = DataSource.Allocate((uint)totalBytes);
            bool dataAssigned = false;

            try
            {
                using IPixelCollection<float> pixels = image.GetPixels()
                    ?? throw new InvalidOperationException("ImageMagick could not expose the decoded pixels.");
                int channelCount = checked((int)pixels.Channels);
                int redIndex = checked((int)(pixels.GetChannelIndex(PixelChannel.Red)
                    ?? pixels.GetChannelIndex(PixelChannel.Gray)
                    ?? 0u));
                int greenIndex = checked((int)(pixels.GetChannelIndex(PixelChannel.Green) ?? (uint)redIndex));
                int blueIndex = checked((int)(pixels.GetChannelIndex(PixelChannel.Blue) ?? (uint)redIndex));
                uint? magickAlphaIndex = pixels.GetChannelIndex(PixelChannel.Alpha);
                int alphaIndex = magickAlphaIndex.HasValue ? checked((int)magickAlphaIndex.Value) : -1;
                float inverseQuantumRange = 1.0f / Quantum.Max;
                float* destination = (float*)decodedData.Address;

                for (uint y = 0; y < image.Height; y += rowsPerChunk)
                {
                    uint chunkHeight = Math.Min(rowsPerChunk, image.Height - y);
                    float[] chunk = pixels.GetArea(0, checked((int)y), image.Width, chunkHeight)
                        ?? throw new InvalidDataException("ImageMagick returned no HDR pixel data.");
                    int pixelCount = checked((int)(image.Width * chunkHeight));
                    int expectedValues = checked(pixelCount * channelCount);
                    if (chunk.Length != expectedValues)
                        throw new InvalidDataException($"ImageMagick returned {chunk.Length} channel values; expected {expectedValues}.");

                    int destinationPixel = checked((int)(y * image.Width));
                    for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                    {
                        int sourceOffset = pixelIndex * channelCount;
                        int destinationOffset = (destinationPixel + pixelIndex) * 4;
                        destination[destinationOffset] = chunk[sourceOffset + redIndex] * inverseQuantumRange;
                        destination[destinationOffset + 1] = chunk[sourceOffset + greenIndex] * inverseQuantumRange;
                        destination[destinationOffset + 2] = chunk[sourceOffset + blueIndex] * inverseQuantumRange;
                        destination[destinationOffset + 3] = alphaIndex >= 0
                            ? chunk[sourceOffset + alphaIndex] * inverseQuantumRange
                            : 1.0f;
                    }
                }

                InternalFormat = EPixelInternalFormat.Rgba32f;
                PixelFormat = EPixelFormat.Rgba;
                PixelType = EPixelType.Float;
                Data = decodedData;
                dataAssigned = true;
            }
            finally
            {
                if (!dataAssigned)
                    decodedData.Dispose();
            }
        }

        private static int GetCheckedRgbaByteLength(uint width, uint height, int componentSize)
        {
            long byteLength = checked((long)width * height * 4L * componentSize);
            if (byteLength > int.MaxValue)
                throw new NotSupportedException("Decoded texture data cannot exceed 2 GB.");

            return (int)byteLength;
        }

        private static bool RequiresSrgbConversion(ColorSpace colorSpace)
            => colorSpace is not ColorSpace.RGB
                and not ColorSpace.sRGB
                and not ColorSpace.scRGB
                and not ColorSpace.Gray
                and not ColorSpace.LinearGray;

        public MagickImage GetImage()
        {
            byte[]? bytes = Data?.GetBytes();
            MagickImage image = bytes != null
                ? XRTexture.NewImage(Width, Height, PixelFormat, PixelType, bytes)
                : XRTexture.NewImage(Width, Height, PixelFormat, PixelType);

            // PixelReadSettings describes raw storage but has no source-file format. Tag floating
            // color images as EXR so a resize or explicit MagickImage round-trip retains HDR range.
            if (PixelType is EPixelType.Float or EPixelType.HalfFloat &&
                PixelFormat is EPixelFormat.Rgb or EPixelFormat.Rgba or EPixelFormat.Bgr or EPixelFormat.Bgra)
            {
                image.Format = MagickFormat.Exr;
            }

            return image;
        }

        private EPixelType _pixelType = EPixelType.UnsignedByte;
        public EPixelType PixelType
        {
            get => _pixelType;
            set => SetField(ref _pixelType, value);
        }

        private EPixelFormat _pixelFormat = EPixelFormat.Rgba;
        public EPixelFormat PixelFormat
        {
            get => _pixelFormat;
            set => SetField(ref _pixelFormat, value);
        }

        private EPixelInternalFormat _internalFormat = EPixelInternalFormat.Rgba8;
        private DataSource? _bytes = null;
        private uint _width = 0;
        private uint _height = 0;
        [MemoryPackIgnore]
        [YamlIgnore]
        public XRDataBuffer? _streamingPBO = null;

        public EPixelInternalFormat InternalFormat
        {
            get => _internalFormat;
            set => SetField(ref _internalFormat, value);
        }

        [MemoryPackIgnore]
        public XRDataBuffer? StreamingPBO
        {
            get => _streamingPBO;
            set => SetField(ref _streamingPBO, value);
        }

        public void Resize(uint width, uint height, bool ignoreImage = false)
        {
            if (Data is not null && Data.Length != 0 && Width != 0u && Height != 0u)
            {
                if (ignoreImage)
                {
                    Width = width;
                    Height = height;
                    Data = new DataSource(XRTexture.AllocateBytes(width, height, PixelFormat, PixelType));
                }
                else
                {
                    try
                    {
                        using var img = GetImage();
                        img.Resize(width, height);
                        SetFromImage(img);
                    }
                    catch (MagickException)
                    {
                        Width = width;
                        Height = height;
                        Data = new DataSource(XRTexture.AllocateBytes(width, height, PixelFormat, PixelType));
                    }
                }
            }
            else
            {
                Width = width;
                Height = height;
            }
        }
        public void InterpolativeResize(uint width, uint height, PixelInterpolateMethod method)
        {
            if (Data is not null && Data.Length != 0 && Width != 0u && Height != 0u)
            {
                using var img = GetImage();
                img.InterpolativeResize(width, height, method);
                SetFromImage(img);
            }
            else
            {
                Width = width;
                Height = height;
            }
        }
        public void AdaptiveResize(uint width, uint height)
        {
            if (Data is not null && Data.Length != 0 && Width != 0u && Height != 0u)
            {
                using var img = GetImage();
                img.AdaptiveResize(width, height);
                SetFromImage(img);
            }
            else
            {
                Width = width;
                Height = height;
            }
        }
        public async Task ResizeAsync(uint width, uint height)
        {
            await Task.Run(() => Resize(width, height));
        }
        public async Task InterpolativeResizeAsync(uint width, uint height, PixelInterpolateMethod method)
        {
            await Task.Run(() => InterpolativeResize(width, height, method));
        }
        public async Task AdaptiveResizeAsync(uint width, uint height)
        {
            await Task.Run(() => AdaptiveResize(width, height));
        }

        public Mipmap2D Clone(bool cloneImage)
            => new()
            {
                InternalFormat = InternalFormat,
                PixelFormat = PixelFormat,
                PixelType = PixelType,
                Data = cloneImage ? Data?.Clone() : Data,
                Width = Width,
                Height = Height
            };

        public uint GetDataLength()
            => Data?.Length ?? 0u;

        public unsafe void FillData(void* ptr)
        {
            if (Data is null)
                return;
            
            uint len = GetDataLength();
            Buffer.MemoryCopy(Data.Address.Pointer, ptr, len, len);
        }

        public bool HasData()
            => Data is not null && Data.Length != 0;
    }
}

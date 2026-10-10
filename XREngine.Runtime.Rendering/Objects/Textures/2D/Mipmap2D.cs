using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Imaging;
using XREngine.Execution;
using MemoryPack;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Rendering
{
    /// <summary>
    /// Defines raw image data for a 2D texture mipmap.
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
        public Mipmap2D(RuntimeImage? image)
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
            if (change && propName is nameof(Data) or nameof(DataBytes) or nameof(Width) or nameof(Height)
                or nameof(PixelFormat) or nameof(PixelType) or nameof(InternalFormat))
                Interlocked.Increment(ref _resizeRevision);
            if (change && propName == nameof(Data) && Data is not null)
                Data.Dispose();
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {

        }

        public static explicit operator Mipmap2D(RuntimeImage image)
        {
            Mipmap2D mip = new();
            mip.SetFromImage(image);
            return mip;
        }

        public static explicit operator RuntimeImage(Mipmap2D mipmap)
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
        /// <summary>Copies image rows synchronously into this mipmap's data source.</summary>
        public unsafe void SetFromImage(RuntimeImage image)
        {
            ArgumentNullException.ThrowIfNull(image);
            int rowBytes = checked((int)((long)image.Width * RuntimeImage.GetBytesPerPixel(image.Format, image.Type)));
            int byteCount = checked(rowBytes * (int)image.Height);
            DataSource copied = DataSource.Allocate((uint)byteCount);
            bool assigned = false;
            try
            {
                ReadOnlySpan<byte> source = image.Pixels.Span;
                for (int row = 0; row < image.Height; row++)
                {
                    int sourceRow = image.Origin == RuntimeImageOrigin.BottomLeft
                        ? checked((int)image.Height - row - 1)
                        : row;
                    source.Slice(sourceRow * image.RowStrideBytes, rowBytes)
                        .CopyTo(new Span<byte>((byte*)copied.Address + row * rowBytes, rowBytes));
                }

                InternalFormat = (image.Format, image.Type) switch
                {
                    (EPixelFormat.Rgba or EPixelFormat.Bgra, EPixelType.UnsignedByte) => EPixelInternalFormat.Rgba8,
                    (EPixelFormat.Rgb or EPixelFormat.Bgr, EPixelType.UnsignedByte) => EPixelInternalFormat.Rgb8,
                    (EPixelFormat.Rgba, EPixelType.Float) => EPixelInternalFormat.Rgba32f,
                    (EPixelFormat.Red, EPixelType.Float) => EPixelInternalFormat.R32f,
                    _ => InternalFormat,
                };
                PixelFormat = image.Format;
                PixelType = image.Type;
                Width = image.Width;
                Height = image.Height;
                Data = copied;
                assigned = true;
            }
            finally
            {
                if (!assigned)
                    copied.Dispose();
            }
        }

        /// <summary>Returns an independent snapshot of this mipmap's pixels.</summary>
        public RuntimeImage GetImage()
        {
            byte[] bytes = Data?.GetBytes()
                ?? XRTexture.AllocateBytes(Width, Height, PixelFormat, PixelType);
            return new RuntimeImage(Width, Height, PixelFormat, PixelType, bytes);
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
        private int _resizeRevision;
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
            if (Data is null || Data.Length == 0 || Width == 0 || Height == 0)
            {
                Width = width;
                Height = height;
                return;
            }
            if (ignoreImage)
            {
                Width = width;
                Height = height;
                Data = new DataSource(XRTexture.AllocateBytes(width, height, PixelFormat, PixelType));
                return;
            }
            ResizeWithBackend(width, height, RuntimeImageResizeMode.Standard);
        }

        public void InterpolativeResize(uint width, uint height, RuntimeImageResizeMode mode)
            => ResizeWithBackend(width, height, mode);

        public void AdaptiveResize(uint width, uint height)
            => ResizeWithBackend(width, height, RuntimeImageResizeMode.Adaptive);

        private void ResizeWithBackend(uint width, uint height, RuntimeImageResizeMode mode)
        {
            if (Data is null || Data.Length == 0 || Width == 0 || Height == 0)
            {
                Width = width;
                Height = height;
                return;
            }

            using RuntimeImage source = GetImage();
            using RuntimeImage resized = RuntimeImageCodecs.Require().Resize(source, width, height, mode);
            SetFromImage(resized);
        }

        public Task ResizeAsync(uint width, uint height)
            => UseCallerThreadResize()
                ? ResizeOnCallerThreadAsync(width, height, RuntimeImageResizeMode.Standard)
                : Task.Run(() => Resize(width, height));

        public Task InterpolativeResizeAsync(uint width, uint height, RuntimeImageResizeMode mode)
            => UseCallerThreadResize()
                ? ResizeOnCallerThreadAsync(width, height, mode)
                : Task.Run(() => InterpolativeResize(width, height, mode));

        public Task AdaptiveResizeAsync(uint width, uint height)
            => UseCallerThreadResize()
                ? ResizeOnCallerThreadAsync(width, height, RuntimeImageResizeMode.Adaptive)
                : Task.Run(() => AdaptiveResize(width, height));

        private static bool UseCallerThreadResize()
            => OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread;

        /// <summary>
        /// Owns a request-time pixel snapshot until the caller job finishes. Another resize
        /// or an observed source change invalidates queued work before publication begins.
        /// </summary>
        private async Task ResizeOnCallerThreadAsync(uint width, uint height, RuntimeImageResizeMode mode)
        {
            JobManager jobs = RuntimeWorkScheduler.CaptureCallerThreadJobs();
            int revision = Interlocked.Increment(ref _resizeRevision);
            DataSource? input = Data;
            if (input?.IsDisposed == true)
                throw new OperationCanceledException("The mipmap resize source was disposed before the request.");

            uint inputLength = input?.Length ?? 0;
            VoidPtr inputAddress = input?.Address ?? VoidPtr.Zero;
            uint inputWidth = Width;
            uint inputHeight = Height;
            EPixelFormat inputFormat = PixelFormat;
            EPixelType inputType = PixelType;
            EPixelInternalFormat inputInternalFormat = InternalFormat;
            bool hasImage = input is { Length: > 0 } && inputWidth > 0 && inputHeight > 0;
            using RuntimeImage? source = hasImage ? GetImage() : null;

            bool IsCurrent()
                => Volatile.Read(ref _resizeRevision) == revision
                    && ReferenceEquals(Data, input)
                    && (input is null || (!input.IsDisposed
                        && input.Length == inputLength && input.Address == inputAddress))
                    && Width == inputWidth && Height == inputHeight
                    && PixelFormat == inputFormat && PixelType == inputType
                    && InternalFormat == inputInternalFormat;

            bool superseded = false;
            ActionJob job = new(() =>
            {
                if (!IsCurrent())
                {
                    superseded = true;
                    return;
                }

                if (source is null)
                {
                    Width = width;
                    Height = height;
                    return;
                }

                using RuntimeImage resized = RuntimeImageCodecs.Require().Resize(source, width, height, mode);
                if (!IsCurrent())
                {
                    superseded = true;
                    return;
                }

                SetFromImage(resized);
            });
            JobHandle handle = jobs.Schedule(job);
            await handle.WaitAsync();
            if (superseded)
                throw new OperationCanceledException("The mipmap resize was superseded before publication.");
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

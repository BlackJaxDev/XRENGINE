using ImageMagick;
using ImageMagick.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Diagnostics;

namespace XREngine.Rendering
{
    [XR3rdPartyExtensions("png", "jpg", "jpeg", "tif", "tiff", "tga", "exr", "hdr")]
    public partial class XRTexture2D : XRTexture, IFrameBufferAttachement
    {
        public override void Reload(string path)
        {
            Load3rdParty(path);
        }
        public override bool Load3rdParty(string filePath)
        {
            try
            {
                var sourceImage = new MagickImage(filePath);
                Mipmaps = GetMipmapsFromImage(sourceImage);
                AutoGenerateMipmaps = false;
                Resizable = false;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to load texture from '{filePath}'. Falling back to filler texture. {ex.Message}");
                AssetDiagnostics.RecordMissingAsset(filePath, nameof(XRTexture2D), $"{nameof(XRTexture2D)}.{nameof(Load3rdParty)}");

                Mipmaps = [new Mipmap2D(new MagickImage(FillerImage))];
                AutoGenerateMipmaps = true;
                Resizable = true;
                return false;
            }
        }

        public static EnumeratorJob ScheduleLoadJob(
            string filePath,
            XRTexture2D? texture = null,
            Action<XRTexture2D>? onFinished = null,
            Action<Exception>? onError = null,
            Action? onCanceled = null,
            Action<float>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path must be provided.", nameof(filePath));

            XRTexture2D target = texture ?? new XRTexture2D();
            target.FilePath ??= filePath;
            if (string.IsNullOrWhiteSpace(target.Name))
                target.Name = Path.GetFileNameWithoutExtension(filePath);

            IEnumerable Routine()
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                cancellationToken.ThrowIfCancellationRequested();
                bool _ = target.Load3rdParty(filePath);
                yield return new JobProgress(0.5f);

                cancellationToken.ThrowIfCancellationRequested();
                yield return UploadMipmapsViaPboAsync(target, cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                    onFinished?.Invoke(target);
            }

            return Engine.Jobs.Schedule(
                Routine(),
                progress: onProgress,
                completed: null,
                error: onError,
                canceled: onCanceled,
                progressWithPayload: null,
                cancellationToken: cancellationToken);
        }

        public static EnumeratorJob SchedulePreviewJob(
            string filePath,
            XRTexture2D? texture = null,
            uint maxPreviewSize = 128,
            Action<XRTexture2D>? onFinished = null,
            Action<Exception>? onError = null,
            Action? onCanceled = null,
            Action<float>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path must be provided.", nameof(filePath));

            XRTexture2D target = texture ?? new XRTexture2D();
            target.FilePath ??= filePath;
            if (string.IsNullOrWhiteSpace(target.Name))
                target.Name = Path.GetFileNameWithoutExtension(filePath);

            IEnumerable Routine()
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                Task previewTask = Task.Run(() =>
                {
                    var previewImage = new MagickImage(filePath);
                    if (maxPreviewSize > 0)
                    {
                        uint width = previewImage.Width;
                        uint height = previewImage.Height;
                        uint largest = Math.Max(width, height);

                        if (largest > maxPreviewSize)
                        {
                            double scale = maxPreviewSize / (double)largest;
                            uint scaledWidth = Math.Max(1u, (uint)Math.Round(width * scale));
                            uint scaledHeight = Math.Max(1u, (uint)Math.Round(height * scale));
                            previewImage.Resize(scaledWidth, scaledHeight);
                        }
                    }

                    target.Mipmaps = [new Mipmap2D(previewImage)];
                    target.AutoGenerateMipmaps = false;
                    target.Resizable = true;
                    target.SizedInternalFormat = ESizedInternalFormat.Rgba8;
                }, cancellationToken);

                yield return previewTask;

                if (previewTask.IsCanceled || cancellationToken.IsCancellationRequested)
                {
                    onCanceled?.Invoke();
                    yield break;
                }

                if (previewTask.IsFaulted)
                {
                    Exception? exception = previewTask.Exception?.InnerException;
                    exception ??= previewTask.Exception;
                    exception ??= new InvalidOperationException($"Failed to load preview for '{filePath}'.");
                    throw exception;
                }

                yield return new JobProgress(0.5f);

                yield return UploadMipmapsViaPboAsync(target, cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                    onFinished?.Invoke(target);
            }

            return Engine.Jobs.Schedule(
                Routine(),
                progress: onProgress,
                completed: null,
                error: onError,
                canceled: onCanceled,
                progressWithPayload: null,
                cancellationToken: cancellationToken);
        }

        private static Task UploadMipmapsViaPboAsync(XRTexture2D texture, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            void UploadAction()
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    texture.ShouldLoadDataFromInternalPBO = true;

                    var mipmaps = texture.Mipmaps;
                    if (mipmaps is not null && mipmaps.Length > 0)
                    {
                        for (int i = 0; i < mipmaps.Length; ++i)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                tcs.TrySetCanceled(cancellationToken);
                                return;
                            }
                            texture.LoadFromPBO(i);
                        }
                    }

                    texture.Generate();
                    texture.PushData();

                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            if (!Engine.InvokeOnMainThread(UploadAction))
                UploadAction();

            return tcs.Task;
        }

        public static Mipmap2D[] GetMipmapsFromImage(MagickImage image)
        {
            Mipmap2D[] mips = new Mipmap2D[GetSmallestMipmapLevel(image.Width, image.Height)];
            mips[0] = new Mipmap2D(image);
            uint w = image.Width;
            uint h = image.Height;
            for (int i = 1; i < mips.Length; ++i)
            {
                var clone = image.Clone();
                clone.Resize(w >> i, h >> i);
                mips[i] = new Mipmap2D(clone as MagickImage);
            }
            return mips;
        }

        private static MagickImage? _fillerImage = null;
        public static MagickImage FillerImage => _fillerImage ??= GetFillerBitmap();

        private static MagickImage GetFillerBitmap()
        {
            string path = Path.Combine(Engine.GameSettings.TexturesFolder, "Filler.png");
            if (File.Exists(path))
                return new MagickImage(path);
            else
            {
                const int squareExtent = 4;
                const int dim = squareExtent * 2;

                // Create a checkerboard pattern image without using bitmap
                MagickImage img = new(MagickColors.Blue, dim, dim);
                img.Draw(new Drawables()
                    .FillColor(MagickColors.Red)
                    .Rectangle(0, 0, squareExtent, squareExtent)
                    .Rectangle(squareExtent, squareExtent, dim, dim));
                return img;
            }
        }

        private ESizedInternalFormat _sizedInternalFormat = ESizedInternalFormat.Rgba32f;
        private ETexMagFilter _magFilter = ETexMagFilter.Nearest;
        private ETexMinFilter _minFilter = ETexMinFilter.Nearest;
        private ETexWrapMode _uWrap = ETexWrapMode.Repeat;
        private ETexWrapMode _vWrap = ETexWrapMode.Repeat;
        private float _lodBias = 0.0f;
        private bool _resizable = true;
        private bool _exclusiveSharing = true;
        private GrabPassInfo? _grabPass;

        public override bool IsResizeable => Resizable;

        /// <summary>
        /// If false, calling resize will do nothing.
        /// Useful for repeating textures that must always be a certain size or textures that never need to be dynamically resized during the game.
        /// False by default.
        /// </summary>
        public bool Resizable
        {
            get => _resizable;
            set => SetField(ref _resizable, value);
        }

        public override uint MaxDimension => Math.Max(Width, Height);

        public XRTexture2D(Task<Image<Rgba32>?> loadTask)
        {
            Mipmaps = [new Mipmap2D(loadTask)];
        }
        public XRTexture2D(uint width, uint height, ColorF4 color)
        {
            Mipmaps = [new Mipmap2D(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true)
            {
                Data = new DataSource(ColorToBytes(width, height, color))
            }];
        }

        private static byte[] ColorToBytes(uint width, uint height, ColorF4 color)
        {
            byte[] data = new byte[width * height * 4];
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i] = (byte)(color.R * 255);
                data[i + 1] = (byte)(color.G * 255);
                data[i + 2] = (byte)(color.B * 255);
                data[i + 3] = (byte)(color.A * 255);
            }
            return data;
        }

        public XRTexture2D() : this(1u, 1u, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, true) { }
        public XRTexture2D(uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, int mipmapCount)
        {
            Mipmap2D[] mips = new Mipmap2D[mipmapCount];
            for (uint i = 0; i < mipmapCount; ++i)
            {
                Mipmap2D mipmap = new()
                {
                    InternalFormat = internalFormat,
                    PixelFormat = format,
                    PixelType = type,
                    Width = width,
                    Height = height,
                    Data = new DataSource(AllocateBytes(width, height, format, type))
                };
                width >>= 1;
                height >>= 1;
                mips[i] = mipmap;
            }
            Mipmaps = mips;
        }

        public XRTexture2D(params string[] mipMapPaths)
        {
            Mipmap2D[] mips = new Mipmap2D[mipMapPaths.Length];
            for (int i = 0; i < mipMapPaths.Length; ++i)
            {
                string path = mipMapPaths[i];
                if (path.StartsWith("file://"))
                    path = path[7..];
                try
                {
                    mips[i] = new Mipmap2D(new MagickImage(path));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to load texture from path: {path}{Environment.NewLine}{e.Message}");
                }
            }
            Mipmaps = mips;
        }
        public XRTexture2D(uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData = false)
        {
            Mipmaps = [new Mipmap2D() 
            {
                InternalFormat = internalFormat,
                PixelFormat = format,
                PixelType = type,
                Width = width,
                Height = height,
                Data = allocateData ? new DataSource(AllocateBytes(width, height, format, type)) : null
            }];
        }
        public XRTexture2D(uint width, uint height, params MagickImage?[] mipmaps)
        {
            Mipmap2D[] mips = new Mipmap2D[mipmaps.Length];
            for (int i = 0; i < mipmaps.Length; ++i)
            {
                var image = mipmaps[i];
                image?.Resize(width >> i, height >> i);
                mips[i] = new Mipmap2D(image);
            }
            Mipmaps = mips;
        }
        public XRTexture2D(MagickImage? image)
        {
            Mipmaps = [new Mipmap2D(image)];
        }
        public XRTexture2D(Image<Rgba32> image)
        {
            Mipmaps = [new Mipmap2D(image)];
        }

        private Mipmap2D[] _mipmaps = [];
        public Mipmap2D[] Mipmaps
        {
            get => _mipmaps;
            set => SetField(ref _mipmaps, value);
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
        public GrabPassInfo? GrabPass
        {
            get => _grabPass;
            set => SetField(ref _grabPass, value);
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

        public uint Width => Mipmaps.Length > 0 ? Mipmaps[0].Width : 0;
        public uint Height => Mipmaps.Length > 0 ? Mipmaps[0].Height : 0;

        /// <summary>
        /// Set on construction
        /// </summary>
        public bool Rectangle { get; set; } = false;
        public bool MultiSample => MultiSampleCount > 1;

        private uint _multiSampleCount = 1;
        /// <summary>
        /// Set on construction
        /// </summary>
        public uint MultiSampleCount
        {
            get => _multiSampleCount;
            set => SetField(ref _multiSampleCount, value);
        }

        private bool _fixedSampleLocations = true;
        /// <summary>
        /// Specifies whether the image will use identical sample locations 
        /// and the same number of samples for all texels in the image,
        /// and the sample locations will not depend on the internal format or size of the image.
        /// </summary>
        public bool FixedSampleLocations
        {
            get => _fixedSampleLocations;
            set => SetField(ref _fixedSampleLocations, value);
        }

        public bool ExclusiveSharing
        {
            get => _exclusiveSharing;
            set => SetField(ref _exclusiveSharing, value);
        }

        /// <summary>
        /// Resizes the textures stored in memory.
        /// Does nothing if Resizeable is false.
        /// </summary>
        public void Resize(uint width, uint height)
        {
            if (Width == width && Height == height || _mipmaps is null || _mipmaps.Length <= 0)
                return;

            for (int i = 0; i < _mipmaps.Length && width > 0u && height > 0u; ++i)
            {
                if (_mipmaps[i] is null)
                    continue;

                _mipmaps[i]?.Resize(width, height);

                width >>= 1;
                height >>= 1;
            }

            Resized?.Invoke();
        }

        public event Action? Resized;

        /// <summary>
        /// Generates mipmaps from the base texture.
        /// </summary>
        public void GenerateMipmapsCPU()
        {
            if (_mipmaps is null || _mipmaps.Length <= 0)
                return;

            Mipmaps = GetMipmapsFromImage(_mipmaps[0].GetImage());
        }

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="name">The name of the texture.</param>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="internalFmt">The internal texture storage format.</param>
        /// <param name="format">The format of the texture's pixels.</param>
        /// <param name="pixelType">How pixels are stored.</param>
        /// <param name="bufAttach">Where to attach to the framebuffer for rendering to.</param>
        /// <returns>A new 2D texture reference.</returns>
        public static XRTexture2D CreateFrameBufferTexture(uint width, uint height,
            EPixelInternalFormat internalFmt, EPixelFormat format, EPixelType type, EFrameBufferAttachment bufAttach)
            => new(width, height, internalFmt, format, type, false)
            {
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
                FrameBufferAttachment = bufAttach,
            };

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="bounds"></param>
        /// <param name="internalFormat"></param>
        /// <param name="format"></param>
        /// <param name="pixelType"></param>
        /// <returns></returns>
        public static XRTexture2D CreateFrameBufferTexture(IVector2 bounds, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type)
            => CreateFrameBufferTexture((uint)bounds.X, (uint)bounds.Y, internalFormat, format, type);
        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="name">The name of the texture.</param>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="internalFmt">The internal texture storage format.</param>
        /// <param name="format">The format of the texture's pixels.</param>
        /// <param name="pixelType">How pixels are stored.</param>
        /// <returns>A new 2D texture reference.</returns>
        public static XRTexture2D CreateFrameBufferTexture(uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type)
            => new(width, height, internalFormat, format, type, false)
            {
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
            };

        private XRDataBuffer? _pbo;

        public bool ShouldLoadDataFromInternalPBO
        {
            get => _pbo != null;
            set
            {
                if (value)
                {
                    if (_pbo == null)
                    {
                        _pbo = new XRDataBuffer(EBufferTarget.PixelUnpackBuffer, true);
                        _pbo.Generate();
                    }
                }
                else
                {
                    if (_pbo != null)
                    {
                        _pbo.Destroy();
                        _pbo = null;
                    }
                }
            }
        }

        public override bool HasAlphaChannel
            => Mipmaps.Any(HasAlpha);

        public override Vector3 WidthHeightDepth
            => new(Width, Height, 1);

        private static bool HasAlpha(Mipmap2D x) => x.PixelFormat switch
        {
            EPixelFormat.Bgra or
            EPixelFormat.Rgba or
            EPixelFormat.LuminanceAlpha => true,
            _ => false,
        };

        public unsafe void LoadFromPBO(int mipIndex)
        {
            if (_pbo is null)
                return;

            if (Mipmaps is null || mipIndex < 0 || mipIndex >= Mipmaps.Length)
                return;

            var mip = Mipmaps[mipIndex];
            var data = mip.Data;
            if (data is null)
                return;

            _pbo.MapBufferData();
            _pbo.SetDataPointer(data.Address);
            _pbo.UnmapBufferData();

            mip.StreamingPBO = _pbo;
        }

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// Resizes the texture to a scale of the current viewport size.
        /// </summary>
        /// <param name="resizeScale"></param>
        /// <param name="readBuffer"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        /// <returns></returns>
        public static XRTexture2D CreateGrabPassTextureResized(
            float resizeScale = 1.0f,
            EReadBufferMode readBuffer = EReadBufferMode.ColorAttachment0,
            bool colorBit = true,
            bool depthBit = false,
            bool stencilBit = false,
            bool linearFilter = true)
        {
            XRTexture2D t = CreateFrameBufferTexture(1, 1, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.GrabPass = new GrabPassInfo(t, readBuffer, colorBit, depthBit, stencilBit, linearFilter, true, resizeScale);
            return t;
        }

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// Keeps the texture at the same size as when it was created.
        /// </summary>
        /// <param name="w"></param>
        /// <param name="h"></param>
        /// <param name="readBuffer"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        /// <returns></returns>
        public static XRTexture2D CreateGrabPassTextureStatic(
            uint w,
            uint h,
            EReadBufferMode readBuffer = EReadBufferMode.ColorAttachment0,
            bool colorBit = true,
            bool depthBit = false,
            bool stencilBit = false,
            bool linearFilter = true)
        {
            XRTexture2D t = CreateFrameBufferTexture(w, h, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
            t.MinFilter = ETexMinFilter.Linear;
            t.MagFilter = ETexMagFilter.Linear;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.GrabPass = new GrabPassInfo(t, readBuffer, colorBit, depthBit, stencilBit, linearFilter, false, 1.0f);
            return t;
        }
    }
}

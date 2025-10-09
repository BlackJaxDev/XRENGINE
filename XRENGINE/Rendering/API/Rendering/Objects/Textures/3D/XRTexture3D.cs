using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTexture3D : XRTexture
    {
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

        public override Vector3 WidthHeightDepth => new(Width, Height, Depth);

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

        private uint _width;
        private uint _height;
        private uint _depth;

        public uint Width => _width;
        public uint Height => _height;
        public uint Depth => _depth;

        public event Action? Resized;

        public XRTexture3D() : this(1, 1, 1, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, true) { }

        public XRTexture3D(uint width, uint height, uint depth) 
            : this(width, height, depth, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, true) { }

        public XRTexture3D(uint width, uint height, uint depth, ColorF4 color)
        {
            _width = width;
            _height = height;
            _depth = depth;
            
            // Create a simple 3D texture filled with the specified color
            // For now, we'll just set the dimensions and let the renderer handle the data
            // In a full implementation, you might want to create actual 3D data here
        }

        public XRTexture3D(uint width, uint height, uint depth, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData = false)
        {
            _width = width;
            _height = height;
            _depth = depth;
            
            // Set default properties
            _sizedInternalFormat = internalFormat switch
            {
                EPixelInternalFormat.Rgb8 => ESizedInternalFormat.Rgb8,
                EPixelInternalFormat.Rgba8 => ESizedInternalFormat.Rgba8,
                //EPixelInternalFormat.Rgb16 => ESizedInternalFormat.Rgb16,
                EPixelInternalFormat.Rgba16 => ESizedInternalFormat.Rgba16,
                EPixelInternalFormat.Rgb32f => ESizedInternalFormat.Rgb32f,
                EPixelInternalFormat.Rgba32f => ESizedInternalFormat.Rgba32f,
                _ => ESizedInternalFormat.Rgba8
            };
            
            // In a full implementation, you might want to create actual 3D mipmap data here
            // similar to how XRTexture2D creates Mipmap2D arrays
        }

        public XRTexture3D(uint width, uint height, uint depth, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, int mipmapCount)
        {
            _width = width;
            _height = height;
            _depth = depth;
            
            // Set default properties
            _sizedInternalFormat = internalFormat switch
            {
                EPixelInternalFormat.Rgb8 => ESizedInternalFormat.Rgb8,
                EPixelInternalFormat.Rgba8 => ESizedInternalFormat.Rgba8,
                //EPixelInternalFormat.Rgb16 => ESizedInternalFormat.Rgb16,
                EPixelInternalFormat.Rgba16 => ESizedInternalFormat.Rgba16,
                EPixelInternalFormat.Rgb32f => ESizedInternalFormat.Rgb32f,
                EPixelInternalFormat.Rgba32f => ESizedInternalFormat.Rgba32f,
                _ => ESizedInternalFormat.Rgba8
            };
            
            // In a full implementation, you would create mipmapCount levels of 3D data
            // with each level having dimensions divided by 2
        }

        /// <summary>
        /// Resizes the textures stored in memory.
        /// Does nothing if Resizeable is false.
        /// </summary>
        public void Resize(uint width, uint height, uint depth, bool resizeRenderTexture = true)
        {
            if (!Resizable)
                return;

            _width = width;
            _height = height;
            _depth = depth;

            Resized?.Invoke();
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
    }
}

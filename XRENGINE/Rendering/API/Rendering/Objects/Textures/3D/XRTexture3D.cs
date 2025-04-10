using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTexture3D : XRTexture
    {
        //public XRTexture3D() : this(null, 1, 1, 1) { }
        //public XRTexture3D(int width, int height, int depth)
        //{
        //    _mipmaps = null;
        //    _width = width;
        //    _height = height;
        //    _internalFormat = EPixelInternalFormat.Rgba8;
        //    _pixelFormat = EPixelFormat.Bgra;
        //    _pixelType = EPixelType.UnsignedByte;
        //}
        //public XRTexture3D(string name, int width, int height, int depth,
        //    ETPixelCompFmt bitmapFormat = ETPixelCompFmt.F16, int mipCount = 1)
        //    : this(name, width, height, depth)
        //{
        //    _mipmaps = new Bitmap3D[mipCount];
        //    for (int i = 0, scale = 1; i < mipCount; scale = 1 << ++i)
        //    {
        //        Bitmap3D tref = new(width / scale, height / scale, depth / scale, ETPixelType.Basic);
        //        _mipmaps[i] = tref;
        //    }

        //    DetermineTextureFormat();
        //}
        //public XRTexture3D(string name, int width, int height, int depth,
        //    EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType)
        //    : this(name, width, height, depth)
        //{
        //    _mipmaps = null;
        //    _internalFormat = internalFormat;
        //    _pixelFormat = pixelFormat;
        //    _pixelType = pixelType;
        //    _width = width;
        //    _height = height;
        //}
        ////public TexRef3D(string name, int width, int height, int depth,
        ////    EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType)
        ////    : this(name, width, height, depth, internalFormat, pixelFormat, pixelType)
        ////{
        ////    _mipmaps = new GlobalFileRef<TBitmap3D>[] { new TBitmap3D(width, height, depth, pixelType) };
        ////}
        //public XRTexture3D(params string[] mipMapPaths)
        //{
        //    _mipmaps = new Bitmap3D[mipMapPaths.Length];
        //    for (int i = 0; i < mipMapPaths.Length; ++i)
        //    {
        //        string path = mipMapPaths[i];
        //        if (path.StartsWith("file://"))
        //            path = path.Substring(7);
        //        _mipmaps =
        //        [
        //            new TBitmap3D(path)
        //        ];
        //    }
        //}

        //public Bitmap3D[] _mipmaps;
        //public Bitmap3D[] Mipmaps
        //{
        //    get => _mipmaps;
        //    set => SetField(ref _mipmaps, value);
        //}

        //protected RenderTex3D _texture;

        //protected int _width;
        //protected int _height;
        //protected int _depth;

        //private EPixelFormat _pixelFormat = EPixelFormat.Rgba;
        //private EPixelType _pixelType = EPixelType.UnsignedByte;
        //private EPixelInternalFormat _internalFormat = EPixelInternalFormat.Rgba8;

        //public EPixelFormat PixelFormat
        //{
        //    get => _pixelFormat;
        //    set
        //    {
        //        _pixelFormat = value;
        //        if (_texture != null)
        //        {
        //            _texture.PixelFormat = _pixelFormat;
        //            _texture.PushData();
        //        }
        //    }
        //}

        //public EPixelType PixelType
        //{
        //    get => _pixelType;
        //    set
        //    {
        //        _pixelType = value;
        //        if (_texture != null)
        //        {
        //            _texture.PixelType = _pixelType;
        //            _texture.PushData();
        //        }
        //    }
        //}

        //public EPixelInternalFormat InternalFormat
        //{
        //    get => _internalFormat;
        //    set
        //    {
        //        _internalFormat = value;
        //        if (_texture != null)
        //        {
        //            _texture.InternalFormat = _internalFormat;
        //            _texture.PushData();
        //        }
        //    }
        //}

        /// <summary>
        /// If false, calling resize will do nothing.
        /// Useful for repeating textures that must always be a certain size or textures that never need to be dynamically resized during the game.
        /// False by default.
        /// </summary>
        public bool Resizable { get; set; } = true;

        public override Vector3 WidthHeightDepth => new(Width, Height, Depth);

        public EDepthStencilFmt DepthStencilFormat { get; set; } = EDepthStencilFmt.None;

        public ETexMagFilter MagFilter { get; set; } = ETexMagFilter.Nearest;

        public ETexMinFilter MinFilter { get; set; } = ETexMinFilter.Nearest;

        public ETexWrapMode UWrap { get; set; } = ETexWrapMode.Repeat;

        public ETexWrapMode VWrap { get; set; } = ETexWrapMode.Repeat;

        public ETexWrapMode WWrap { get; set; } = ETexWrapMode.Repeat;

        public float LodBias { get; set; } = 0.0f;

        private int _width;
        private int _height;
        private int _depth;

        public int Width => _width;
        public int Height => _height;
        public int Depth => _depth;

        //private bool _isLoading = false;

        //public async Task<RenderTex3D> GetTextureAsync()
        //{
        //    if (_texture != null)
        //        return _texture;

        //    if (!_isLoading)
        //        await Task.Run(LoadMipmaps);

        //    return _texture;
        //}
        //public RenderTex3D GetTexture(bool loadSynchronously = false)
        //{
        //    if (_texture != null)
        //        return _texture;

        //    if (!_isLoading)
        //    {
        //        if (loadSynchronously)
        //        {
        //            LoadMipmaps();
        //            return _texture;
        //        }
        //        else
        //        {
        //            GetTextureAsync().ContinueWith(task => _texture = task.Result);
        //        }
        //    }

        //    return _texture;
        //}

        /// <summary>
        /// Resizes the textures stored in memory.
        /// Does nothing if Resizeable is false.
        /// </summary>
        public void Resize(int width, int height, int depth, bool resizeRenderTexture = true)
        {
            if (!Resizable)
                return;

            _width = width;
            _height = height;
            _depth = depth;

            //_mipmaps?.ForEach(x => x.File?.Resize(width, height, depth));
        }

        //public override int MaxDimension { get; }

        ///// <summary>
        ///// Call if you want to load all mipmap texture files, in a background thread for example.
        ///// </summary>
        //public void LoadMipmaps()
        //{
        //    _isLoading = true;
        //    _mipmaps?.ForEach(tex => tex?.GetInstance());
        //    DetermineTextureFormat(false);
        //    CreateRenderTexture();
        //    _isLoading = false;
        //}
        ///// <summary>
        ///// Decides the best internal format, pixel format, and pixel type for the stored mipmaps.
        ///// </summary>
        ///// <param name="force">If true, sets the formats/type even if the mipmaps are loaded.</param>
        //public void DetermineTextureFormat(bool force = true)
        //{
        //    if (_mipmaps != null && _mipmaps.Length > 0)
        //    {
        //        var tref = _mipmaps[0];
        //        //if (!tref.IsLoaded && !force)
        //        //    return;
        //        var t = tref.File;
        //        if (t != null)
        //        {
        //            //switch (t.Format)
        //            //{
        //            //    case System.Drawing.Imaging.PixelFormat.Format24bppRgb:
        //            //        InternalFormat = EPixelInternalFormat.Rgb8;
        //            //        PixelFormat = EPixelFormat.Bgr;
        //            //        PixelType = EPixelType.UnsignedByte;
        //            //        break;
        //            //    case System.Drawing.Imaging.PixelFormat.Format32bppRgb:
        //            //        InternalFormat = EPixelInternalFormat.Rgb8;
        //            //        PixelFormat = EPixelFormat.Bgra;
        //            //        PixelType = EPixelType.UnsignedByte;
        //            //        break;
        //            //    case System.Drawing.Imaging.PixelFormat.Format32bppArgb:
        //            //    case System.Drawing.Imaging.PixelFormat.Format32bppPArgb:
        //            //        InternalFormat = EPixelInternalFormat.Rgba8;
        //            //        PixelFormat = EPixelFormat.Bgra;
        //            //        PixelType = EPixelType.UnsignedByte;
        //            //        break;
        //            //    case System.Drawing.Imaging.PixelFormat.Format64bppArgb:
        //            //    case System.Drawing.Imaging.PixelFormat.Format64bppPArgb:
        //            //        InternalFormat = EPixelInternalFormat.Rgba16;
        //            //        PixelFormat = EPixelFormat.Bgra;
        //            //        PixelType = EPixelType.UnsignedShort;
        //            //        break;
        //            //}
        //        }
        //    }
        //}
        //protected virtual void CreateRenderTexture()
        //{
        //    if (_texture != null)
        //    {
        //        _texture.PostPushData -= SetParameters;
        //        _texture.Destroy();
        //    }

        //    if (_mipmaps != null && _mipmaps.Length > 0)
        //        _texture = new RenderTex3D(InternalFormat, PixelFormat, PixelType, _mipmaps.Select(x => x.File).ToArray())
        //        {
        //            Resizable = Resizable,
        //        };
        //    else
        //        _texture = new RenderTex3D(_width, _height, _depth, InternalFormat, PixelFormat, PixelType)
        //        {
        //            Resizable = Resizable
        //        };

        //    _texture.PostPushData += SetParameters;
        //}
        public override uint MaxDimension { get; }
    }
}

using ImageMagick;
using Silk.NET.Core.Attributes;
using Silk.NET.Core.Native;
using Silk.NET.Windowing;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms.Rotations;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering
{
    /// <summary>
    /// An abstract window renderer handles rendering to a specific window using a specific graphics API.
    /// </summary>
    public abstract unsafe class AbstractRenderer : XRBase//, IDisposable
    {
        /// <summary>
        /// If true, this renderer is currently being used to render a window.
        /// </summary>
        public bool Active { get; internal set; } = false;

        public static readonly Vector3 UIPositionBias = new(0.0f, 0.0f, 0.1f);
        public static readonly Rotator UIRotation = new(90.0f, 0.0f, 0.0f, ERotationOrder.YPR);

        protected AbstractRenderer(XRWindow window, bool shouldLinkWindow = true)
        {
            _window = window;

            //Set the initial object cache for this window of all existing render objects
            lock (_roCacheLock)
                _renderObjectCache = Engine.Rendering.CreateObjectsForNewRenderer(this);
        }

        private readonly object _roCacheLock = new();
        private readonly ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> _renderObjectCache = [];
        public IReadOnlyDictionary<GenericRenderObject, AbstractRenderAPIObject> RenderObjectCache => _renderObjectCache;

        private readonly Stack<BoundingRectangle> _renderAreaStack = new();
        public BoundingRectangle CurrentRenderArea
            => _renderAreaStack.Count > 0 
            ? _renderAreaStack.Peek()
            : new BoundingRectangle(0, 0, Window.Size.X, Window.Size.Y);

        public void FrameBufferInvalidated()
        {
            _frameBufferInvalidated = true;
        }
        protected bool _frameBufferInvalidated = false;

        public IWindow Window => XRWindow.Window;

        private XRWindow _window;
        public XRWindow XRWindow
        {
            get => _window;
            protected set => _window = value;
        }

        /// <summary>
        /// Use this to retrieve the currently rendering window renderer.
        /// </summary>
        public static AbstractRenderer? Current { get; internal set; }

        protected Dictionary<string, bool> _verifiedExtensions = [];
        protected void LogExtension(string name, bool exists)
            => _verifiedExtensions.Add(name, exists);
        protected bool ExtensionChecked(string name)
        {
            _verifiedExtensions.TryGetValue(name, out bool exists);
            return exists;
        }

        public static byte* ToAnsi(string str)
            => (byte*)Marshal.StringToHGlobalAnsi(str);
        public static string? FromAnsi(byte* ptr)
            => Marshal.PtrToStringAnsi((nint)ptr);

        public abstract void Initialize();
        public abstract void CleanUp();

        protected abstract void WindowRenderCallback(double delta);
        protected virtual void MainLoop() => Window?.Run();

        public const float DefaultPointSize = 5.0f;
        public const float DefaultLineSize = 1.0f;

        public abstract void CropRenderArea(BoundingRectangle region);
        public abstract void SetRenderArea(BoundingRectangle region);
        public abstract void SetCroppingEnabled(bool enabled);

        /// <summary>
        /// Gets or creates a new API-specific render object linked to this window renderer from a generic render object.
        /// </summary>
        /// <param name="renderObject"></param>
        /// <returns></returns>
        public AbstractRenderAPIObject? GetOrCreateAPIRenderObject(GenericRenderObject? renderObject, bool generateNow = false)
        {
            if (renderObject is null)
                return null;

            AbstractRenderAPIObject? obj;
            lock (_roCacheLock)
            {
                obj = _renderObjectCache.GetOrAdd(renderObject, _ => CreateAPIRenderObject(renderObject));
                if (generateNow && !obj.IsGenerated)
                    obj.Generate();
            }

            return obj;
        }

        public bool TryGetAPIRenderObject(GenericRenderObject renderObject, out AbstractRenderAPIObject? apiObject)
        {
            if (renderObject is null)
            {
                apiObject = null;
                return false;
            }
            lock (_roCacheLock)
            {
                return _renderObjectCache.TryGetValue(renderObject, out apiObject);
            }
        }

        /// <summary>
        /// Converts a generic render object reference into a reference to the wrapper object for this specific renderer.
        /// A generic render object can have multiple wrappers wrapping it at a time, but only one per renderer.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="renderObject"></param>
        /// <returns></returns>
        public T? GenericToAPI<T>(GenericRenderObject? renderObject) where T : AbstractRenderAPIObject
            => GetOrCreateAPIRenderObject(renderObject) as T;

        /// <summary>
        /// Creates a new API-specific render object linked to this window renderer from a generic render object.
        /// </summary>
        /// <param name="renderObject"></param>
        /// <returns></returns>
        protected abstract AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject);

        public bool CalcDotLuminance(XRTexture2D texture, out float dotLuminance, bool genMipmapsNow)
            => CalcDotLuminance(texture, Engine.Rendering.Settings.DefaultLuminance, out dotLuminance, genMipmapsNow);
        public abstract bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow);
        public float CalculateDotLuminance(XRTexture2D texture, bool generateMipmapsNow)
            => CalcDotLuminance(texture, out float dotLum, generateMipmapsNow) ? dotLum : 1.0f;

        public bool CalcDotLuminance(XRTexture2DArray texture, out float dotLuminance, bool genMipmapsNow)
            => CalcDotLuminance(texture, Engine.Rendering.Settings.DefaultLuminance, out dotLuminance, genMipmapsNow);
        public abstract bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow);
        public float CalculateDotLuminance(XRTexture2DArray texture, bool generateMipmapsNow)
            => CalcDotLuminance(texture, out float dotLum, generateMipmapsNow) ? dotLum : 1.0f;

        public abstract void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true);
        public abstract void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true);

        public abstract void Clear(bool color, bool depth, bool stencil);
        public abstract void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo);
        public abstract void ClearColor(ColorF4 color);
        public abstract void SetReadBuffer(EReadBufferMode mode);
        public abstract void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode);
        public abstract float GetDepth(int x, int y);
        public abstract void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> colorCallback);
        public abstract void GetDepthAsync(XRFrameBuffer fbo, int x, int y, Action<float> depthCallback);
        public abstract byte GetStencilIndex(float x, float y);
        public abstract void EnableDepthTest(bool enable);
        public abstract void StencilMask(uint mask);
        public abstract void ClearStencil(int value);
        public abstract void ClearDepth(float value);
        public abstract void AllowDepthWrite(bool allow);
        public abstract void DepthFunc(EComparison always);

        public void Dispose()
        {
            //UnlinkWindow();
            //_viewports.Clear();
            //_currentCamera = null;
            //_worldInstance = null;
            //foreach (var obj in _renderObjectCache.Values)
            //    obj.Destroy();
            //_renderObjectCache.Clear();
            //GC.SuppressFinalize(this);
        }

        public abstract void DispatchCompute(XRRenderProgram program, int v1, int v2, int v3);

        public abstract void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage> imageCallback);

        /// <summary>
        /// Blits the contents of one framebuffer to another.
        /// </summary>
        /// <param name="inFBO"></param>
        /// <param name="outFBO"></param>
        /// <param name="readBufferMode"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        public void BlitFBOToFBO(
            XRFrameBuffer inFBO,
            XRFrameBuffer outFBO,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            Blit(
                inFBO,
                outFBO,
                inFBO.Width,
                inFBO.Height,
                outFBO.Width,
                outFBO.Height,
                readBufferMode,
                colorBit,
                depthBit,
                stencilBit,
                linearFilter);
        }

        /// <summary>
        /// Blits the contents of a viewport to a framebuffer.
        /// </summary>
        /// <param name="inViewport"></param>
        /// <param name="outFBO"></param>
        /// <param name="readBufferMode"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        public void BlitViewportToFBO(
            XRViewport inViewport,
            XRFrameBuffer outFBO,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            Blit(
                null,
                outFBO,
                inViewport.Region.X,
                inViewport.Region.Y,
                (uint)inViewport.Width,
                (uint)inViewport.Height,
                0,
                0,
                outFBO.Width,
                outFBO.Height,
                readBufferMode,
                colorBit,
                depthBit,
                stencilBit,
                linearFilter);
        }

        /// <summary>
        /// Blits the contents of a framebuffer to a viewport.
        /// </summary>
        /// <param name="inFBO"></param>
        /// <param name="outViewport"></param>
        /// <param name="readBufferMode"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        public void BlitFBOToViewport(
            XRFrameBuffer inFBO,
            XRViewport outViewport,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            Blit(
                inFBO,
                null,
                0,
                0,
                inFBO.Width,
                inFBO.Height,
                outViewport.Region.X,
                outViewport.Region.Y,
                (uint)outViewport.Width,
                (uint)outViewport.Height,
                readBufferMode,
                colorBit,
                depthBit,
                stencilBit,
                linearFilter);
        }

        /// <summary>
        /// Blits the contents of one viewport to another.
        /// Both viewports must be in the same window.
        /// </summary>
        /// <param name="inViewport"></param>
        /// <param name="outViewport"></param>
        /// <param name="readBufferMode"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        public void BlitViewportToViewport(
            XRViewport inViewport,
            XRViewport outViewport,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            Blit(
                null,
                null,
                inViewport.Region.X,
                inViewport.Region.Y,
                (uint)inViewport.Width,
                (uint)inViewport.Height,
                outViewport.Region.X,
                outViewport.Region.Y,
                (uint)outViewport.Width,
                (uint)outViewport.Height,
                readBufferMode,
                colorBit,
                depthBit,
                stencilBit,
                linearFilter);
        }

        /// <summary>
        /// Blits the contents of one framebuffer to another.
        /// </summary>
        /// <param name="inFBO"></param>
        /// <param name="outFBO"></param>
        /// <param name="inW"></param>
        /// <param name="inH"></param>
        /// <param name="outW"></param>
        /// <param name="outH"></param>
        /// <param name="readBufferMode"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        public void Blit(
            XRFrameBuffer? inFBO,
            XRFrameBuffer? outFBO,
            uint inW, uint inH,
            uint outW, uint outH,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            Blit(
                inFBO,
                outFBO,
                0,
                0,
                inW,
                inH,
                0,
                0,
                outW,
                outH,
                readBufferMode,
                colorBit,
                depthBit,
                stencilBit,
                linearFilter);
        }

        /// <summary>
        /// Blits the contents of one framebuffer to another.
        /// </summary>
        /// <param name="inFBO"></param>
        /// <param name="outFBO"></param>
        /// <param name="inX"></param>
        /// <param name="inY"></param>
        /// <param name="inW"></param>
        /// <param name="inH"></param>
        /// <param name="outX"></param>
        /// <param name="outY"></param>
        /// <param name="outW"></param>
        /// <param name="outH"></param>
        /// <param name="readBufferMode"></param>
        /// <param name="colorBit"></param>
        /// <param name="depthBit"></param>
        /// <param name="stencilBit"></param>
        /// <param name="linearFilter"></param>
        public abstract void Blit(
            XRFrameBuffer? inFBO,
            XRFrameBuffer? outFBO,
            int inX, int inY, uint inW, uint inH,
            int outX, int outY, uint outW, uint outH,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter);

        public abstract void MemoryBarrier(EMemoryBarrierMask mask);
    }
    [Flags]
    public enum EMemoryBarrierMask : int
    {
        None = 0,
        VertexAttribArray = 0x1,
        ElementArray = 0x2,
        Uniform = 0x4,
        TextureFetch = 0x8,
        ShaderGlobalAccess = 0x10,
        ShaderImageAccess = 0x20,
        Command = 0x40,
        PixelBuffer = 0x80,
        TextureUpdate = 0x100,
        BufferUpdate = 0x200,
        Framebuffer = 0x400,
        TransformFeedback = 0x800,
        AtomicCounter = 0x1000,
        ShaderStorage = 0x2000,
        ClientMappedBuffer = 0x4000,
        QueryBuffer = 0x8000,
        All = unchecked((int)0xFFFFFFFF),
    }
    public abstract unsafe partial class AbstractRenderer<TAPI>(XRWindow window, bool shouldLinkWindow = true) : AbstractRenderer(window, shouldLinkWindow) where TAPI : NativeAPI
    {
        ~AbstractRenderer() => _api?.Dispose();

        private TAPI? _api;
        protected TAPI Api 
        {
            get => _api ??= GetAPI();
            private set => _api = value;
        }
        protected abstract TAPI GetAPI();

        //protected void VerifyExt<T>(string name, ref T? output) where T : NativeExtension<TAPI>
        //{
        //    if (output is null && !ExtensionChecked(name))
        //        LogExtension(name, LoadExt(out output));
        //}
        //protected abstract bool LoadExt<T>(out T output) where T : NativeExtension<TAPI>?;
    }
}

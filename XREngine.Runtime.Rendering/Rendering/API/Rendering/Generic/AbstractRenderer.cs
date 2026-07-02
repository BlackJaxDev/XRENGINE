using ImageMagick;
using ImGuiNET;
using Silk.NET.Core.Native;
using Silk.NET.Windowing;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms.Rotations;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;

namespace XREngine.Rendering
{
    /// <summary>
    /// An abstract window renderer handles rendering to a specific window using a specific graphics API.
    /// </summary>
    public abstract unsafe class AbstractRenderer : XRBase, IRenderApiWrapperOwner, IRuntimeRendererHost//, IDisposable
    {
        #region Constants / Statics
        /// <summary>
        /// If true, this renderer is currently being used to render a window.
        /// </summary>
        public bool Active { get; internal set; } = false;

        public static readonly Vector3 UIPositionBias = new(0.0f, 0.0f, 0.1f);
        public static readonly Rotator UIRotation = new(90.0f, 0.0f, 0.0f, ERotationOrder.YPR);

        private static AbstractRenderer? _globalCurrent;
        [ThreadStatic]
        private static AbstractRenderer? _threadCurrent;
        [ThreadStatic]
        private static bool _hasThreadCurrentOverride;

        /// <summary>
        /// Use this to retrieve the currently rendering window renderer.
        /// </summary>
        public static AbstractRenderer? Current
        {
            get => _hasThreadCurrentOverride ? _threadCurrent : _globalCurrent;
            internal set
            {
                _threadCurrent = value;
                _hasThreadCurrentOverride = value is not null;
                _globalCurrent = value;
            }
        }

        internal static IDisposable PushThreadCurrent(AbstractRenderer? renderer)
            => new ThreadCurrentScope(renderer);

        private readonly struct ThreadCurrentScope : IDisposable
        {
            private readonly AbstractRenderer? _previousThreadCurrent;
            private readonly bool _previousHasThreadCurrentOverride;

            public ThreadCurrentScope(AbstractRenderer? renderer)
            {
                _previousThreadCurrent = _threadCurrent;
                _previousHasThreadCurrentOverride = _hasThreadCurrentOverride;
                _threadCurrent = renderer;
                _hasThreadCurrentOverride = true;
            }

            public void Dispose()
            {
                _threadCurrent = _previousThreadCurrent;
                _hasThreadCurrentOverride = _previousHasThreadCurrentOverride;
            }
        }

        public const float DefaultPointSize = 5.0f;
        public const float DefaultLineSize = 1.0f;
        #endregion

        #region Window / Lifecycle
        private XRWindow _window;

        protected AbstractRenderer(XRWindow window, bool shouldLinkWindow = true)
        {
            _window = window;

            //Set the initial object cache for this window of all existing render objects
            using (_roCacheLock.EnterScope())
                _renderObjectCache = RuntimeRenderObjectServices.Current?.CreateObjectsForOwner(this) ?? [];
        }

        public IWindow Window => XRWindow.Window;

        public XRWindow XRWindow
        {
            get => _window;
            protected set => _window = value;
        }

        public string RenderApiWrapperOwnerName
        {
            get
            {
                string? title = XRWindow.Window?.Title;
                return string.IsNullOrWhiteSpace(title)
                    ? $"{GetType().Name}@{GetHashCode()}"
                    : title;
            }
        }

        public abstract void Initialize();
        public abstract void CleanUp();
        public virtual bool ShouldSkipNativeWindowDisposeForShutdown => false;
        public virtual bool IsDeviceLost => false;

        protected abstract void WindowRenderCallback(double delta);

        /// <summary>
        /// Processes any pending async uploads within a frame time budget.
        /// Called at the start of each frame to spread upload work across frames.
        /// </summary>
        public virtual void ProcessPendingUploads() { }

        public void RenderWindow(double delta)
            => WindowRenderCallback(delta);

        protected virtual void MainLoop() => Window?.Run();

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

        public void FrameBufferInvalidated()
        {
            _frameBufferInvalidated = true;
        }

        internal virtual void NotifyRenderResourcesChanged()
        {
        }

        protected bool _frameBufferInvalidated = false;
        #endregion

        #region Render Area
        private readonly Stack<BoundingRectangle> _renderAreaStack = new();

        public BoundingRectangle CurrentRenderArea
            => _renderAreaStack.Count > 0
            ? _renderAreaStack.Peek()
            : new BoundingRectangle(0, 0, Window.Size.X, Window.Size.Y);

        public abstract void CropRenderArea(BoundingRectangle region);
        public abstract void SetRenderArea(BoundingRectangle region);
        public abstract void SetCroppingEnabled(bool enabled);
        public virtual bool SetIndexedViewportScissors(
            ReadOnlySpan<BoundingRectangle> viewports,
            ReadOnlySpan<BoundingRectangle> scissors)
            => false;
        public virtual void ClearIndexedViewportScissors(int count) { }

        /// <summary>
        /// Temporarily applies the renderer's UI-safe clip-space policy for raw UI backend draws.
        /// </summary>
        public virtual IDisposable? PushUiClipSpacePolicy()
            => null;
        #endregion

        #region ImGui
        private long _lastImGuiTimestampTicks = long.MinValue;
        private readonly object _imguiRenderLock = new();

        internal static bool ShouldSkipImGuiFrame(bool allowMultipleInFrame, long timestampTicks, long lastTimestampTicks)
            => !allowMultipleInFrame && timestampTicks == lastTimestampTicks;

        protected interface IImGuiRendererBackend
        {
            void MakeCurrent();
            void Update(float deltaSeconds);
            void Render();
            void RenderPlatformWindows();
        }

        protected virtual bool SupportsImGui => false;

        protected virtual bool ShouldRenderImGui(XRViewport? viewport)
            => viewport?.Window is not null || XRWindow.Window is not null;

        protected virtual IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport)
            => null;

        protected void ResetImGuiFrameMarker()
            => _lastImGuiTimestampTicks = long.MinValue;

        private static long BeginImGuiCpuPhase(bool profilingActive)
            => profilingActive ? Stopwatch.GetTimestamp() : 0L;

        private static void RecordImGuiCpuPhase(bool profilingActive, ulong frameId, string name, long startTicks)
        {
            if (!profilingActive || startTicks == 0L)
                return;

            double milliseconds = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            RenderPipelineGpuProfiler.Instance.RecordRenderThreadCpuTiming(frameId, name, milliseconds);
        }

        private static void RecordImGuiFrameOutput(XRCamera? camera, ulong frameId, long elapsedTicks)
        {
            double cpuMs = elapsedTicks <= 0L ? 0.0 : elapsedTicks * 1000.0 / Stopwatch.Frequency;
            IRuntimeRenderingHostServices services = RuntimeRenderingHostServices.Current;
            EVrOutputViewKind viewKind = ResolveImGuiOverlayViewKind(camera, services);
            bool desktopFacing = viewKind is EVrOutputViewKind.DesktopEditor or EVrOutputViewKind.CyclopeanDesktop;
            bool mirror = desktopFacing &&
                services.IsInVR &&
                services.VrMirrorMode is EVrMirrorMode.BlitSubmittedEye or EVrMirrorMode.CyclopeanReconstruct;
            var pacing = FrameOutputPacingDecision.Due(viewKind, EFrameOutputKind.ImGuiOverlay, frameId);
            var telemetry = new FrameOutputTelemetry(
                EFrameOutputKind.ImGuiOverlay,
                viewKind,
                EFrameOutputPhase.Overlay,
                pacing,
                BuildImGuiOverlayOutputName(viewKind),
                string.Empty,
                true,
                true,
                false,
                mirror,
                false,
                viewKind is EVrOutputViewKind.LeftEye or EVrOutputViewKind.RightEye ||
                    (viewKind == EVrOutputViewKind.CyclopeanDesktop && services.VrMirrorMode != EVrMirrorMode.FullIndependentRender),
                0,
                0,
                0,
                0,
                cpuMs,
                0.0);
            services.RecordRenderFrameOutput(telemetry);
        }

        private static EVrOutputViewKind ResolveImGuiOverlayViewKind(XRCamera? camera, IRuntimeRenderingHostServices services)
        {
            if (camera?.StereoEyeLeft == true)
                return EVrOutputViewKind.LeftEye;
            if (camera?.StereoEyeLeft == false)
                return EVrOutputViewKind.RightEye;

            return services.IsInVR && services.VrMirrorMode != EVrMirrorMode.FullIndependentRender
                ? EVrOutputViewKind.CyclopeanDesktop
                : EVrOutputViewKind.DesktopEditor;
        }

        private static string BuildImGuiOverlayOutputName(EVrOutputViewKind viewKind)
            => viewKind switch
            {
                EVrOutputViewKind.LeftEye => "ImGui overlay left eye",
                EVrOutputViewKind.RightEye => "ImGui overlay right eye",
                EVrOutputViewKind.CyclopeanDesktop => "ImGui overlay VR mirror",
                _ => "ImGui overlay",
            };

        protected static void ConfigureImGuiDisplay(IRuntimeScreenSpaceUserInterface? canvas, XRViewport? viewport, XRCamera? camera)
        {
            var io = ImGui.GetIO();

            Vector2 displaySize;
            Vector2 displayPos = Vector2.Zero;
            Vector2 framebufferScale = Vector2.One;

            bool interactiveResize = viewport?.Window?.IsInteractiveResizeInProgress == true;

            // Canvas ActualSize is layout-produced and can lag the Win32 modal resize loop.
            // ImGui needs the live viewport/window size here so it relays out instead of
            // rendering old logical coordinates into the new framebuffer scale.
            if (!interactiveResize &&
                canvas?.TryGetImGuiDisplayMetrics(viewport, camera, out displaySize, out displayPos, out framebufferScale) == true)
            {
            }
            else if (viewport is not null)
            {
                var region = viewport.Region;
                displaySize = new Vector2(region.Width, region.Height);

                var hostWindow = viewport.Window?.Window;
                if (hostWindow is not null)
                {
                    var logicalSize = viewport.Window?.EffectiveWindowSize ?? hostWindow.Size;
                    var framebufferSize = viewport.Window?.EffectiveFramebufferSize ?? hostWindow.FramebufferSize;
                    var scaleSourceFramebufferSize = framebufferSize;
                    if (scaleSourceFramebufferSize.X <= 0 || scaleSourceFramebufferSize.Y <= 0)
                        scaleSourceFramebufferSize = hostWindow.FramebufferSize;

                    float scaleX = logicalSize.X > 0
                        ? (float)scaleSourceFramebufferSize.X / logicalSize.X
                        : 1f;
                    float scaleY = logicalSize.Y > 0
                        ? (float)scaleSourceFramebufferSize.Y / logicalSize.Y
                        : 1f;

                    framebufferScale = new Vector2(
                        MathF.Max(scaleX, float.Epsilon),
                        MathF.Max(scaleY, float.Epsilon));

                    bool coversWholeFramebuffer =
                        region.X == 0 &&
                        region.Y == 0 &&
                        Math.Abs(region.Width - scaleSourceFramebufferSize.X) <= 1 &&
                        Math.Abs(region.Height - scaleSourceFramebufferSize.Y) <= 1;

                    displaySize = coversWholeFramebuffer
                        ? new Vector2(Math.Max(logicalSize.X, 1), Math.Max(logicalSize.Y, 1))
                        : new Vector2(
                            region.Width / framebufferScale.X,
                            region.Height / framebufferScale.Y);
                }
            }
            else if (camera?.Parameters is XROrthographicCameraParameters ortho)
            {
                displaySize = new Vector2(ortho.Width, ortho.Height);
                displayPos = ortho.Origin;
            }
            else
            {
                displaySize = Vector2.One;
            }

            if (displaySize.X <= 0 || displaySize.Y <= 0)
                displaySize = Vector2.One;

            io.DisplaySize = displaySize;
            //io.DisplayPos = displayPos;
            io.DisplayFramebufferScale = framebufferScale;
        }

        public bool TryRenderImGui(XRViewport? viewport, IRuntimeScreenSpaceUserInterface? canvas, XRCamera? camera, Action draw)
            => TryRenderImGui(viewport, canvas, camera, draw, allowMultipleInFrame: false);

        public bool TryRenderImGui(
            XRViewport? viewport,
            IRuntimeScreenSpaceUserInterface? canvas,
            XRCamera? camera,
            Action draw,
            bool allowMultipleInFrame)
        {
            if (!SupportsImGui)
                return false;

            if (RuntimeRenderingHostServices.Current.IsShadowPass)
                return false;

            if (!ShouldRenderImGui(viewport))
                return false;

            var backend = GetImGuiBackend(viewport);
            if (backend is null)
                return false;

            long timestampTicks = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
            bool allowResizeFrame =
                viewport?.Window?.IsInteractiveResizeInProgress == true ||
                XRWindow.IsInteractiveResizeInProgress;

            if (ShouldSkipImGuiFrame(allowMultipleInFrame || allowResizeFrame, timestampTicks, _lastImGuiTimestampTicks))
                return false;

            _lastImGuiTimestampTicks = timestampTicks;
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            long overlayStartTicks = Stopwatch.GetTimestamp();

            lock (ImGuiContextTracker.SyncRoot)
            {
                lock (_imguiRenderLock)
                {
                    var previousContext = ImGui.GetCurrentContext();
                    backend.MakeCurrent();
                    bool frameStarted = false;

                    try
                    {
                        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
                        bool profilingActive = profiler.ShouldInstrumentCommandScopes;

                        long phaseStart = BeginImGuiCpuPhase(profilingActive);
                        ConfigureImGuiDisplay(canvas, viewport, camera);
                        RecordImGuiCpuPhase(profilingActive, frameId, "ImGui.ConfigureDisplay", phaseStart);

                        phaseStart = BeginImGuiCpuPhase(profilingActive);
                        backend.Update((float)RuntimeRenderingHostServices.Current.RenderDeltaSeconds);
                        RecordImGuiCpuPhase(profilingActive, frameId, "ImGui.Backend.Update", phaseStart);
                        frameStarted = true;

                        phaseStart = BeginImGuiCpuPhase(profilingActive);
                        draw();
                        RecordImGuiCpuPhase(profilingActive, frameId, "ImGui.DrawCallback", phaseStart);

                        phaseStart = BeginImGuiCpuPhase(profilingActive);
                        using (profilingActive ? profiler.StartScope("ImGui.Backend.Render") : default)
                            backend.Render();
                        RecordImGuiCpuPhase(profilingActive, frameId, "ImGui.Backend.Render", phaseStart);

                        phaseStart = BeginImGuiCpuPhase(profilingActive);
                        using (profilingActive ? profiler.StartScope("ImGui.PlatformWindows") : default)
                            backend.RenderPlatformWindows();
                        RecordImGuiCpuPhase(profilingActive, frameId, "ImGui.PlatformWindows", phaseStart);

                        frameStarted = false;
                    }
                    catch
                    {
                        if (frameStarted)
                        {
                            try
                            {
                                ImGui.EndFrame();
                            }
                            catch
                            {
                            }
                        }

                        throw;
                    }
                    finally
                    {
                        if (previousContext == IntPtr.Zero)
                        {
                            ImGui.SetCurrentContext(IntPtr.Zero);
                        }
                        else if (ImGuiContextTracker.IsAlive(previousContext))
                        {
                            ImGui.SetCurrentContext(previousContext);
                        }
                    }
                }
            }

            RecordImGuiFrameOutput(camera, frameId, Stopwatch.GetTimestamp() - overlayStartTicks);
            return true;
        }

        protected Dictionary<string, bool> _verifiedExtensions = [];
        protected void LogExtension(string name, bool exists)
            => _verifiedExtensions.Add(name, exists);
        protected bool ExtensionChecked(string name)
        {
            _verifiedExtensions.TryGetValue(name, out bool exists);
            return exists;
        }
        #endregion

        #region Render Object Cache
        private readonly Lock _roCacheLock = new();
        private readonly ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> _renderObjectCache = [];
        public IReadOnlyDictionary<GenericRenderObject, AbstractRenderAPIObject> RenderObjectCache => _renderObjectCache;

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
            using (_roCacheLock.EnterScope())
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
            using (_roCacheLock.EnterScope())
                return _renderObjectCache.TryGetValue(renderObject, out apiObject);
        }

        /// <summary>
        /// Drops a single render-object entry from this renderer's API cache. Used by
        /// <see cref="GenericRenderObject.OnDestroying"/> so destroyed programs/textures/meshes do not
        /// linger in the cache after their GL/Vulkan handles are released.
        /// </summary>
        public void RemoveAPIRenderObject(GenericRenderObject renderObject)
        {
            if (renderObject is null)
                return;

            using (_roCacheLock.EnterScope())
                _renderObjectCache.TryRemove(renderObject, out _);
        }

        public void DestroyCachedAPIRenderObjects()
        {
            KeyValuePair<GenericRenderObject, AbstractRenderAPIObject>[] cachedObjects;
            using (_roCacheLock.EnterScope())
            {
                if (_renderObjectCache.Count == 0)
                    return;

                cachedObjects = [.. _renderObjectCache];
                _renderObjectCache.Clear();
            }

            foreach (var pair in cachedObjects)
            {
                try
                {
                    pair.Key.RemoveWrapper(pair.Value);
                }
                catch
                {
                }

                try
                {
                    pair.Value.Destroy();
                }
                catch
                {
                }
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
        #endregion

        #region Utilities
        public static byte* ToAnsi(string str)
            => (byte*)Marshal.StringToHGlobalAnsi(str);
        public static string? FromAnsi(byte* ptr)
            => Marshal.PtrToStringAnsi((nint)ptr);
        #endregion

        #region Luminance / Exposure

        public bool CalcDotLuminance(XRTexture2D texture, out float dotLuminance, bool genMipmapsNow)
            => CalcDotLuminance(texture, RuntimeRenderingHostServices.Current.DefaultLuminance, out dotLuminance, genMipmapsNow);
        public abstract bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow);
        public float CalculateDotLuminance(XRTexture2D texture, bool generateMipmapsNow)
            => CalcDotLuminance(texture, out float dotLum, generateMipmapsNow) ? dotLum : 1.0f;

        public bool CalcDotLuminance(XRTexture2DArray texture, out float dotLuminance, bool genMipmapsNow)
            => CalcDotLuminance(texture, RuntimeRenderingHostServices.Current.DefaultLuminance, out dotLuminance, genMipmapsNow);
        public abstract bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow);
        public float CalculateDotLuminance(XRTexture2DArray texture, bool generateMipmapsNow)
            => CalcDotLuminance(texture, out float dotLum, generateMipmapsNow) ? dotLum : 1.0f;

        public abstract void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true);
        public abstract void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true);

        /// <summary>
        /// Reads the red channel of a 2D texture's center texel at mip 0.
        /// Returns 0 if the texture is not generated.
        /// Unlike CalcDotLuminance, this does NOT require mipmaps.
        /// </summary>
        public virtual float ReadTextureCenterRedMip0(XRTexture2D texture) => 0.0f;

        /// <summary>
        /// True if this renderer supports updating and sampling auto exposure entirely on the GPU.
        /// </summary>
        public virtual bool SupportsGpuAutoExposure => false;

        /// <summary>
        /// Updates a 1x1 exposure texture based on the supplied HDR source texture.
        /// Implementations should avoid CPU readback and keep the exposure value on-GPU.
        /// Returns true only when a valid GPU exposure value was produced for the current frame.
        /// </summary>
        public virtual bool UpdateAutoExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, ColorGradingSettings settings, float deltaTime, bool generateMipmapsNow)
            => throw new NotSupportedException();

        public void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency, Action<bool, float> callback)
            => CalcDotLuminanceFrontAsync(region, withTransparency, RuntimeRenderingHostServices.Current.DefaultLuminance, callback);
        public abstract void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback);

        public void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Action<bool, float> callback)
            => CalcDotLuminanceFrontAsyncCompute(region, withTransparency, RuntimeRenderingHostServices.Current.DefaultLuminance, callback);
        public abstract void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback);
        #endregion

        #region Core Rendering / IO

        public abstract void Clear(bool color, bool depth, bool stencil);
        public abstract void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo);
        public abstract void ClearColor(ColorF4 color);
        public abstract void SetReadBuffer(EReadBufferMode mode);
        public abstract void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode);
        /// <summary>
        /// True while the renderer has redirected its active present target to a non-window swapchain image.
        /// </summary>
        public virtual bool IsRenderingExternalSwapchainTarget => false;

        /// <summary>
        /// True when descriptor and buffer resolution may perform blocking GPU uploads on the render thread.
        /// External runtime swapchain submission can disable this while an acquired image is active.
        /// </summary>
        public virtual bool AllowSynchronousResourceUploads => !IsRenderingExternalSwapchainTarget;

        public virtual bool TryGetExternalSwapchainTargetRegion(out BoundingRectangle region)
        {
            region = default;
            return false;
        }
        public virtual void TrackWindowPresentSource(XRTexture? colorTexture, XRFrameBuffer? sourceFrameBuffer) { }
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

        public abstract void EnableStencilTest(bool enable);
        public abstract void StencilFunc(EComparison function, int reference, uint mask);
        public abstract void StencilOp(EStencilOp sfail, EStencilOp dpfail, EStencilOp dppass);

        public abstract void EnableBlend(bool enable);
        public abstract void BlendFunc(EBlendingFactor src, EBlendingFactor dst);
        public abstract void BlendFuncSeparate(EBlendingFactor srcRGB, EBlendingFactor dstRGB, EBlendingFactor srcAlpha, EBlendingFactor dstAlpha);
        public abstract void BlendEquation(EBlendEquationMode mode);
        public abstract void BlendEquationSeparate(EBlendEquationMode modeRGB, EBlendEquationMode modeAlpha);

        /// <summary>
        /// Enables per-sample shading (GL_SAMPLE_SHADING) with the given minimum fraction.
        /// When enabled with minValue=1.0, the fragment shader runs once per sample.
        /// </summary>
        public abstract void EnableSampleShading(float minValue);
        /// <summary>
        /// Disables per-sample shading (GL_SAMPLE_SHADING).
        /// </summary>
        public abstract void DisableSampleShading();

        public abstract void DispatchCompute(XRRenderProgram program, int numGroupsX, int numGroupsY, int numGroupsZ);

        /// <summary>
        /// True when images returned by <see cref="GetScreenshotAsync"/> need a CPU-side
        /// vertical flip before being written in normal top-left image-file order.
        /// This is independent from framebuffer texture UV sampling direction.
        /// </summary>
        public virtual bool ScreenshotRequiresVerticalFlip => true;

        public abstract void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback);

        public abstract bool TryReadTextureMipRgbaFloat(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            out float[]? rgbaFloats,
            out int width,
            out int height,
            out string failure);

        public abstract bool TryReadTexturePixelRgbaFloat(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            out Vector4 rgba,
            out string failure);

        public virtual void TryBuildTexture2DMipChainRgba8Async(
            XRTexture2D texture,
            Action<bool, Mipmap2D[]?, string> callback)
            => callback(false, null, "GPU RGBA8 texture mip-chain cooking is not supported by this renderer.");
        #endregion

        #region Blitting
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
        /// Blits a single color attachment between FBOs, selecting both read and draw buffers.
        /// Use this for MSAA resolve of individual GBuffer attachments.
        /// </summary>
        public void BlitFBOToFBOSingleAttachment(
            XRFrameBuffer inFBO,
            XRFrameBuffer outFBO,
            EReadBufferMode readBufferMode,
            EReadBufferMode drawBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            BlitWithDrawBuffer(
                inFBO,
                outFBO,
                inFBO.Width,
                inFBO.Height,
                outFBO.Width,
                outFBO.Height,
                readBufferMode,
                drawBufferMode,
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

        /// <summary>
        /// Blits between FBOs with explicit read and draw buffer selection.
        /// Required for per-attachment MSAA resolve of multi-target FBOs.
        /// </summary>
        public abstract void BlitWithDrawBuffer(
            XRFrameBuffer? inFBO,
            XRFrameBuffer? outFBO,
            uint inW, uint inH,
            uint outW, uint outH,
            EReadBufferMode readBufferMode,
            EReadBufferMode drawBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter);
        #endregion

        #region Synchronization / Masks

        public abstract void MemoryBarrier(EMemoryBarrierMask mask);
        public virtual XRGpuFence? InsertGpuFence()
            => null;
        public abstract void ColorMask(bool red, bool green, bool blue, bool alpha);

        #endregion

        #region Indirect + Pipeline Abstraction (initial surface)

        /// <summary>
        /// Binds the VAO (or equivalent) for the given mesh renderer version for subsequent draws.
        /// Pass null to unbind.
        /// </summary>
        public abstract void BindVAOForRenderer(XRMeshRenderer.BaseVersion? version);

        /// <summary>
        /// Returns true if an index (element) buffer is bound for the currently bound VAO.
        /// Implementations should check triangle/line/point element buffers according to active primitive.
        /// </summary>
        public abstract bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version);

        /// <summary>
        /// Returns true if an index (element) buffer is bound, and outputs the index element type.
        /// </summary>
        /// <param name="version">The mesh renderer version to check, or null to check the currently bound VAO.</param>
        /// <param name="indexElementSize">The size of each index element (u8, u16, or u32).</param>
        /// <param name="indexCount">The number of indices in the element buffer.</param>
        /// <returns>True if an index buffer is bound and valid.</returns>
        public abstract bool TryGetIndexBufferInfo(XRMeshRenderer.BaseVersion? version, out IndexSize indexElementSize, out uint indexCount);

        /// <summary>
        /// Syncs the mesh renderer's triangle index buffer with the provided data buffer.
        /// Used for indirect rendering to share the atlas index buffer across all indirect draws.
        /// </summary>
        /// <param name="meshRenderer">The mesh renderer whose index buffer should be updated.</param>
        /// <param name="indexBuffer">The index buffer containing triangle indices.</param>
        /// <param name="elementSize">The size of each index element (u16 or u32).</param>
        /// <returns>True if the sync was successful.</returns>
        public abstract bool TrySyncMeshRendererIndexBuffer(XRMeshRenderer meshRenderer, XRDataBuffer indexBuffer, IndexSize elementSize);

        /// <summary>
        /// Ensures vertex attributes and buffer bindings for the active program are configured on the VAO for the given mesh renderer version.
        /// Should be called when switching programs to avoid missing attribute locations.
        /// </summary>
        public abstract void ConfigureVAOAttributesForProgram(XRRenderProgram program, XRMeshRenderer.BaseVersion? version);

        /// <summary>
        /// Bind the draw-indirect buffer target to the provided buffer.
        /// </summary>
        public abstract void BindDrawIndirectBuffer(XRDataBuffer buffer);
        public abstract void UnbindDrawIndirectBuffer();

        /// <summary>
        /// Bind/unbind the parameter buffer (draw count source) if supported.
        /// </summary>
        public abstract void BindParameterBuffer(XRDataBuffer buffer);
        public abstract void UnbindParameterBuffer();

        /// <summary>
        /// Polls pending asynchronous GPU statistics readbacks without waiting for the GPU.
        /// </summary>
        public virtual void PollGpuRenderStatsReadbacks() { }

        /// <summary>
        /// Queues a nonblocking GPU->CPU readback of one or more uint draw-count values.
        /// Implementations must return immediately and publish the result only after a GPU fence is already signaled.
        /// </summary>
        public virtual bool QueueGpuRenderDrawCountReadback(XRDataBuffer drawCountBuffer, uint countByteOffset = 0, uint countElementCount = 1)
            => false;

        /// <summary>
        /// Queues a nonblocking readback of the shared GPU render stats buffer.
        /// </summary>
        public virtual bool QueueGpuRenderStatsBufferReadback(XRDataBuffer statsBuffer, bool publishDraws, bool publishTriangles)
            => false;

        /// <summary>
        /// Issue indirect multi-draws.
        /// </summary>
        public abstract void MultiDrawElementsIndirect(uint drawCount, uint stride);
        public abstract void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset);
        public abstract void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset = 0, nuint countByteOffset = 0);

        /// <summary>
        /// Apply the given render parameters (depth/blend/cull/stencil, etc.).
        /// </summary>
        public abstract void ApplyRenderParameters(RenderingParameters parameters);

        /// <summary>
        /// Set standard engine uniforms (e.g. camera) for the provided program.
        /// </summary>
        public abstract void SetEngineUniforms(XRRenderProgram program, XRCamera camera);

        /// <summary>
        /// Set material parameters/textures for the provided program.
        /// </summary>
        public abstract void SetMaterialUniforms(XRMaterial material, XRRenderProgram program);

        /// <summary>
        /// Returns whether the current API supports the Count variant for MultiDrawElementsIndirect.
        /// </summary>
        public abstract bool SupportsIndirectCountDraw();

        /// <summary>
        /// Returns whether the current API supports task/mesh shader dispatch for meshlet rendering.
        /// </summary>
        public virtual EMeshShaderDialect MeshShaderDialect
            => EMeshShaderDialect.None;

        public virtual bool SupportsDirectMeshTaskDispatch()
            => false;

        public virtual bool SupportsIndirectCountMeshTaskDispatch()
            => false;

        public virtual bool SupportsProductionMeshletShaders()
            => false;

        public virtual bool TryDrawMeshTasksIndirectCount(
            XRDataBuffer indirectBuffer,
            XRDataBuffer countBuffer,
            uint maxDrawCount,
            uint stride,
            out string failureReason,
            nuint byteOffset = 0,
            nuint countByteOffset = 0)
        {
            if (!ValidateMeshTasksIndirectCountArgs(
                indirectBuffer,
                countBuffer,
                maxDrawCount,
                stride,
                byteOffset,
                countByteOffset,
                out failureReason))
            {
                return false;
            }

            failureReason = MeshletDispatchUnsupportedReason;
            return false;
        }

        public static bool ValidateMeshTasksIndirectCountArgs(
            XRDataBuffer? indirectBuffer,
            XRDataBuffer? countBuffer,
            uint maxDrawCount,
            uint stride,
            nuint byteOffset,
            nuint countByteOffset,
            out string failureReason)
        {
            if (indirectBuffer is null)
            {
                failureReason = "Mesh-task indirect dispatch requires a draw-indirect command buffer.";
                return false;
            }

            if (countBuffer is null)
            {
                failureReason = "Mesh-task indirect dispatch requires a GPU-written indirect-command count buffer.";
                return false;
            }

            if (indirectBuffer.Target != EBufferTarget.DrawIndirectBuffer)
            {
                failureReason = $"Mesh-task indirect commands must use {nameof(EBufferTarget.DrawIndirectBuffer)} so OpenGL and Vulkan backends can consume the same buffer.";
                return false;
            }

            if (countBuffer.Target is not EBufferTarget.ShaderStorageBuffer and not EBufferTarget.ParameterBuffer)
            {
                failureReason = $"Mesh-task indirect command counts must use {nameof(EBufferTarget.ShaderStorageBuffer)} or {nameof(EBufferTarget.ParameterBuffer)}.";
                return false;
            }

            if (maxDrawCount == 0u)
            {
                failureReason = "Mesh-task indirect dispatch requires maxDrawCount > 0.";
                return false;
            }

            if (stride < GPUMeshletLayout.MeshTaskIndirectCommandStride || (stride & 3u) != 0u)
            {
                failureReason = $"Mesh-task indirect stride must be at least {GPUMeshletLayout.MeshTaskIndirectCommandStride} bytes and 4-byte aligned.";
                return false;
            }

            if (((ulong)byteOffset & 3ul) != 0ul || ((ulong)countByteOffset & 3ul) != 0ul)
            {
                failureReason = "Mesh-task indirect byte offsets must be 4-byte aligned.";
                return false;
            }

            ulong indirectBytesRequired = (ulong)byteOffset + ((ulong)stride * maxDrawCount);
            if (indirectBytesRequired > indirectBuffer.Length)
            {
                failureReason = $"Mesh-task indirect buffer is too small for maxDrawCount={maxDrawCount}, stride={stride}, offset={byteOffset}.";
                return false;
            }

            ulong countBytesRequired = (ulong)countByteOffset + sizeof(uint);
            if (countBytesRequired > countBuffer.Length)
            {
                failureReason = $"Mesh-task dispatch count buffer is too small for offset={countByteOffset}.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }

        public virtual string MeshletDispatchUnsupportedReason
            => MeshShaderDialect == EMeshShaderDialect.None
                ? "No mesh shader dialect is available on the active renderer."
                : "The active renderer does not expose production indirect-count mesh task dispatch.";

        public virtual bool SupportsMeshletDispatch()
            => MeshShaderDialect != EMeshShaderDialect.None &&
               SupportsIndirectCountMeshTaskDispatch() &&
               SupportsProductionMeshletShaders();

        /// <summary>
        /// Blocks the CPU until all GPU commands have completed.
        /// </summary>
        public abstract void WaitForGpu();

        #endregion
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

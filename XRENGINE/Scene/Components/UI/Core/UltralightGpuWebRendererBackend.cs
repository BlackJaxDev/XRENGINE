using System;
using System.Collections.Concurrent;
using System.IO;
using UltralightNet;
using UltralightNet.AppCore;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.UI.Ultralight;
using Silk.NET.OpenGL;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Ultralight GPU backend that renders directly into a GL framebuffer (no CPU readback).
    /// Uses <see cref="OpenGLGPUDriver"/> for GPU-accelerated rendering.
    /// </summary>
    public sealed class UltralightGpuWebRendererBackend : IWebRendererBackend
    {
        private static bool _fontLoaderInitialized;
        private static OpenGLGPUDriver? _gpuDriver;

        private UltralightNet.Renderer? _renderer;
        private View? _view;
        private ULConfig? _config;
        private ULViewConfig? _viewConfig;

        private uint _targetFramebufferId;
        private uint _readFramebufferId;
        private bool _loggedMissingTarget;
        private bool _loggedEmptyRenderTarget;
        private bool _loggedInvalidTextureMapping;
        private bool _loggedFirstBlit;
        private bool _loggedMsaaResolve;
        private bool _loggedCompatibilityProbe;
        private int _consoleMessageCount;

        // Cached GL reference
        private GL? _cachedGL;

        // Thread-safe input queue: events are enqueued from the game/input thread
        // and dispatched on the render thread inside Update().
        private readonly ConcurrentQueue<Action<View>> _pendingInputEvents = new();
        private bool _loggedFirstDispatch;

        public bool IsInitialized => _renderer is not null && _view is not null;
        public bool SupportsFramebuffer => true;

        public void Initialize(uint width, uint height, bool transparentBackground)
        {
            Dispose();

            EnsureFontLoader();
            CacheOpenGL();

            if (_cachedGL is null)
            {
                Debug.LogWarning("UltralightGpuWebRendererBackend: No OpenGL context available.");
                return;
            }

            EnsureGpuDriver();

            string? resourcePathPrefix = ResolveUltralightResourcePathPrefix();

            _config = new ULConfig
            {
                ForceRepaint = true,
            };

            // Only set ResourcePathPrefix if we actually found resources on disk
            if (resourcePathPrefix is not null)
            {
                _config = new ULConfig
                {
                    ForceRepaint = true,
                    ResourcePathPrefix = resourcePathPrefix,
                };
            }

            if (resourcePathPrefix is null)
            {
                Debug.LogWarning(
                    "Ultralight resources not found (icudt67l.dat). " +
                    "Proceeding with defaults; some content may not render correctly. " +
                    "Run Tools/Dependencies/Get-UltralightResources.ps1 to download resources.");
            }

            _viewConfig = new ULViewConfig
            {
                IsAccelerated = true,
                IsTransparent = transparentBackground,
                EnableJavaScript = true,
                InitialFocus = true,
            };

            _renderer = ULPlatform.CreateRenderer(_config.Value);
            _view = _renderer.CreateView(width, height, _viewConfig, null);
            AttachDiagnostics();

            EnsureReadFramebuffer();
        }

        public void Resize(uint width, uint height)
        {
            _view?.Resize(in width, in height);
        }

        public void LoadUrl(string url)
        {
            if (_view is null)
                return;

            _view.URL = url;
        }

        public void Update(double deltaSeconds)
        {
            // Drain queued input events on the render thread before updating.
            // Ultralight is not thread-safe — all View calls must happen on the
            // same thread that calls Renderer.Update() / Renderer.Render().
            if (_view is not null)
            {
                int dispatched = 0;
                while (_pendingInputEvents.TryDequeue(out var action))
                {
                    action(_view);
                    dispatched++;
                }

                if (dispatched > 0 && !_loggedFirstDispatch)
                {
                    _loggedFirstDispatch = true;
                    Debug.Out($"[UltralightGpuWeb] First input dispatch: {dispatched} event(s) on render thread.");
                }
            }

            _renderer?.Update();
        }

        public void Render()
        {
            if (_renderer is null || _view is null)
                return;

            if (_targetFramebufferId == 0)
            {
                if (!_loggedMissingTarget)
                {
                    Debug.LogWarning("[UltralightGpuWeb] Render skipped: target framebuffer is 0.");
                    _loggedMissingTarget = true;
                }
                return;
            }

            _renderer.Render();

            RenderTarget rt = _view.RenderTarget;
            if (rt.IsEmpty || rt.TextureId == 0)
            {
                if (!_loggedEmptyRenderTarget)
                {
                    Debug.LogWarning($"[UltralightGpuWeb] Render target empty: isEmpty={rt.IsEmpty}, textureId={rt.TextureId}.");
                    _loggedEmptyRenderTarget = true;
                }
                return;
            }

            // Look up the real GL texture handle from the driver's texture list
            if (_gpuDriver is null)
                return;

            var textures = _gpuDriver.Textures;
            int texIdx = (int)rt.TextureId;
            if (texIdx < 0 || texIdx >= textures.Count || textures[texIdx] is null)
            {
                if (!_loggedInvalidTextureMapping)
                {
                    Debug.LogWarning($"[UltralightGpuWeb] Texture mapping failed: rt.TextureId={rt.TextureId}, textures.Count={textures.Count}.");
                    _loggedInvalidTextureMapping = true;
                }
                return;
            }

            var texEntry = textures[texIdx];
            uint glTextureId = texEntry.TextureId;
            if (glTextureId == 0)
                return;

            GL? gl = _cachedGL;
            if (gl is null)
                return;

            EnsureReadFramebuffer();

            int srcWidth = (int)rt.TextureWidth;
            int srcHeight = (int)rt.TextureHeight;

            // Resolve MSAA if the final render target was rendered
            // into a multisampled framebuffer (the resolve within
            // UpdateCommandList only runs when a texture is sampled
            // by a subsequent draw — the final RT is never sampled).
            if (texEntry.MultisampledFramebuffer != 0)
            {
                gl.BindFramebuffer(GLEnum.ReadFramebuffer, texEntry.MultisampledFramebuffer);
                gl.BindFramebuffer(GLEnum.DrawFramebuffer, texEntry.Framebuffer);
                gl.BlitFramebuffer(
                    0, 0, srcWidth, srcHeight,
                    0, 0, srcWidth, srcHeight,
                    ClearBufferMask.ColorBufferBit,
                    BlitFramebufferFilter.Linear);

                if (!_loggedMsaaResolve)
                {
                    Debug.Out($"[UltralightGpuWeb] MSAA resolved: msaaFbo={texEntry.MultisampledFramebuffer} → fbo={texEntry.Framebuffer}, size={srcWidth}x{srcHeight}.");
                    _loggedMsaaResolve = true;
                }
            }

            // Now blit the resolved (non-MSAA) texture to our target FBO
            gl.BindFramebuffer(GLEnum.ReadFramebuffer, _readFramebufferId);
            gl.FramebufferTexture2D(GLEnum.ReadFramebuffer, GLEnum.ColorAttachment0,
                GLEnum.Texture2D, glTextureId, 0);

            gl.BindFramebuffer(GLEnum.DrawFramebuffer, _targetFramebufferId);

            gl.BlitFramebuffer(
                0, 0, srcWidth, srcHeight,
                0, 0, srcWidth, srcHeight,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear);

            if (!_loggedFirstBlit)
            {
                Debug.Out($"[UltralightGpuWeb] First blit succeeded: srcTex={glTextureId}, msaa={texEntry.MultisampledFramebuffer != 0}, src={srcWidth}x{srcHeight}, dstFbo={_targetFramebufferId}.");
                _loggedFirstBlit = true;
            }

            gl.BindFramebuffer(GLEnum.ReadFramebuffer, 0);
            gl.BindFramebuffer(GLEnum.DrawFramebuffer, 0);
        }

        public bool TryGetFrame(out WebFrame frame)
        {
            frame = default;
            return false;
        }

        public void SendMouseMove(int x, int y)
        {
            if (_view is null)
                return;

            _pendingInputEvents.Enqueue(v => v.FireMouseEvent(new ULMouseEvent
            {
                Type = ULMouseEventType.MouseMoved,
                Button = ULMouseEventButton.None,
                X = x,
                Y = y,
            }));
        }

        public void SendMouseButton(bool pressed, WebMouseButton button, int x, int y)
        {
            if (_view is null)
                return;

            var mapped = MapMouseButton(button);
            _pendingInputEvents.Enqueue(v =>
            {
                if (pressed)
                    v.Focus();

                v.FireMouseEvent(new ULMouseEvent
                {
                    Type = pressed ? ULMouseEventType.MouseDown : ULMouseEventType.MouseUp,
                    Button = mapped,
                    X = x,
                    Y = y,
                });
            });
        }

        public void SendScroll(int deltaX, int deltaY)
        {
            if (_view is null)
                return;

            _pendingInputEvents.Enqueue(v => v.FireScrollEvent(new ULScrollEvent
            {
                Type = ULScrollEventType.ByPixel,
                DeltaX = deltaX,
                DeltaY = deltaY,
            }));
        }

        public void SetTargetFramebuffer(uint framebufferId)
        {
            _targetFramebufferId = framebufferId;
        }

        public void Dispose()
        {
            if (_view is not null)
            {
                _view.OnFinishLoading -= HandleFinishLoading;
                _view.OnAddConsoleMessage -= HandleConsoleMessage;
            }

            _view?.Dispose();
            _view = null;

            _renderer?.Dispose();
            _renderer = null;

            _config = null;
            _viewConfig = null;
            _targetFramebufferId = 0;

            if (_readFramebufferId != 0)
            {
                _cachedGL?.DeleteFramebuffer(_readFramebufferId);
                _readFramebufferId = 0;
            }
        }

        private void AttachDiagnostics()
        {
            if (_view is null)
                return;

            _view.OnFinishLoading += HandleFinishLoading;
            _view.OnAddConsoleMessage += HandleConsoleMessage;
        }

        private void HandleFinishLoading(ulong frameId, bool isMainFrame, string url)
        {
            if (!isMainFrame || _view is null || _loggedCompatibilityProbe)
                return;

            const string probeScript = "JSON.stringify({ua:navigator.userAgent,dpr:window.devicePixelRatio,innerWidth:window.innerWidth,innerHeight:window.innerHeight,href:location.href})";
            string result = _view.EvaluateScript(probeScript, out string exception);

            if (!string.IsNullOrWhiteSpace(exception))
                Debug.LogWarning($"[UltralightProbe] EvaluateScript exception: {exception}");
            else
                Debug.Out($"[UltralightProbe] {result}");

            _loggedCompatibilityProbe = true;
        }

        private void HandleConsoleMessage(ULMessageSource source, ULMessageLevel level, string message, uint lineNumber, uint columnNumber, string sourceId)
        {
            if (_consoleMessageCount >= 10)
                return;

            _consoleMessageCount++;
            Debug.Out($"[UltralightConsole] {level} {source} {sourceId}:{lineNumber}:{columnNumber} {message}");
        }

        private static ULMouseEventButton MapMouseButton(WebMouseButton button)
            => button switch
            {
                WebMouseButton.Left => ULMouseEventButton.Left,
                WebMouseButton.Right => ULMouseEventButton.Right,
                WebMouseButton.Middle => ULMouseEventButton.Middle,
                _ => ULMouseEventButton.None,
            };

        private static void EnsureFontLoader()
        {
            if (_fontLoaderInitialized)
                return;

            AppCoreMethods.SetPlatformFontLoader();
            AppCoreMethods.ulEnablePlatformFileSystem(".");
            _fontLoaderInitialized = true;
        }

        private void EnsureGpuDriver()
        {
            if (_gpuDriver is not null)
                return;

            if (_cachedGL is null)
                throw new InvalidOperationException("Cannot initialize GPU driver without an OpenGL context.");

            _gpuDriver = new OpenGLGPUDriver(_cachedGL);
            ULPlatform.GPUDriver = _gpuDriver;
        }

        private static string? ResolveUltralightResourcePathPrefix()
        {
            static string? EnsureDatFile(string resourceDir)
            {
                string dat = Path.Combine(resourceDir, "icudt67l.dat");
                return File.Exists(dat) ? resourceDir : null;
            }

            foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                string? cursor = start;
                for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(cursor); depth++)
                {
                    string[] candidates =
                    [
                        Path.Combine(cursor, "resources"),
                        Path.Combine(cursor, "Build", "Dependencies", "Ultralight", "resources"),
                        Path.Combine(cursor, "ThirdParty", "Ultralight", "resources"),
                    ];

                    for (int i = 0; i < candidates.Length; i++)
                    {
                        string? found = EnsureDatFile(candidates[i]);
                        if (found is null)
                            continue;

                        string normalized = found.Replace('\\', '/');
                        return normalized.EndsWith('/') ? normalized : normalized + '/';
                    }

                    cursor = Directory.GetParent(cursor)?.FullName;
                }
            }

            return null;
        }

        private void CacheOpenGL()
        {
            _cachedGL = null;
            if (AbstractRenderer.Current is OpenGLRenderer current)
            {
                _cachedGL = current.RawGL;
                return;
            }

            var windows = Engine.Windows;
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i].Renderer is OpenGLRenderer renderer)
                {
                    _cachedGL = renderer.RawGL;
                    return;
                }
            }
        }

        private void EnsureReadFramebuffer()
        {
            if (_readFramebufferId != 0)
                return;

            if (_cachedGL is null)
                CacheOpenGL();

            _readFramebufferId = _cachedGL?.GenFramebuffer() ?? 0;
        }
    }
}
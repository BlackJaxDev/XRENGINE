using System;
using System.IO;
using UltralightNet;
using UltralightNet.AppCore;
using XREngine.Data.Rendering;
using XREngine;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Ultralight-based software backend that renders into a CPU pixel buffer.
    /// Returns a pointer to native memory - no managed allocations per frame.
    /// </summary>
    public sealed class UltralightWebRendererBackend : IWebRendererBackend
    {
        private static bool _fontLoaderInitialized;

        private UltralightNet.Renderer? _renderer;
        private View? _view;
        private ULConfig? _config;
        private ULViewConfig? _viewConfig;

        // Cached frame data to avoid allocation
        private WebFrame _lastFrame;
        private bool _hasFrame;
        private bool _pixelsLocked;

        public bool IsInitialized => _renderer is not null && _view is not null;
        public bool SupportsFramebuffer => false;

        public void Initialize(uint width, uint height, bool transparentBackground)
        {
            Dispose();

            EnsureFontLoader();
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
                IsAccelerated = false,
                IsTransparent = transparentBackground,
                EnableJavaScript = true,
                InitialFocus = true,
            };

            _renderer = ULPlatform.CreateRenderer(_config.Value);
            _view = _renderer.CreateView(width, height, _viewConfig, null);
        }

        public void Resize(uint width, uint height)
        {
            if (_view is null)
                return;

            _view.Resize(in width, in height);
            _hasFrame = false;
        }

        public void LoadUrl(string url)
        {
            if (_view is null)
                return;

            _view.URL = url;
        }

        public void Update(double deltaSeconds)
        {
            _renderer?.Update();
        }

        public unsafe void Render()
        {
            if (_renderer is null || _view is null)
                return;

            // Unlock pixels from previous frame if still locked
            UnlockPixelsIfNeeded();

            _renderer.Render();

            ULSurface? surface = _view.Surface;
            if (surface is null)
                return;

            ULBitmap bitmap = surface.Value.Bitmap;
            int width = (int)bitmap.Width;
            int height = (int)bitmap.Height;
            int stride = (int)bitmap.RowBytes;
            int byteCount = stride * height;

            if (width <= 0 || height <= 0 || byteCount <= 0)
            {
                _hasFrame = false;
                return;
            }

            // Lock pixels and return pointer directly - no managed copy
            byte* pixels = bitmap.LockPixels();
            _pixelsLocked = true;

            _lastFrame = new WebFrame(
                width,
                height,
                stride,
                EPixelFormat.Bgra,
                EPixelType.UnsignedByte,
                (IntPtr)pixels,
                byteCount);
            _hasFrame = true;
        }

        private void UnlockPixelsIfNeeded()
        {
            if (!_pixelsLocked)
                return;

            ULSurface? surface = _view?.Surface;
            if (surface is null)
                return;

            surface.Value.Bitmap.UnlockPixels();
            _pixelsLocked = false;
        }

        public bool TryGetFrame(out WebFrame frame)
        {
            frame = _lastFrame;
            return _hasFrame;
        }

        public void SetTargetFramebuffer(uint framebufferId)
        {
            // Not supported in software mode.
        }

        public void Dispose()
        {
            UnlockPixelsIfNeeded();
            
            _view?.Dispose();
            _view = null;

            _renderer?.Dispose();
            _renderer = null;

            _hasFrame = false;
            _config = null;
            _viewConfig = null;
        }

        private static void EnsureFontLoader()
        {
            if (_fontLoaderInitialized)
                return;

            AppCoreMethods.SetPlatformFontLoader();
            AppCoreMethods.ulEnablePlatformFileSystem(".");
            _fontLoaderInitialized = true;
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
    }
}
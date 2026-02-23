using System;
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

        // Cached GL reference
        private GL? _cachedGL;

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
            _renderer?.Update();
        }

        public void Render()
        {
            if (_renderer is null || _view is null || _targetFramebufferId == 0)
                return;

            _renderer.Render();

            RenderTarget rt = _view.RenderTarget;
            if (rt.IsEmpty || rt.TextureId == 0)
                return;

            // Look up the real GL texture handle from the driver's texture list
            if (_gpuDriver is null)
                return;

            var textures = _gpuDriver.Textures;
            int texIdx = (int)rt.TextureId;
            if (texIdx < 0 || texIdx >= textures.Count || textures[texIdx] is null)
                return;

            uint glTextureId = textures[texIdx].TextureId;
            if (glTextureId == 0)
                return;

            GL? gl = _cachedGL;
            if (gl is null)
                return;

            EnsureReadFramebuffer();

            int srcWidth = (int)rt.TextureWidth;
            int srcHeight = (int)rt.TextureHeight;

            gl.BindFramebuffer(GLEnum.ReadFramebuffer, _readFramebufferId);
            gl.FramebufferTexture2D(GLEnum.ReadFramebuffer, GLEnum.ColorAttachment0,
                GLEnum.Texture2D, glTextureId, 0);

            gl.BindFramebuffer(GLEnum.DrawFramebuffer, _targetFramebufferId);

            gl.BlitFramebuffer(
                0, 0, srcWidth, srcHeight,
                0, 0, srcWidth, srcHeight,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear);

            gl.BindFramebuffer(GLEnum.ReadFramebuffer, 0);
            gl.BindFramebuffer(GLEnum.DrawFramebuffer, 0);
        }

        public bool TryGetFrame(out WebFrame frame)
        {
            frame = default;
            return false;
        }

        public void SetTargetFramebuffer(uint framebufferId)
        {
            _targetFramebufferId = framebufferId;
        }

        public void Dispose()
        {
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
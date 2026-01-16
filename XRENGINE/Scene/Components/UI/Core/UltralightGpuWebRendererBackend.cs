using System;
using System.Reflection;
using UltralightNet;
using UltralightNet.AppCore;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using Silk.NET.OpenGL;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Ultralight GPU backend that renders directly into a GL framebuffer (no CPU readback).
    /// </summary>
    public sealed class UltralightGpuWebRendererBackend : IWebRendererBackend
    {
        private static bool _fontLoaderInitialized;
        private static object? _gpuDriverInstance;
        
        // Cached reflection info to avoid per-frame allocations
        private static PropertyInfo? _cachedGpuDriverProp;
        private static Type? _cachedDriverType;
        private static bool _reflectionCacheInitialized;

        private UltralightNet.Renderer? _renderer;
        private View? _view;
        private ULConfig? _config;
        private ULViewConfig? _viewConfig;

        private uint _targetFramebufferId;
        private uint _readFramebufferId;
        
        // Cached GL reference to avoid LINQ allocation every frame
        private GL? _cachedGL;
        
        // Cached render target reflection info
        private PropertyInfo? _cachedTexIdProp;
        private PropertyInfo? _cachedWidthProp;
        private PropertyInfo? _cachedHeightProp;
        private Type? _cachedRenderTargetType;

        public bool IsInitialized => _renderer is not null && _view is not null;
        public bool SupportsFramebuffer => true;

        public void Initialize(uint width, uint height, bool transparentBackground)
        {
            Dispose();

            EnsureFontLoader();
            EnsureGpuDriver();
            CacheOpenGL();

            _config = new ULConfig
            {
                ForceRepaint = true
            };

            _viewConfig = new ULViewConfig
            {
                IsAccelerated = true,
                IsTransparent = transparentBackground
            };

            _renderer = ULPlatform.CreateRenderer(_config.Value);
            _view = _renderer.CreateView(width, height, _viewConfig, null);
            
            // Invalidate render target cache since view changed
            _cachedRenderTargetType = null;
            _cachedTexIdProp = null;
            _cachedWidthProp = null;
            _cachedHeightProp = null;

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

            if (!TryGetUltralightTexture(out uint textureId, out int srcWidth, out int srcHeight))
                return;

            GL? gl = _cachedGL;
            if (gl is null)
                return;

            EnsureReadFramebuffer();

            gl.BindFramebuffer(GLEnum.ReadFramebuffer, _readFramebufferId);
            gl.FramebufferTexture2D(GLEnum.ReadFramebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D, textureId, 0);

            gl.BindFramebuffer(GLEnum.DrawFramebuffer, _targetFramebufferId);

            int dstWidth = srcWidth;
            int dstHeight = srcHeight;
            gl.BlitFramebuffer(
                0, 0, srcWidth, srcHeight,
                0, 0, dstWidth, dstHeight,
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
            
            // Clear cached render target reflection info
            _cachedRenderTargetType = null;
            _cachedTexIdProp = null;
            _cachedWidthProp = null;
            _cachedHeightProp = null;

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
            _fontLoaderInitialized = true;
        }

        private static void EnsureGpuDriver()
        {
            if (_gpuDriverInstance is not null)
                return;

            // Cache reflection info once
            if (!_reflectionCacheInitialized)
            {
                Type ulPlatformType = typeof(ULPlatform);
                _cachedGpuDriverProp = ulPlatformType.GetProperty("GPUDriver", BindingFlags.Public | BindingFlags.Static);
                
                if (_cachedGpuDriverProp is not null)
                {
                    var assembly = ulPlatformType.Assembly;
                    var types = assembly.GetTypes();
                    for (int i = 0; i < types.Length; i++)
                    {
                        var t = types[i];
                        if (t.Name.Contains("GPUDriver", StringComparison.OrdinalIgnoreCase) &&
                            t.Name.Contains("OpenGL", StringComparison.OrdinalIgnoreCase) &&
                            t.GetConstructor(Type.EmptyTypes) is not null)
                        {
                            _cachedDriverType = t;
                            break;
                        }
                    }
                }
                _reflectionCacheInitialized = true;
            }

            if (_cachedGpuDriverProp is null)
                throw new InvalidOperationException("UltralightNet does not expose ULPlatform.GPUDriver.");

            if (_cachedDriverType is null)
                throw new InvalidOperationException("UltralightNet OpenGL GPU driver type not found.");

            _gpuDriverInstance = Activator.CreateInstance(_cachedDriverType);
            _cachedGpuDriverProp.SetValue(null, _gpuDriverInstance);
        }

        private bool TryGetUltralightTexture(out uint textureId, out int width, out int height)
        {
            textureId = 0;
            width = 0;
            height = 0;

            object? renderTarget = _view?.RenderTarget;
            if (renderTarget is null)
                return false;

            Type rtType = renderTarget.GetType();
            
            // Cache reflection info for render target type - only recalculate if type changes
            if (_cachedRenderTargetType != rtType)
            {
                _cachedRenderTargetType = rtType;
                _cachedTexIdProp = null;
                _cachedWidthProp = null;
                _cachedHeightProp = null;
                
                var props = rtType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < props.Length; i++)
                {
                    var p = props[i];
                    if (_cachedTexIdProp is null &&
                        p.Name.Contains("Texture", StringComparison.OrdinalIgnoreCase) &&
                        p.Name.Contains("Id", StringComparison.OrdinalIgnoreCase) &&
                        (p.PropertyType == typeof(uint) || p.PropertyType == typeof(int)))
                    {
                        _cachedTexIdProp = p;
                    }
                    else if (p.Name == "TextureWidth" || (_cachedWidthProp is null && p.Name == "Width"))
                    {
                        _cachedWidthProp = p;
                    }
                    else if (p.Name == "TextureHeight" || (_cachedHeightProp is null && p.Name == "Height"))
                    {
                        _cachedHeightProp = p;
                    }
                }
            }

            if (_cachedTexIdProp is null)
                return false;

            object? value = _cachedTexIdProp.GetValue(renderTarget);
            if (value is uint u)
                textureId = u;
            else if (value is int i)
                textureId = (uint)i;
            else
                return false;

            width = ReadCachedIntProperty(_cachedWidthProp, renderTarget) ?? 0;
            height = ReadCachedIntProperty(_cachedHeightProp, renderTarget) ?? 0;

            return textureId != 0 && width > 0 && height > 0;
        }

        private static int? ReadCachedIntProperty(PropertyInfo? prop, object instance)
        {
            if (prop is null)
                return null;

            object? value = prop.GetValue(instance);
            return value switch
            {
                int i => i,
                uint u => (int)u,
                _ => null
            };
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
using Extensions;
using Silk.NET.Maths;
using XREngine.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// Adapter responsible for presenting a window's world rendering inside a dockable editor "scene panel" region.
    /// This intentionally avoids the term "viewport" to reduce ambiguity with XRViewport (player viewports).
    /// </summary>
    internal sealed class XRWindowScenePanelAdapter : IDisposable
    {
        private XRTexture2D? _scenePanelTexture;
        private XRFrameBuffer? _scenePanelFBO;

        private bool _scenePanelSizingActive;
        private int _lastPanelWidth;
        private int _lastPanelHeight;

        private int _pendingPanelWidth;
        private int _pendingPanelHeight;
        private DateTime _panelResizeLastChangeUtc;

        public XRTexture2D? Texture => _scenePanelTexture;
        public XRFrameBuffer? FrameBuffer => _scenePanelFBO;

        public bool IsActiveForWindow(XRWindow window)
            => Engine.IsEditor &&
               Engine.Rendering.Settings.ViewportPresentationMode == Engine.Rendering.EngineSettings.EViewportPresentationMode.UseViewportPanel &&
               !window.IsDisposed;

        public void InvalidateResources()
        {
            DestroyFBO();
            _scenePanelSizingActive = false;
            _lastPanelWidth = 0;
            _lastPanelHeight = 0;
            _pendingPanelWidth = 0;
            _pendingPanelHeight = 0;
            _panelResizeLastChangeUtc = default;
        }

        /// <summary>
        /// Forces immediate destruction of the scene panel FBO/texture.
        /// Use this during play mode transitions to ensure stale content is not displayed.
        /// </summary>
        public void InvalidateResourcesImmediate()
        {
            // Force immediate destruction so the GL texture handle becomes invalid
            _scenePanelFBO?.Destroy(true);
            _scenePanelFBO = null;
            _scenePanelTexture?.Destroy(true);
            _scenePanelTexture = null;
            
            _scenePanelSizingActive = false;
            _lastPanelWidth = 0;
            _lastPanelHeight = 0;
            _pendingPanelWidth = 0;
            _pendingPanelHeight = 0;
            _panelResizeLastChangeUtc = default;
        }

        public void OnFramebufferResized(XRWindow window, Vector2D<int> framebufferSize)
        {
            // If the underlying framebuffer/swapchain changes, scene-panel resources can become stale.
            // Recreate them on demand next frame.
            InvalidateResources();

            // If we were previously in scene panel mode, also restore viewports to full framebuffer size.
            if (framebufferSize.X > 0 && framebufferSize.Y > 0)
                RestoreFullWindowSizing(window, framebufferSize.X, framebufferSize.Y);
        }

        public bool TryRenderScenePanelMode(XRWindow window)
        {
            BoundingRectangle? region = Engine.Rendering.ScenePanelRenderRegionProvider?.Invoke(window);
            if (!region.HasValue || region.Value.Width <= 0 || region.Value.Height <= 0)
            {
                Debug.RenderingEvery(
                    $"XRWindow.RenderCallback.{window.GetHashCode()}.ScenePanelRegionMissing",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Scene panel mode active but region missing/invalid. Region={0}",
                    region.HasValue ? $"{region.Value.Width}x{region.Value.Height}" : "<null>");

                EndScenePanelMode(window);
                return false;
            }

            // ImGui rendering typically leaves scissor/cropping enabled.
            // Ensure world rendering starts from a clean state so clears and passes aren't clipped/offset.
            window.Renderer.SetCroppingEnabled(false);

            int desiredWidth = region.Value.Width;
            int desiredHeight = region.Value.Height;

            int debounceMs = Engine.Rendering.Settings.ScenePanelResizeDebounceMs;
            if (debounceMs < 0)
                debounceMs = 0;

            // First creation should always happen immediately.
            if (_scenePanelFBO is null || _scenePanelTexture is null)
            {
                EnsureFBO(desiredWidth, desiredHeight);
                ApplyViewportSizingForFBO(window, desiredWidth, desiredHeight);
                ClearPendingResizeState();
            }
            else
            {
                DateTime nowUtc = DateTime.UtcNow;

                bool sizeChanged = desiredWidth != _lastPanelWidth || desiredHeight != _lastPanelHeight;
                if (sizeChanged)
                {
                    if (_pendingPanelWidth != desiredWidth || _pendingPanelHeight != desiredHeight)
                    {
                        _pendingPanelWidth = desiredWidth;
                        _pendingPanelHeight = desiredHeight;
                        _panelResizeLastChangeUtc = nowUtc;
                    }

                    bool shouldApplyResizeNow = debounceMs == 0 ||
                        (_panelResizeLastChangeUtc != default &&
                         (nowUtc - _panelResizeLastChangeUtc) >= TimeSpan.FromMilliseconds(debounceMs));

                    if (shouldApplyResizeNow)
                    {
                        int applyWidth = _pendingPanelWidth > 0 ? _pendingPanelWidth : desiredWidth;
                        int applyHeight = _pendingPanelHeight > 0 ? _pendingPanelHeight : desiredHeight;

                        EnsureFBO(applyWidth, applyHeight);
                        ApplyViewportSizingForFBO(window, applyWidth, applyHeight);

                        ClearPendingResizeState();
                    }
                    else
                    {
                        // Keep using the existing FBO/viewport size for a few frames.
                        // ImGui will scale the image; once stabilized we'll resize.
                        int currentWidth = _lastPanelWidth > 0 ? _lastPanelWidth : desiredWidth;
                        int currentHeight = _lastPanelHeight > 0 ? _lastPanelHeight : desiredHeight;
                        ApplyViewportSizingForFBO(window, currentWidth, currentHeight);
                    }
                }
                else
                {
                    ApplyViewportSizingForFBO(window, desiredWidth, desiredHeight);
                }
            }

            window.RenderViewportsToFBO(_scenePanelFBO);
            return true;
        }

        public void EndScenePanelMode(XRWindow window)
        {
            RestoreFullWindowSizing(window);
            DestroyFBO();
        }

        public void RestoreFullWindowSizing(XRWindow window)
        {
            if (!_scenePanelSizingActive)
                return;

            var fb = window.Window.FramebufferSize;
            if (fb.X > 0 && fb.Y > 0)
                RestoreFullWindowSizing(window, fb.X, fb.Y);
        }

        private void RestoreFullWindowSizing(XRWindow window, int framebufferWidth, int framebufferHeight)
        {
            if (window.Viewports.Count > 0)
            {
                foreach (var vp in window.Viewports)
                    vp.Resize((uint)framebufferWidth, (uint)framebufferHeight, setInternalResolution: true);
            }

            _scenePanelSizingActive = false;
            _lastPanelWidth = 0;
            _lastPanelHeight = 0;
        }

        private void ApplyViewportSizingForFBO(XRWindow window, int width, int height)
        {
            if (window.Viewports.Count == 0)
                return;

            bool sizeChanged =
                !_scenePanelSizingActive ||
                width != _lastPanelWidth ||
                height != _lastPanelHeight;

            if (sizeChanged)
            {
                foreach (var vp in window.Viewports)
                    vp.Resize((uint)width, (uint)height, setInternalResolution: true);
            }

            // For FBO mode, viewport position is always (0,0). ImGui positions the image.
            foreach (var vp in window.Viewports)
            {
                vp.X = 0;
                vp.Y = 0;
            }

            _scenePanelSizingActive = true;
            _lastPanelWidth = width;
            _lastPanelHeight = height;
        }

        private void EnsureFBO(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            bool needsResize = _scenePanelTexture is not null &&
                (_scenePanelTexture.Width != (uint)width || _scenePanelTexture.Height != (uint)height);

            if (_scenePanelFBO is null || needsResize)
            {
                _scenePanelTexture?.Destroy();

                _scenePanelTexture = XRTexture2D.CreateFrameBufferTexture(
                    (uint)width,
                    (uint)height,
                    EPixelInternalFormat.Rgba8,
                    EPixelFormat.Rgba,
                    EPixelType.UnsignedByte,
                    EFrameBufferAttachment.ColorAttachment0);
                _scenePanelTexture.Resizable = true;
                _scenePanelTexture.MinFilter = ETexMinFilter.Linear;
                _scenePanelTexture.MagFilter = ETexMagFilter.Linear;
                _scenePanelTexture.UWrap = ETexWrapMode.ClampToEdge;
                _scenePanelTexture.VWrap = ETexWrapMode.ClampToEdge;
                _scenePanelTexture.Name = "ScenePanelTexture";

                _scenePanelFBO?.Destroy();
                _scenePanelFBO = new XRFrameBuffer((_scenePanelTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = "ScenePanelFBO"
                };
            }
        }

        private void DestroyFBO()
        {
            _scenePanelFBO?.Destroy();
            _scenePanelFBO = null;
            _scenePanelTexture?.Destroy();
            _scenePanelTexture = null;
        }

        private void ClearPendingResizeState()
        {
            _pendingPanelWidth = 0;
            _pendingPanelHeight = 0;
            _panelResizeLastChangeUtc = default;
        }

        public void Dispose()
        {
            DestroyFBO();
            GC.SuppressFinalize(this);
        }
    }
}

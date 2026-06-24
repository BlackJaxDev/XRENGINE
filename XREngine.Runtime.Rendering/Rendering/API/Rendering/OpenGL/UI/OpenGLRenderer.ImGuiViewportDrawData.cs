using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.OpenGL
{
    public partial class OpenGLRenderer
    {
        private sealed unsafe partial class OpenGLImGuiMultiViewportController
        {
            public void RenderPlatformWindows()
            {
                if (!_installed || _disposed)
                    return;

                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0)
                    return;

                nint previousContext = ImGui.GetCurrentContext();
                try
                {
                    DisposePendingPlatformWindows();
                    UpdatePlatformMonitors();
                    ImGui.UpdatePlatformWindows();
                    ImGui.RenderPlatformWindowsDefault();
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(RenderPlatformWindows), ex);
                }
                finally
                {
                    try
                    {
                        _mainWindow.MakeCurrent();
                    }
                    catch (Exception ex)
                    {
                        LogCallbackException("RestoreMainOpenGLContext", ex);
                    }

                    if (previousContext == nint.Zero)
                    {
                        ImGui.SetCurrentContext(nint.Zero);
                    }
                    else if (ImGuiContextTracker.IsAlive(previousContext))
                    {
                        ImGui.SetCurrentContext(previousContext);
                    }
                }
            }

            /// <summary>
            /// Unhooks platform callbacks, disposes popup windows, and disables multi-viewport mode.
            /// </summary>
            private void PlatformRenderWindow(ImGuiViewport* nativeViewport, void* renderArg)
            {
                try
                {
                    var viewport = new ImGuiViewportPtr(nativeViewport);
                    if (GetPlatformWindow(viewport) is not { } window)
                        return;

                    window.Window.MakeCurrent();
                    using var clipScope = _renderer.PushUiClipSpacePolicy();
                    Vector2D<int> framebufferSize = window.Window.FramebufferSize;
                    _renderer.Api.Viewport(0, 0, (uint)Math.Max(1, framebufferSize.X), (uint)Math.Max(1, framebufferSize.Y));

                    if ((viewport.Flags & ImGuiViewportFlags.NoRendererClear) == 0)
                    {
                        _renderer.Api.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
                        _renderer.Api.Clear(ClearBufferMask.ColorBufferBit);
                    }
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformRenderWindow), ex);
                }
            }

            private void PlatformSwapBuffers(ImGuiViewport* nativeViewport, void* renderArg)
            {
                try
                {
                    if (GetPlatformWindow(new ImGuiViewportPtr(nativeViewport)) is { } window)
                        window.Window.GLContext?.SwapBuffers();
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformSwapBuffers), ex);
                }
            }

            private void RendererRenderWindow(ImGuiViewport* nativeViewport, void* renderArg)
            {
                try
                {
                    var viewport = new ImGuiViewportPtr(nativeViewport);
                    ImDrawDataPtr drawData = viewport.DrawData;
                    if (drawData.NativePtr is null)
                        return;

                    using var clipScope = _renderer.PushUiClipSpacePolicy();
                    RenderImDrawData!(_controller, drawData);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(RendererRenderWindow), ex);
                }
            }

            private void RendererSwapBuffers(ImGuiViewport* nativeViewport, void* renderArg)
            {
            }


        }
    }
}

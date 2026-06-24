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
            private static RenderImDrawDataDelegate? CreateRenderImDrawDataDelegate()
            {
                try
                {
                    MethodInfo? method = typeof(ImGuiController).GetMethod("RenderImDrawData", BindingFlags.Instance | BindingFlags.NonPublic);
                    return method?.CreateDelegate<RenderImDrawDataDelegate>();
                }
                catch
                {
                    return null;
                }
            }

            private static TranslateInputKeyDelegate? CreateTranslateInputKeyDelegate()
            {
                try
                {
                    MethodInfo? method = typeof(ImGuiController).GetMethod("TranslateInputKeyToImGuiKey", BindingFlags.Static | BindingFlags.NonPublic);
                    return method?.CreateDelegate<TranslateInputKeyDelegate>();
                }
                catch
                {
                    return null;
                }
            }

            private static void LogCallbackException(string callback, Exception ex)
            {
                Debug.RenderingWarningEvery(
                    $"OpenGL.ImGui.MultiViewport.{callback}",
                    TimeSpan.FromSeconds(2),
                    "[ImGuiMultiViewport] {0} failed: {1}",
                    callback,
                    ex.Message);
            }
        }
    }
}

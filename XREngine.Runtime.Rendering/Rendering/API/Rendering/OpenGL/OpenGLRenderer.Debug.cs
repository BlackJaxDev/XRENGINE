using XREngine.Extensions;
using ImageMagick;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private unsafe static void SetupDebug(GL api)
    {
        // Be defensive here: some drivers (or non-debug contexts) can report GL_INVALID_OPERATION
        // for DebugMessageControl even though the process otherwise supports OpenGL.
        // We still try to enable the callback; we just skip driver-level filtering when unsupported.

        bool supportsDebugOutput = true;
        string[]? extensions = Engine.Rendering.State.OpenGLExtensions;
        if (extensions is { Length: > 0 })
        {
            supportsDebugOutput = extensions.Any(static e =>
                string.Equals(e, "GL_KHR_debug", StringComparison.Ordinal)
                || string.Equals(e, "GL_ARB_debug_output", StringComparison.Ordinal));
        }

        if (!supportsDebugOutput)
            return;

        try
        {
            api.Enable(EnableCap.DebugOutput);
            api.Enable(EnableCap.DebugOutputSynchronous);
            api.DebugMessageCallback(DebugCallback, null);
        }
        catch
        {
            return;
        }

        bool isDebugContext = false;
        try
        {
            int flags = api.GetInteger(GLEnum.ContextFlags);
            isDebugContext = ((ContextFlagMask)flags).HasFlag(ContextFlagMask.DebugBit);
        }
        catch
        {
            // If we can't query flags, assume non-debug context.
        }

        if (!isDebugContext)
            return;

        // Disable known-noisy messages at the driver level to avoid spamming logs.
        if (_ignoredMessageIds.Length == 0)
            return;

        try
        {
            uint[] ids = Array.ConvertAll(_ignoredMessageIds, static x => unchecked((uint)x));
            fixed (uint* ptr = ids)
                api.DebugMessageControl(GLEnum.DontCare, GLEnum.DontCare, GLEnum.DontCare, (uint)ids.Length, ptr, false);
        }
        catch
        {
            // Ignore: callback will still work, we just won't filter spammy IDs.
        }
    }

    private static int[] _ignoredMessageIds =
    [
        131185, //buffer will use video memory
        131204, //no base level, no mipmaps, etc
        131169, //allocated memory for render buffer
        131154, //pixel transfer is synchronized with 3d rendering
        //131216,
        131218,
        131076,
        131139, //Rasterization quality warning: A non-fullscreen clear caused a fallback from CSAA to MSAA.
        131186, //Buffer performance warning: buffer is being copied/moved from video memory to host memory.
        131188, //Buffer usage warning: Analysis of buffer object usage indicates that CPU is consuming buffer object data.  The usage hint supplied with this buffer object, GL_DYNAMIC_COPY, is inconsistent with this usage pattern.  Try using GL_STREAM_READ_ARB, GL_STATIC_READ_ARB, or GL_DYNAMIC_READ_ARB instead.
        131220, //Program/shader state usage warning (integer framebuffer): fragment shader required (often spammy during Clear on some drivers)
        //1282,
        //0,
        //9,
    ];
    private static int[] _printMessageIds =
    [
        //1280, //Invalid texture format and type combination
        //1281, //Invalid texture format
        //1282,
    ];

    public unsafe static void DebugCallback(GLEnum source, GLEnum type, int id, GLEnum severity, int length, nint message, nint userParam)
    {
        // Never suppress actual GL error messages.
        if (type != GLEnum.DebugTypeError && _ignoredMessageIds.IndexOf(id) >= 0)
            return;

        string messageStr = new((sbyte*)message);
        string formattedMessage = $"OPENGL {FormatSeverity(severity)} #{id} | {FormatSource(source)} {FormatType(type)} | {messageStr}";

        // Mirror high-severity messages to Console.Error synchronously so they survive a
        // driver fastfail (NVIDIA FAST_FAIL_CORRUPT_LIST_ENTRY tears the process down before
        // Serilog flushes). Errors and high-severity messages only; keep noise low.
        if (type == GLEnum.DebugTypeError || severity == GLEnum.DebugSeverityHigh)
        {
            try { System.Console.Error.WriteLine("[GLDebug] " + formattedMessage); } catch { }
            try { System.Diagnostics.Trace.WriteLine("[GLDebug] " + formattedMessage); System.Diagnostics.Trace.Flush(); } catch { }
        }

        if (type == GLEnum.DebugTypeError)
        {
            if (Current is OpenGLRenderer renderer)
            {
                string context = renderer.BuildOpenGLErrorContext();
                if (!string.IsNullOrWhiteSpace(context))
                    formattedMessage = $"{formattedMessage}{Environment.NewLine}{context}";
            }

            // Keep driver-reported GL errors deeper than ordinary warnings so the log reaches the calling render pass.
            Debug.LogWarning(ELogCategory.OpenGL, 0, 10, formattedMessage);
        }
        else
        {
            Debug.OpenGLWarning(formattedMessage);
        }
        bool shouldTrack = type == GLEnum.DebugTypeError;
        RecordOpenGLError(id, FormatSource(source), FormatType(type), FormatSeverity(severity), messageStr, shouldTrack);

        // OOM errors leave the driver in a corrupted state � flag it so draw calls are skipped for the rest of this frame.
        if (id == 1285 || messageStr.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
        {
            if (Current is OpenGLRenderer renderer)
                renderer._oomDetectedThisFrame = true;
        }
    }

    private static string FormatSeverity(GLEnum severity)
        => severity switch
        {
            GLEnum.DebugSeverityHigh => "High",
            GLEnum.DebugSeverityMedium => "Medium",
            GLEnum.DebugSeverityLow => "Low",
            GLEnum.DebugSeverityNotification => "Notification",
            _ => severity.ToString(),
        };

    private static string FormatType(GLEnum type)
        => type switch
        {
            GLEnum.DebugTypeError => "Error",
            GLEnum.DebugTypeDeprecatedBehavior => "Deprecated Behavior",
            GLEnum.DebugTypeUndefinedBehavior => "Undefined Behavior",
            GLEnum.DebugTypePortability => "Portability",
            GLEnum.DebugTypePerformance => "Performance",
            GLEnum.DebugTypeOther => "Other",
            GLEnum.DebugTypeMarker => "Marker",
            GLEnum.DebugTypePushGroup => "Push Group",
            GLEnum.DebugTypePopGroup => "Pop Group",
            _ => type.ToString(),
        };

    private static string FormatSource(GLEnum source)
        => source switch
        {
            GLEnum.DebugSourceApi => "API",
            GLEnum.DebugSourceWindowSystem => "Window System",
            GLEnum.DebugSourceShaderCompiler => "Shader Compiler",
            GLEnum.DebugSourceThirdParty => "Third Party",
            GLEnum.DebugSourceApplication => "Application",
            GLEnum.DebugSourceOther => "Other",
            _ => source.ToString(),
        };

    public static void CheckError(string? name)
    {
        //if (Current is not OpenGLRenderer renderer)
        //    return;

        //var error = renderer.Api.GetError();
        //if (error != GLEnum.NoError)
        //    Debug.LogWarning(name is null ? error.ToString() : $"{name}: {error}", 1);
    }

    public bool LogGLErrors(string context)
    {
        bool hadError = false;
        GLEnum error;
        while ((error = Api.GetError()) != GLEnum.NoError)
        {
            hadError = true;
            Debug.OpenGLWarning($"OpenGL error after {context}: {error}");
        }

        return hadError;
    }
}

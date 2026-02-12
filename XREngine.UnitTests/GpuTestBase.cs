using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace XREngine.UnitTests;

/// <summary>
/// Base class for unit tests that require an OpenGL window/context.
/// Provides common helpers for window creation, shader compilation, and
/// guaranteed auto-close so tests never hang waiting for human intervention.
/// <para>
/// Usage: inherit from this class and call <see cref="RunWithGLContext(Action{GL}, int, bool)"/>
/// or <see cref="CreateGLContext"/> + manual try/finally with <see cref="DisposeContext"/>.
/// </para>
/// </summary>
public abstract class GpuTestBase
{
    /// <summary>Default hidden-window dimensions (small — we only need an OpenGL context).</summary>
    protected const int DefaultWidth = 64;
    protected const int DefaultHeight = 64;

    /// <summary>Default timeout before a test window is forcibly closed (30 s).</summary>
    protected const int DefaultTimeoutMs = 30_000;

    /// <summary>Override to change the default window width for a fixture.</summary>
    protected virtual int Width => DefaultWidth;

    /// <summary>Override to change the default window height for a fixture.</summary>
    protected virtual int Height => DefaultHeight;

    // ───────────────────────── environment helpers ──────────────────────

    /// <summary>True when running in a headless CI environment.</summary>
    protected static bool IsHeadless =>
        Environment.GetEnvironmentVariable("XR_HEADLESS_TEST") == "1" ||
        Environment.GetEnvironmentVariable("CI") == "true";

    /// <summary>True when the user explicitly asked for visible test windows.</summary>
    protected static bool ShowWindow => IsTrue(Environment.GetEnvironmentVariable("XR_SHOW_TEST_WINDOWS"))
        || IsTrue(TestContext.Parameters.Get("ShowWindow"));

    /// <summary>Duration (ms) a visible window should stay open for visual inspection.
    /// Defaults to 2 000 ms; override via <c>XR_SHOW_WINDOW_DURATION_MS</c> env var.</summary>
    protected static int ShowWindowDurationMs
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable("XR_SHOW_WINDOW_DURATION_MS")
                        ?? TestContext.Parameters.Get("ShowWindowDurationMs");
            return int.TryParse(v, out int ms) && ms > 0 ? ms : 2_000;
        }
    }

    /// <summary>Loose truthy-string parser: "1", "true", "yes", "on" (case-insensitive).</summary>
    protected static bool IsTrue(string? value)
        => value is not null &&
           (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase));

    // ─────────────────────── shader-source helpers ──────────────────────

    /// <summary>Walks up from the build output directory to find <c>Build/CommonAssets/Shaders</c>.</summary>
    protected static string ShaderBasePath
    {
        get
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                string candidate = Path.Combine(dir, "Build", "CommonAssets", "Shaders");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
            return Path.Combine(AppContext.BaseDirectory, "Build", "CommonAssets", "Shaders");
        }
    }

    /// <summary>Reads a shader source file relative to <see cref="ShaderBasePath"/>,
    /// calling <see cref="Assert.Inconclusive(string)"/> if it doesn't exist.</summary>
    protected static string LoadShaderSource(string relativePath)
    {
        string path = Path.Combine(ShaderBasePath, relativePath);
        if (!File.Exists(path))
            Assert.Inconclusive($"Shader file not found: {path}");
        return File.ReadAllText(path);
    }

    // ─────────────────── GL context creation / disposal ─────────────────

    /// <summary>
    /// Creates a (potentially hidden) OpenGL 4.6 Core window+context.
    /// Returns <c>(null, null)</c> on headless CI or when no GPU is available.
    /// </summary>
    protected (GL? gl, IWindow? window) CreateGLContext(bool visible = false, int width = 0, int height = 0)
    {
        if (IsHeadless)
            return (null, null);

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(
            width > 0 ? width : Width,
            height > 0 ? height : Height);
        options.IsVisible = visible && ShowWindow;
        options.API = new GraphicsAPI(
            ContextAPI.OpenGL,
            ContextProfile.Core,
            ContextFlags.ForwardCompatible,
            new APIVersion(4, 6));

        IWindow? window = null;
        GL? gl = null;
        try
        {
            window = Window.Create(options);
            window.Initialize();
            window.MakeCurrent();
            window.DoEvents();
            gl = GL.GetApi(window);
        }
        catch
        {
            window?.Close();
            window?.Dispose();
            return (null, null);
        }
        return (gl, window);
    }

    /// <summary>Safely closes and disposes an OpenGL window (idempotent).</summary>
    protected static void DisposeContext(IWindow? window)
    {
        if (window is null) return;
        try { window.Close(); } catch { /* best-effort */ }
        try { window.Dispose(); } catch { /* best-effort */ }
    }

    // ─────────────── auto-managed GL context (recommended) ─────────────

    /// <summary>
    /// Runs <paramref name="testAction"/> in an auto-managed GL context that is
    /// <b>guaranteed</b> to be closed after the action completes, throws, or
    /// <paramref name="timeoutMs"/> elapses — whichever comes first.
    /// <para>If no GPU context is available the test is marked Inconclusive.</para>
    /// </summary>
    protected void RunWithGLContext(
        Action<GL> testAction,
        int timeoutMs = DefaultTimeoutMs,
        bool visible = false)
    {
        RunWithGLContext((gl, _) => testAction(gl), timeoutMs, visible);
    }

    /// <summary>
    /// Overload that also exposes the <see cref="IWindow"/> (e.g. for swapchain tests).
    /// The window is auto-closed after <paramref name="timeoutMs"/>.
    /// </summary>
    protected void RunWithGLContext(
        Action<GL, IWindow> testAction,
        int timeoutMs = DefaultTimeoutMs,
        bool visible = false)
    {
        var (gl, window) = CreateGLContext(visible);
        if (gl is null || window is null)
        {
            Assert.Inconclusive("Could not create OpenGL context (headless or no GPU).");
            return;
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        CancellationTokenRegistration reg = default;
        try
        {
            // If the timeout fires, forcibly close the window from whatever
            // thread the CTS callback runs on — unblocking any render loop.
            reg = cts.Token.Register(() =>
            {
                try { window.Close(); } catch { /* best-effort */ }
            });

            testAction(gl, window);
        }
        finally
        {
            reg.Dispose();
            DisposeContext(window);
        }
    }

    /// <summary>
    /// Runs <paramref name="testAction"/> with a <see cref="CancellationToken"/> that
    /// fires after <paramref name="timeoutMs"/>, allowing the test body to cooperatively
    /// check for cancellation instead of relying on forcible window closure.
    /// </summary>
    protected void RunWithGLContext(
        Action<GL, IWindow, CancellationToken> testAction,
        int timeoutMs = DefaultTimeoutMs,
        bool visible = false)
    {
        var (gl, window) = CreateGLContext(visible);
        if (gl is null || window is null)
        {
            Assert.Inconclusive("Could not create OpenGL context (headless or no GPU).");
            return;
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        CancellationTokenRegistration reg = default;
        try
        {
            reg = cts.Token.Register(() =>
            {
                try { window.Close(); } catch { /* best-effort */ }
            });

            testAction(gl, window, cts.Token);
        }
        finally
        {
            reg.Dispose();
            DisposeContext(window);
        }
    }

    // ──────────────── hardware capability checks ───────────────────────

    /// <summary>Marks the test Inconclusive if the driver doesn't support GL_ARB_compute_shader.</summary>
    protected static void AssertHardwareComputeOrInconclusive(GL gl)
    {
        // glGetInteger(GL_MAX_COMPUTE_WORK_GROUP_COUNT, 0) returns 0 when unsupported.
        gl.GetInteger(GetPName.MaxComputeWorkGroupCount, out int maxWgX);
        if (maxWgX <= 0)
            Assert.Inconclusive("GPU does not support compute shaders (maxComputeWorkGroupCount.x = 0).");
    }

    // ──────────────── shader compilation helpers ───────────────────────

    /// <summary>Compiles a GLSL compute shader and returns its GL handle.</summary>
    protected static uint CompileComputeShader(GL gl, string source)
        => CompileShader(gl, ShaderType.ComputeShader, source);

    /// <summary>Links a single compute shader into a program and returns the program handle.</summary>
    protected static uint CreateComputeProgram(GL gl, uint computeShader)
    {
        uint program = gl.CreateProgram();
        gl.AttachShader(program, computeShader);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string infoLog = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"Compute program linking failed:\n{infoLog}");
        }
        return program;
    }

    /// <summary>Compiles a GLSL shader of any type and returns its GL handle.</summary>
    protected static uint CompileShader(GL gl, ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string infoLog = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compilation failed:\n{infoLog}");
        }
        return shader;
    }

    /// <summary>Links a vertex + fragment shader pair into a program.</summary>
    protected static uint LinkProgram(GL gl, uint vertexShader, uint fragmentShader)
    {
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string infoLog = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"Program linking failed:\n{infoLog}");
        }
        return program;
    }

    /// <summary>Compiles vertex + fragment sources and links them into a program.</summary>
    protected static uint CreateProgram(GL gl, string vertexSource, string fragmentSource)
    {
        uint vs = CompileShader(gl, ShaderType.VertexShader, vertexSource);
        uint fs = CompileShader(gl, ShaderType.FragmentShader, fragmentSource);
        try
        {
            return LinkProgram(gl, vs, fs);
        }
        finally
        {
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
        }
    }
}

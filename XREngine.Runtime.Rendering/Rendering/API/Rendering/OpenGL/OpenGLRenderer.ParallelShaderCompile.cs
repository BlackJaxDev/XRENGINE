using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private const string ArbParallelShaderCompileExtensionName = "GL_ARB_parallel_shader_compile";
    private const string KhrParallelShaderCompileExtensionName = "GL_KHR_parallel_shader_compile";
    private const uint ParallelShaderCompileImplementationMaxThreads = 0xFFFF_FFFFu;
    private const int GL_MAX_SHADER_COMPILER_THREADS = 0x91B0;
    private const int GL_COMPLETION_STATUS = 0x91B1;

    private delegate void GlMaxShaderCompilerThreadsDelegate(uint count);

    private GlMaxShaderCompilerThreadsDelegate? _glMaxShaderCompilerThreadsKhr;
    private ArbParallelShaderCompile? _arbParallelShaderCompile;
    private uint _configuredParallelShaderCompilerThreadCount;
    private bool _parallelShaderCompileSupported;
    private bool _parallelShaderCompileProbePassed;
    private string _parallelShaderCompileExtensionName = string.Empty;

    [ThreadStatic]
    private static bool _suppressDriverParallelShaderCompile;

    private static bool WantsSharedContextProgramCompileLinkQueue
    {
        get
        {
            if (!Engine.Rendering.Settings.AsyncProgramCompilation)
                return false;

            return Engine.Rendering.Settings.OpenGLShaderLinkStrategy switch
            {
                // Auto creates the shared-context queue as a fallback lane, but the
                // selector only uses it when the driver-parallel startup probe fails.
                EOpenGLShaderLinkStrategy.Auto => true,
                EOpenGLShaderLinkStrategy.SharedContext => true,
                _ => false,
            };
        }
    }

    internal bool UseSharedContextProgramCompileLinkQueue
    {
        get
        {
            if (!WantsSharedContextProgramCompileLinkQueue)
                return false;

            return Engine.Rendering.Settings.OpenGLShaderLinkStrategy switch
            {
                EOpenGLShaderLinkStrategy.Auto => !_parallelShaderCompileProbePassed && ProgramCompileLinkQueue is { IsAvailable: true },
                EOpenGLShaderLinkStrategy.SharedContext => true,
                _ => false,
            };
        }
    }

    internal bool UseDriverParallelShaderCompile
    {
        get
        {
            if (_suppressDriverParallelShaderCompile)
                return false;

            if (!_parallelShaderCompileSupported)
                return false;

            EOpenGLShaderLinkStrategy strategy = Engine.Rendering.Settings.OpenGLShaderLinkStrategy;
            if (strategy != EOpenGLShaderLinkStrategy.DriverParallel &&
                !Engine.Rendering.Settings.AsyncProgramCompilation)
            {
                return false;
            }

            return strategy switch
            {
                EOpenGLShaderLinkStrategy.DriverParallel =>
                    !Engine.Rendering.Settings.OpenGLParallelShaderCompileProbeEnabled ||
                    _parallelShaderCompileProbePassed,
                EOpenGLShaderLinkStrategy.Auto => _parallelShaderCompileProbePassed,
                _ => false,
            };
        }
    }

    private void ConfigureParallelShaderCompile(GL api, string[] extensions)
    {
        bool hasArb = HasExtension(extensions, ArbParallelShaderCompileExtensionName);
        bool hasKhr = HasExtension(extensions, KhrParallelShaderCompileExtensionName);
        _parallelShaderCompileSupported = hasArb || hasKhr;
        _parallelShaderCompileExtensionName = hasArb
            ? ArbParallelShaderCompileExtensionName
            : hasKhr
                ? KhrParallelShaderCompileExtensionName
                : string.Empty;

        Engine.Rendering.State.HasParallelShaderCompile = _parallelShaderCompileSupported;
        Engine.Rendering.State.OpenGLParallelShaderCompileExtension = _parallelShaderCompileExtensionName;
        Engine.Rendering.State.OpenGLParallelShaderCompileProbePassed = false;

        if (!_parallelShaderCompileSupported)
        {
            _parallelShaderCompileProbePassed = false;
            Debug.OpenGL("OpenGL parallel shader compile extension unavailable.");
            return;
        }

        LoadKhrParallelShaderCompileDelegate();
        _arbParallelShaderCompile =
            hasArb && api.TryGetExtension<ArbParallelShaderCompile>(out var arbExt) ? arbExt : null;

        uint requestedThreads = ResolveParallelShaderCompilerThreadCount();
        _configuredParallelShaderCompilerThreadCount = requestedThreads;
        bool threadCountSet = TrySetMaxShaderCompilerThreads(requestedThreads, _arbParallelShaderCompile);
        int reportedThreads = TryGetMaxShaderCompilerThreads(api);

        bool shouldRunProbe = ShouldRunParallelShaderCompileProbe();
        string probeResult = "skipped";
        if (shouldRunProbe)
        {
            _parallelShaderCompileProbePassed = ProbeParallelShaderCompile(
                api,
                Engine.Rendering.Settings.OpenGLParallelShaderCompileProbeTimeoutMs);
            probeResult = _parallelShaderCompileProbePassed ? "passed" : "failed";
        }
        else
        {
            _parallelShaderCompileProbePassed = false;
            if (!Engine.Rendering.Settings.OpenGLParallelShaderCompileProbeEnabled)
                probeResult = "disabled";
        }

        Engine.Rendering.State.OpenGLParallelShaderCompileProbePassed = _parallelShaderCompileProbePassed;

        Debug.OpenGL(
            $"OpenGL parallel shader compile: extension={_parallelShaderCompileExtensionName}, " +
            $"requestedThreads={FormatThreadCount(requestedThreads)}, reportedThreads={reportedThreads}, " +
            $"set={threadCountSet}, probe={probeResult}, " +
            $"strategy={Engine.Rendering.Settings.OpenGLShaderLinkStrategy}.");
    }

    private static bool ShouldRunParallelShaderCompileProbe()
    {
        if (!Engine.Rendering.Settings.OpenGLParallelShaderCompileProbeEnabled)
            return false;

        return Engine.Rendering.Settings.OpenGLShaderLinkStrategy switch
        {
            EOpenGLShaderLinkStrategy.Auto => true,
            EOpenGLShaderLinkStrategy.DriverParallel => true,
            _ => false,
        };
    }

    private static bool HasExtension(string[] extensions, string name)
    {
        for (int i = 0; i < extensions.Length; i++)
            if (string.Equals(extensions[i], name, StringComparison.Ordinal))
                return true;

        return false;
    }

    private void LoadKhrParallelShaderCompileDelegate()
    {
        if (_glMaxShaderCompilerThreadsKhr is not null)
            return;

        if (Window.GLContext is not INativeContext nativeContext)
            return;

        if (nativeContext.TryGetProcAddress("glMaxShaderCompilerThreadsKHR", out IntPtr proc) && proc != IntPtr.Zero)
            _glMaxShaderCompilerThreadsKhr = Marshal.GetDelegateForFunctionPointer<GlMaxShaderCompilerThreadsDelegate>(proc);
    }

    private static uint ResolveParallelShaderCompilerThreadCount()
    {
        int configured = Engine.Rendering.Settings.OpenGLShaderCompilerThreadCount;
        if (configured < 0)
            return ParallelShaderCompileImplementationMaxThreads;

        return (uint)configured;
    }

    private bool TrySetMaxShaderCompilerThreads(uint count, ArbParallelShaderCompile? arbParallelShaderCompile)
    {
        try
        {
            if (_parallelShaderCompileExtensionName == ArbParallelShaderCompileExtensionName &&
                arbParallelShaderCompile is not null)
            {
                arbParallelShaderCompile.MaxShaderCompilerThreads(count);
                return true;
            }

            if (_parallelShaderCompileExtensionName == KhrParallelShaderCompileExtensionName &&
                _glMaxShaderCompilerThreadsKhr is not null)
            {
                _glMaxShaderCompilerThreadsKhr(count);
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.OpenGLWarning($"Failed to set OpenGL parallel shader compiler thread count: {ex.Message}");
        }

        return false;
    }

    private static int TryGetMaxShaderCompilerThreads(GL api)
    {
        try
        {
            return api.GetInteger((GLEnum)GL_MAX_SHADER_COMPILER_THREADS);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Temporarily disables driver parallel shader compilation by setting
    /// <c>glMaxShaderCompilerThreadsKHR(0)</c>. Use around link operations on
    /// programs that are known to wedge the driver's parallel-link worker
    /// (notably single-stage separable programs on NVIDIA). Returns true if
    /// parallel compile was active and was successfully disabled; the caller
    /// must pair a successful call with <see cref="RestoreParallelShaderCompile"/>.
    /// While disabled, <see cref="UseDriverParallelShaderCompile"/> reports
    /// false on the calling thread so dependent code paths take their
    /// synchronous branches.
    /// </summary>
    internal bool TryDisableParallelShaderCompileForHazardousLink()
    {
        if (!_parallelShaderCompileSupported)
            return false;

        if (_configuredParallelShaderCompilerThreadCount == 0)
            return false;

        if (!TrySetMaxShaderCompilerThreads(0, _arbParallelShaderCompile))
            return false;

        _suppressDriverParallelShaderCompile = true;
        return true;
    }

    /// <summary>
    /// Restores the driver parallel shader compiler thread count previously
    /// configured at startup. Pair with a successful
    /// <see cref="TryDisableParallelShaderCompileForHazardousLink"/>.
    /// </summary>
    internal void RestoreParallelShaderCompile()
    {
        _suppressDriverParallelShaderCompile = false;

        if (!_parallelShaderCompileSupported)
            return;

        TrySetMaxShaderCompilerThreads(_configuredParallelShaderCompilerThreadCount, _arbParallelShaderCompile);
    }

    private bool ProbeParallelShaderCompile(GL api, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds <= 0)
            return true;

        uint vertexShader = 0;
        uint fragmentShader = 0;
        uint program = 0;

        try
        {
            vertexShader = api.CreateShader(ShaderType.VertexShader);
            fragmentShader = api.CreateShader(ShaderType.FragmentShader);
            program = api.CreateProgram();

            api.ShaderSource(vertexShader, "#version 460 core\nlayout(location=0) in vec3 Position;\nvoid main(){gl_Position=vec4(Position,1.0);}\n");
            api.ShaderSource(fragmentShader, "#version 460 core\nout vec4 FragColor;\nvoid main(){FragColor=vec4(1.0);}\n");
            api.CompileShader(vertexShader);
            api.CompileShader(fragmentShader);

            if (!WaitForShaderCompletion(api, vertexShader, timeoutMilliseconds) ||
                !WaitForShaderCompletion(api, fragmentShader, timeoutMilliseconds) ||
                !CheckShaderCompileStatus(api, vertexShader) ||
                !CheckShaderCompileStatus(api, fragmentShader))
            {
                return false;
            }

            api.AttachShader(program, vertexShader);
            api.AttachShader(program, fragmentShader);
            api.LinkProgram(program);

            if (!WaitForProgramCompletion(api, program, timeoutMilliseconds))
                return false;

            api.GetProgram(program, GLEnum.LinkStatus, out int linkStatus);
            return linkStatus != 0;
        }
        catch (Exception ex)
        {
            Debug.OpenGLWarning($"OpenGL parallel shader compile probe failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (program != 0)
            {
                if (vertexShader != 0)
                    api.DetachShader(program, vertexShader);
                if (fragmentShader != 0)
                    api.DetachShader(program, fragmentShader);
                api.DeleteProgram(program);
            }

            if (vertexShader != 0)
                api.DeleteShader(vertexShader);
            if (fragmentShader != 0)
                api.DeleteShader(fragmentShader);
        }
    }

    private static bool WaitForShaderCompletion(GL api, uint shader, int timeoutMilliseconds)
    {
        long start = Stopwatch.GetTimestamp();
        long timeoutTicks = MillisecondsToStopwatchTicks(timeoutMilliseconds);
        do
        {
            api.GetShader(shader, (GLEnum)GL_COMPLETION_STATUS, out int complete);
            if (complete != 0)
                return true;
        }
        while (Stopwatch.GetTimestamp() - start < timeoutTicks);

        return false;
    }

    private static bool WaitForProgramCompletion(GL api, uint program, int timeoutMilliseconds)
    {
        long start = Stopwatch.GetTimestamp();
        long timeoutTicks = MillisecondsToStopwatchTicks(timeoutMilliseconds);
        do
        {
            api.GetProgram(program, (GLEnum)GL_COMPLETION_STATUS, out int complete);
            if (complete != 0)
                return true;
        }
        while (Stopwatch.GetTimestamp() - start < timeoutTicks);

        return false;
    }

    private static bool CheckShaderCompileStatus(GL api, uint shader)
    {
        api.GetShader(shader, GLEnum.CompileStatus, out int status);
        if (status != 0)
            return true;

        api.GetShaderInfoLog(shader, out string? info);
        Debug.OpenGLWarning(string.IsNullOrWhiteSpace(info)
            ? "OpenGL parallel shader compile probe shader failed without an info log."
            : info);
        return false;
    }

    private static long MillisecondsToStopwatchTicks(int milliseconds)
        => (long)(milliseconds * (Stopwatch.Frequency / 1000.0));

    private static string FormatThreadCount(uint count)
        => count == ParallelShaderCompileImplementationMaxThreads ? "implementation-max" : count.ToString();
}

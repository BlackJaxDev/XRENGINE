using XREngine.Extensions;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
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
    public static void HandleShaderPipelineModeChanged(bool allowShaderPipelines)
        => GLRenderProgram.CancelPendingAsyncProgramsForShaderPipelineModeChange(allowShaderPipelines);

    private void LogShaderProgramLifecycleSummaryForShutdown()
    {
        if (Volatile.Read(ref _asyncShaderProgramShutdownDisposeRequested) != 0)
        {
            Debug.OpenGL("[ShaderProgramShutdown] Skipped shader program summary because async shader work was already abandoned during window shutdown.");
            return;
        }

        if (HasPendingShaderProgramShutdownWork(
            out int pendingBinaryUploads,
            out int pendingSourceLinks,
            out bool pendingAsyncPrograms))
        {
            _shutdownAbandonedAsyncShaderWork = true;
            Debug.OpenGL(
                $"[ShaderProgramShutdown] Skipped shader program summary because async shader work is still active; " +
                $"pendingSourceLinks={pendingSourceLinks} pendingBinaryUploads={pendingBinaryUploads} " +
                $"pendingAsyncPrograms={pendingAsyncPrograms}.");
            return;
        }

        ShaderProgramLifecycleDiagnostics.LogSummary(GetProgramBinaryUploadSummary());
    }

    /// <summary>
    /// Captures the backend-owned asynchronous program-binary upload counters.
    /// </summary>
    public ShaderProgramBinaryUploadSummary GetProgramBinaryUploadSummary()
        => new(
            _programBinaryUploadQueue?.CompletedCount ?? 0,
            _programBinaryUploadQueue?.FailedCount ?? 0,
            _programBinaryUploadQueue?.BackpressureCount ?? 0,
            _programBinaryUploadQueue?.CoalescedCount ?? 0);

    private void DisposeAsyncShaderProgramWorkForShutdown()
    {
        if (Interlocked.Exchange(ref _asyncShaderProgramShutdownDisposeRequested, 1) != 0)
            return;

        _programBinaryUploadQueue = null;
        _programCompileLinkQueue = null;

        TimeSpan joinTimeout = ShouldOrphanGLHandlesForShutdown
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(250);
        _programBinarySharedContext?.DisposeForShutdown(joinTimeout);
        if (_programCompileLinkSharedContexts is { Length: > 0 } compileLinkContexts)
        {
            for (int i = 0; i < compileLinkContexts.Length; i++)
                compileLinkContexts[i]?.DisposeForShutdown(joinTimeout);
        }
        _sharedContext?.DisposeForShutdown(joinTimeout);

        _programBinarySharedContext = null;
        _programCompileLinkSharedContexts = null;
        _sharedContext = null;
    }

    private bool HasPendingShaderProgramShutdownWork(
        out int pendingBinaryUploads,
        out int pendingSourceLinks,
        out bool pendingAsyncPrograms)
    {
        pendingBinaryUploads = _programBinaryUploadQueue?.InFlightCount ?? 0;
        pendingSourceLinks = _programCompileLinkQueue?.InFlightCount ?? 0;
        pendingAsyncPrograms = GLRenderProgram.HasPendingAsyncPrograms;
        return pendingBinaryUploads > 0 || pendingSourceLinks > 0 || pendingAsyncPrograms;
    }

    private GLSharedContext? _sharedContext;
    private GLSharedContext? _programBinarySharedContext;
    private GLSharedContext[]? _programCompileLinkSharedContexts;
    private GLProgramBinaryUploadQueue? _programBinaryUploadQueue;
    private GLProgramCompileLinkQueue? _programCompileLinkQueue;
    private bool _wantBinaryUploadSharedContext;
    private bool _wantCompileLinkSharedContext;
    private bool _wantSparseTextureSharedContext;
    private bool _binaryUploadSharedContextInitialized;
    private bool _compileLinkSharedContextInitialized;
    private bool _sparseTextureSharedContextInitialized;

    internal bool HasSharedContext => _sharedContext is { IsRunning: true }
                                   || _programBinarySharedContext is { IsRunning: true }
                                   || HasAnyRunningCompileLinkSharedContext;

    private bool HasAnyRunningCompileLinkSharedContext
    {
        get
        {
            if (_programCompileLinkSharedContexts is not { } contexts)
                return false;
            for (int i = 0; i < contexts.Length; i++)
                if (contexts[i] is { IsRunning: true })
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Gets the async program binary upload queue, or <c>null</c> if the shared context
    /// could not be created or async uploads are disabled.
    /// </summary>
    public GLProgramBinaryUploadQueue? ProgramBinaryUploadQueue => _programBinaryUploadQueue;

    /// <summary>
    /// Gets the async compile+link queue, or <c>null</c> if the shared context
    /// could not be created or async compilation is disabled.
    /// </summary>
    public GLProgramCompileLinkQueue? ProgramCompileLinkQueue => _programCompileLinkQueue;

    internal bool TryEnqueueSharedContextJob(Action<GL> job)
    {
        GLSharedContext? sharedContext = _sharedContext is { IsRunning: true }
            ? _sharedContext
            : _programBinarySharedContext is { IsRunning: true }
                ? _programBinarySharedContext
                : FirstRunningCompileLinkSharedContext();

        if (sharedContext is null)
        {
            EnsureSparseTextureSharedContextInitialized();
            sharedContext = _sharedContext is { IsRunning: true } ? _sharedContext : null;
        }

        if (sharedContext is null)
            return false;

        sharedContext.Enqueue(job);
        return true;
    }

    private GLSharedContext? FirstRunningCompileLinkSharedContext()
    {
        if (_programCompileLinkSharedContexts is not { } contexts)
            return null;
        for (int i = 0; i < contexts.Length; i++)
            if (contexts[i] is { IsRunning: true } running)
                return running;
        return null;
    }

    private static int ResolveProgramCompileLinkWorkerCount()
    {
        int configured = Math.Clamp(
            RuntimeEngine.Rendering.Settings.OpenGLProgramCompileLinkWorkerCount,
            1,
            16);

        if (configured <= 1)
            return 1;

        if (!XREnvironment.IsEnabled(
                XREngineEnvironmentVariables.DisableOpenGlCompileLinkWorkerPool))
            return configured;

        Debug.OpenGL(
            $"[ShaderCache] OpenGLProgramCompileLinkWorkerCount={configured} requested, " +
            $"but {XREngineEnvironmentVariables.DisableOpenGlCompileLinkWorkerPool}=1 requested the single-worker compatibility path. " +
            "Using 1 worker.");
        return 1;
    }

    /// <summary>
    /// Records desired shared GL context capabilities and defers hidden GLFW window
    /// creation until a shader cache miss, slow binary load, or async upload job
    /// actually requires a background worker context.
    /// </summary>
    private void InitAsyncProgramBinaryUpload()
    {
        _wantBinaryUploadSharedContext = RuntimeEngine.Rendering.Settings.AsyncProgramBinaryUpload
                                      && RuntimeEngine.Rendering.Settings.AllowBinaryProgramCaching;
        _wantCompileLinkSharedContext = WantsSharedContextProgramCompileLinkQueue;
        if (!_wantCompileLinkSharedContext)
            Debug.OpenGL("[ShaderCache] Shared-context compile/link queue disabled by settings. Shader source cache misses will stay pending; render-thread synchronous source linking is disabled.");
        _wantSparseTextureSharedContext = GetSparseTextureStreamingSupport(ESizedInternalFormat.Rgba8).IsAvailable;
    }

    internal void EnsureProgramBinaryUploadQueueInitialized()
    {
        if (_binaryUploadSharedContextInitialized ||
            !_wantBinaryUploadSharedContext ||
            Volatile.Read(ref _asyncShaderProgramShutdownDisposeRequested) != 0 ||
            Environment.CurrentManagedThreadId != XRWindow.NativeWindowThreadId)
        {
            return;
        }

        _binaryUploadSharedContextInitialized = true;
        var binaryContext = new GLSharedContext("XR Program Binary Upload");
        if (binaryContext.Initialize(XRWindow))
        {
            _programBinarySharedContext = binaryContext;
            _programBinaryUploadQueue = new GLProgramBinaryUploadQueue(binaryContext);
            Debug.OpenGL("[ShaderCache] Async program binary upload enabled via shared GL context.");
        }
        else
        {
            Debug.OpenGLWarning("[ShaderCache] Failed to create binary-upload shared context. Cached program binaries will load on the render thread.");
            binaryContext.Dispose();
        }
    }

    internal void EnsureProgramCompileLinkQueueInitialized()
    {
        if (_compileLinkSharedContextInitialized ||
            !_wantCompileLinkSharedContext ||
            Volatile.Read(ref _asyncShaderProgramShutdownDisposeRequested) != 0 ||
            Environment.CurrentManagedThreadId != XRWindow.NativeWindowThreadId)
        {
            return;
        }

        _compileLinkSharedContextInitialized = true;

        // Cold link of large uber shaders (e.g. imported model fragment
        // programs near 400 KB) can legitimately take 60 - 120 seconds on
        // the first run before the per-program binary cache is populated.
        // Use a generous unhealthy threshold so the worker is not flagged
        // as unhealthy while it is making forward progress on a real
        // (slow) GL_COMPLETION_STATUS_ARB poll loop. If we let the
        // default 30 s threshold trip, IsRunning flips false mid-link
        // and the selector falls back to the synchronous render-thread
        // path for any new hazardous program, which then stalls the
        // render thread on glMaxShaderCompilerThreadsARB / glCreateShader
        // for the remainder of the cold link. See repo memory note
        // opengl-shared-context-worker-unhealthy-threshold.
        const double CompileLinkUnhealthySeconds = 600.0;

        int requestedWorkers = ResolveProgramCompileLinkWorkerCount();

        var compileContexts = new List<GLSharedContext>(requestedWorkers);
        var compileContextNames = new List<string>(requestedWorkers);
        for (int i = 0; i < requestedWorkers; i++)
        {
            string threadName = requestedWorkers == 1
                ? "XR Program Source Compile"
                : $"XR Program Source Compile {i + 1}/{requestedWorkers}";
            var compileContext = new GLSharedContext(threadName, CompileLinkUnhealthySeconds);
            if (compileContext.Initialize(XRWindow))
            {
                compileContexts.Add(compileContext);
                compileContextNames.Add(threadName);
            }
            else
            {
                Debug.OpenGLWarning($"[ShaderCache] Failed to create compile/link shared context #{i + 1}/{requestedWorkers}; will continue with {compileContexts.Count} worker(s).");
                compileContext.Dispose();
            }
        }

        for (int i = 0; i < compileContexts.Count; i++)
        {
            EnableParallelShaderCompileOnSharedContextWorker(compileContexts[i], compileContextNames[i]);
        }

        if (compileContexts.Count > 0)
        {
            _programCompileLinkSharedContexts = compileContexts.ToArray();
            _programCompileLinkQueue = new GLProgramCompileLinkQueue(
                _programCompileLinkSharedContexts,
                completionStatusPollingEnabled: _parallelShaderCompileSupported);
            Debug.OpenGL(
                $"[ShaderCache] Async program compile+link enabled via {_programCompileLinkSharedContexts.Length} shared GL context worker(s); " +
                $"completionPolling={_parallelShaderCompileSupported}.");
        }
        else
        {
            Debug.OpenGLWarning("[ShaderCache] Failed to create any compile/link shared context. Shader source cache misses will remain pending because render-thread synchronous source linking is disabled.");
        }
    }

    private void EnsureSparseTextureSharedContextInitialized()
    {
        if (_sparseTextureSharedContextInitialized ||
            !_wantSparseTextureSharedContext ||
            Volatile.Read(ref _asyncShaderProgramShutdownDisposeRequested) != 0 ||
            Environment.CurrentManagedThreadId != XRWindow.NativeWindowThreadId)
        {
            return;
        }

        _sparseTextureSharedContextInitialized = true;
        var generalContext = new GLSharedContext("XR GL Shared Uploads");
        if (generalContext.Initialize(XRWindow))
        {
            _sharedContext = generalContext;
            Debug.OpenGL("Sparse texture streaming shared GL context enabled for async texture uploads.");
        }
        else
        {
            Debug.OpenGLWarning("Failed to create sparse texture shared context. Sparse texture uploads will fall back to render-thread work.");
            generalContext.Dispose();
        }
    }

}

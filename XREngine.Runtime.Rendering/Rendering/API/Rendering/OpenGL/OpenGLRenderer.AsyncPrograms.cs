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

        ShaderProgramLifecycleDiagnostics.LogSummary(_programBinaryUploadQueue);
    }

    private void DisposeAsyncShaderProgramWorkForShutdown()
    {
        if (Interlocked.Exchange(ref _asyncShaderProgramShutdownDisposeRequested, 1) != 0)
            return;

        _programBinaryUploadQueue = null;
        _programCompileLinkQueue = null;

        _programBinarySharedContext?.DisposeForShutdown();
        if (_programCompileLinkSharedContexts is { Length: > 0 } compileLinkContexts)
        {
            for (int i = 0; i < compileLinkContexts.Length; i++)
                compileLinkContexts[i]?.DisposeForShutdown();
        }
        _sharedContext?.DisposeForShutdown();

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
            Engine.Rendering.Settings.OpenGLProgramCompileLinkWorkerCount,
            1,
            16);

        if (configured <= 1)
            return 1;

        if (string.Equals(
            Environment.GetEnvironmentVariable("XRE_ENABLE_OPENGL_COMPILE_LINK_WORKER_POOL"),
            "1",
            StringComparison.Ordinal))
        {
            return configured;
        }

        Debug.OpenGL(
            $"[ShaderCache] OpenGLProgramCompileLinkWorkerCount={configured} requested, " +
            "but compile/link worker pools are disabled by default for OpenGL driver startup stability. " +
            "Using 1 worker; set XRE_ENABLE_OPENGL_COMPILE_LINK_WORKER_POOL=1 to opt in.");
        return 1;
    }

    /// <summary>
    /// Creates a shared GL context and the async queues for program binary upload
    /// and compile+link. Must be called from the main render thread after the
    /// primary context is fully initialized.
    /// </summary>
    private void InitAsyncProgramBinaryUpload()
    {
        bool wantBinaryUpload = Engine.Rendering.Settings.AsyncProgramBinaryUpload
                             && Engine.Rendering.Settings.AllowBinaryProgramCaching;
        bool wantCompileLink = WantsSharedContextProgramCompileLinkQueue;
        if (!wantCompileLink)
            Debug.OpenGL("[ShaderCache] Shared-context compile/link queue disabled by settings. Shader source cache misses will stay pending; render-thread synchronous source linking is disabled.");
        bool wantSparseTextureUploads = GetSparseTextureStreamingSupport(ESizedInternalFormat.Rgba8).IsAvailable;

        if (!wantBinaryUpload && !wantCompileLink && !wantSparseTextureUploads)
            return;

        if (wantBinaryUpload)
        {
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

        if (wantCompileLink)
        {
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
            for (int i = 0; i < requestedWorkers; i++)
            {
                string threadName = requestedWorkers == 1
                    ? "XR Program Source Compile"
                    : $"XR Program Source Compile {i + 1}/{requestedWorkers}";
                var compileContext = new GLSharedContext(threadName, CompileLinkUnhealthySeconds);
                if (compileContext.Initialize(XRWindow))
                {
                    compileContexts.Add(compileContext);
                    // KHR_parallel_shader_compile is per-context. Without enabling
                    // it on each worker, the driver falls back to serial compile/
                    // link on the worker context, causing cold links of large
                    // uber fragment shaders to take 60-120+ seconds per program.
                    EnableParallelShaderCompileOnSharedContextWorker(compileContext, threadName);
                }
                else
                {
                    Debug.OpenGLWarning($"[ShaderCache] Failed to create compile/link shared context #{i + 1}/{requestedWorkers}; will continue with {compileContexts.Count} worker(s).");
                    compileContext.Dispose();
                    // If at least one worker started, keep going with what we have.
                    // If none have started yet, keep trying — early failures may be
                    // transient. After the loop, we still create the queue from
                    // whatever workers we got (or skip queue creation if zero).
                }
            }

            if (compileContexts.Count > 0)
            {
                _programCompileLinkSharedContexts = compileContexts.ToArray();
                _programCompileLinkQueue = new GLProgramCompileLinkQueue(_programCompileLinkSharedContexts);
                Debug.OpenGL($"[ShaderCache] Async program compile+link enabled via {_programCompileLinkSharedContexts.Length} shared GL context worker(s).");
            }
            else
            {
                Debug.OpenGLWarning("[ShaderCache] Failed to create any compile/link shared context. Shader source cache misses will remain pending because render-thread synchronous source linking is disabled.");
            }
        }

        if (wantSparseTextureUploads)
        {
            var generalContext = new GLSharedContext("XR GL Shared Uploads");
            if (generalContext.Initialize(XRWindow))
            {
                _sharedContext = generalContext;
                if (wantSparseTextureUploads)
                    Debug.OpenGL("Sparse texture streaming shared GL context enabled for async texture uploads.");
            }
            else
            {
                Debug.OpenGLWarning("Failed to create sparse texture shared context. Sparse texture uploads will fall back to render-thread work.");
                generalContext.Dispose();
            }
        }
    }

}

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
using System.Diagnostics;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : AbstractRenderer<GL>
{
    public GL RawGL => Api; // public accessor for underlying GL instance
    private bool _shutdownAbandonedAsyncShaderWork;
    private int _asyncShaderProgramShutdownDisposeRequested;
    public override bool ShouldSkipNativeWindowDisposeForShutdown => _shutdownAbandonedAsyncShaderWork;
    internal bool ShouldOrphanGLHandlesForShutdown => _shutdownAbandonedAsyncShaderWork;

    public OvrMultiview? OVRMultiView { get; }
    public Silk.NET.OpenGL.Extensions.NV.NVMeshShader? NVMeshShader { get; }
    public Silk.NET.OpenGL.Extensions.NV.NVGpuShader5? NVGpuShader5 { get; }
    public Silk.NET.OpenGL.Extensions.NV.NVPathRendering? NVPathRendering { get; }
    public Silk.NET.OpenGLES.GL ESApi { get; }
    public NVViewportArray? NVViewportArray { get; }
    public ExtMemoryObject? EXTMemoryObject { get; }
    public ExtSemaphore? EXTSemaphore { get; }
    public ExtMemoryObjectWin32? EXTMemoryObjectWin32 { get; }

    public ExtSemaphoreWin32? EXTSemaphoreWin32 { get; }
    public ExtSemaphoreFd? EXTSemaphoreFd { get; }
    public ExtMemoryObjectFd? EXTMemoryObjectFd { get; }
    public NVBindlessMultiDrawIndirectCount? NVBindlessMultiDrawIndirectCount { get; }
    public ArbMultiDrawIndirect? ArbMultiDrawIndirect { get; }
    public ArbParallelShaderCompile? ARBParallelShaderCompile { get; }
    public ArbBindlessTexture? ARBBindlessTexture { get; }

    private static string? _version = null;
    public string? Version
    {
        get
        {
            if (_version is not null)
                return _version;

            // GL calls require the correct context + thread. During import/externalization we may
            // be traversing object graphs from job threads; never query GL there.
            if (!RuntimeEngine.IsRenderThread)
                return null;

            unsafe
            {
                _version ??= new((sbyte*)Api.GetString(StringName.Version));
            }
            return _version;
        }
    }
    public OpenGLRenderer(XRWindow window, bool shouldLinkWindow = true) : base(window, shouldLinkWindow)
    {
        ESApi = Silk.NET.OpenGLES.GL.GetApi(Window.GLContext);

        EXTMemoryObject = ESApi.TryGetExtension<ExtMemoryObject>(out var ext) ? ext : null;
        EXTSemaphore = ESApi.TryGetExtension<ExtSemaphore>(out var ext2) ? ext2 : null;
        EXTMemoryObjectWin32 = ESApi.TryGetExtension<ExtMemoryObjectWin32>(out var ext3) ? ext3 : null;
        EXTSemaphoreWin32 = ESApi.TryGetExtension<ExtSemaphoreWin32>(out var ext4) ? ext4 : null;
        EXTMemoryObjectFd = ESApi.TryGetExtension<ExtMemoryObjectFd>(out var ext5) ? ext5 : null;
        EXTSemaphoreFd = ESApi.TryGetExtension<ExtSemaphoreFd>(out var ext6) ? ext6 : null;

        var api = Api;

        OVRMultiView = api.TryGetExtension(out OvrMultiview ext7) ? ext7 : null;
        RuntimeEngine.Rendering.State.HasOvrMultiViewExtension = OVRMultiView is not null;
        RuntimeEngine.Rendering.State.HasVulkanMultiView = false;
        NVMeshShader = api.TryGetExtension(out Silk.NET.OpenGL.Extensions.NV.NVMeshShader ext8) ? ext8 : null;
        NVGpuShader5 = api.TryGetExtension(out Silk.NET.OpenGL.Extensions.NV.NVGpuShader5 ext9) ? ext9 : null;
        NVViewportArray = ESApi.TryGetExtension(out NVViewportArray ext10) ? ext10 : null;

        NVBindlessMultiDrawIndirectCount = api.TryGetExtension<NVBindlessMultiDrawIndirectCount>(out var ext11) ? ext11 : null;
        ArbMultiDrawIndirect = api.TryGetExtension<ArbMultiDrawIndirect>(out var ext12) ? ext12 : null;
        NVPathRendering = api.TryGetExtension(out Silk.NET.OpenGL.Extensions.NV.NVPathRendering ext13) ? ext13 : null;
        ARBParallelShaderCompile = api.TryGetExtension<ArbParallelShaderCompile>(out var ext14) ? ext14 : null;
        ARBBindlessTexture = api.TryGetExtension<ArbBindlessTexture>(out var ext15) ? ext15 : null;
    }

    protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject)
        => renderObject switch
        {
            //Materials
            XRMaterial data => new GLMaterial(this, data),
            XRShader s => new GLShader(this, s),

            //Meshes
            //"BaseVersion" here is the base class for different mesh renderers necessary for different render paths (like VR or not).
            XRMeshRenderer.BaseVersion data => new GLMeshRenderer(this, data),

            //Programs
            XRRenderProgramPipeline data => new GLRenderProgramPipeline(this, data),
            XRRenderProgram data => new GLRenderProgram(this, data),

            //Buffers
            XRDataBuffer data => new GLDataBuffer(this, data),
            XRDataBufferView data => new GLDataBufferView(this, data),

            //Render Targets
            XRRenderBuffer data => new GLRenderBuffer(this, data),
            XRFrameBuffer data => new GLFrameBuffer(this, data),

            //Texture 1D
            XRTexture1D data => new GLTexture1D(this, data),
            XRTexture1DArray data => new GLTexture1DArray(this, data),
            XRTextureViewBase data => new GLTextureView(this, data),

            //Texture 2D
            XRTexture2D data => new GLTexture2D(this, data),
            XRTexture2DArray data => new GLTexture2DArray(this, data),
            XRTextureRectangle data => new GLTextureRectangle(this, data),

            //Texture 3D
            XRTexture3D data => new GLTexture3D(this, data),

            //Texture Cube
            XRTextureCube data => new GLTextureCube(this, data),
            XRTextureCubeArray data => new GLTextureCubeArray(this, data),

            //Texture Buffer
            XRTextureBuffer data => new GLTextureBuffer(this, data),

            //Samplers
            XRSampler s => new GLSampler(this, s),

            //Feedback
            XRRenderQuery data => new GLRenderQuery(this, data),
            XRTransformFeedback data => new GLTransformFeedback(this, data),

            _ => throw new InvalidOperationException($"Render object type {renderObject.GetType()} is not supported.")
        };

    protected override GL GetAPI()
    {
        var api = GL.GetApi(Window.GLContext);
        InitGL(api);
        return api;
    }

    public override void Initialize()
    {

    }

    public override void CleanUp()
    {
        bool orphanGLHandles = ShouldOrphanGLHandlesForShutdown;

        if (!orphanGLHandles)
            _imguiMultiViewportController?.Dispose();
        _imguiMultiViewportController = null;

        if (_imguiController is { } controller)
        {
            ImGuiControllerUtilities.DetachInputHandlers(controller);
            ImGuiControllerUtilities.MarkContextDestroyed(controller.Context);
            ImGuiContextTracker.Unregister(controller.Context);
            if (!orphanGLHandles)
                controller.Dispose();
        }
        _imguiController = null;
        _imguiBackend = null;
        _imguiFontValidationCountdown = 0;
        ResetImGuiFrameMarker();

        // Clean up shared contexts and async queues.
        LogShaderProgramLifecycleSummaryForShutdown();
        DisposeAsyncShaderProgramWorkForShutdown();

        CancelPendingFrontLuminanceReadback();
        DisposeGpuRenderStatsReadbacks();

        // Clean up cached luminance front resources
        if (!ShouldOrphanGLHandlesForShutdown)
        {
            if (_luminanceFrontTex != 0)
                Api.DeleteTexture(_luminanceFrontTex);
            if (_luminanceFrontFbo != 0)
                Api.DeleteFramebuffer(_luminanceFrontFbo);
            if (_luminanceFrontPbo != 0)
                Api.DeleteBuffer(_luminanceFrontPbo);
        }
        _luminanceFrontTex = 0;
        _luminanceFrontFbo = 0;
        _luminanceFrontPbo = 0;
        _luminanceFrontPboSize = 0;
        _luminanceFrontTexWidth = 0;
        _luminanceFrontTexHeight = 0;
        _luminanceFrontMipLevels = 0;

        // Clean up compute shader resources
        if (_luminanceResultBuffer != 0 && !ShouldOrphanGLHandlesForShutdown)
            Api.DeleteBuffer(_luminanceResultBuffer);
        _luminanceResultBuffer = 0;
        _luminanceResultBufferSize = 0;
        _luminanceComputeProgram?.Destroy();
        _luminanceComputeProgram = null;
        _luminanceComputeInitialized = false;
    }

    /// <summary>
    /// Per-renderer frame counter for caching purposes.
    /// Incremented each frame during ProcessPendingUploads.
    /// </summary>
    internal long _frameCounter;

    /// <summary>
    /// Set by the debug callback when the NVIDIA driver reports GL_OUT_OF_MEMORY.
    /// Consumed (cleared) at the start of each frame by <see cref="ProcessPendingUploads"/>.
    /// Do NOT use this to suppress draw calls � use <see cref="SuppressDrawsForOomRecovery"/> instead.
    /// </summary>
    internal volatile bool _oomDetectedThisFrame;

    /// <summary>
    /// True while the renderer is inside an OOM cooldown period.
    /// Suppresses draw calls and GPU allocations until the cooldown expires.
    /// Kept separate from <see cref="_oomDetectedThisFrame"/> to avoid the cooldown
    /// self-restarting by reading back its own suppression flag.
    /// </summary>
    internal bool SuppressDrawsForOomRecovery;

    /// <summary>
    /// Remaining frames of OOM cooldown. While positive, all GPU allocations (buffer uploads,
    /// mesh generation, draw calls) are suppressed to let the driver recover memory.
    /// </summary>
    private int _oomCooldownFrames;

    /// <summary>
    /// Number of frames to suppress GPU allocations after an OOM event.
    /// Gives the driver time to reclaim memory from destroyed objects.
    /// </summary>
    private const int OomCooldownDuration = 10;

    /// <summary>
    /// Processes pending async buffer uploads and mesh generations within the frame time budget.
    /// Skips all GPU allocations during OOM cooldown to let the driver recover.
    /// </summary>
    public override void ProcessPendingUploads()
    {
        _frameCounter++;

        long _tPoll = Stopwatch.GetTimestamp();
        GLRenderProgram.PollPendingAsyncPrograms(RuntimeEngine.Rendering.Settings.MaxAsyncShaderProgramsPerFrame);
        UploadStageStats.Record("PollPendingAsyncPrograms", ElapsedMs(_tPoll), 0, 0);

        // Snapshot and clear the volatile flag set by the debug callback.
        // This must happen before the cooldown check so the cooldown's own
        // draw-suppression flag cannot self-restart the timer.
        bool driverReportedOom = _oomDetectedThisFrame;
        _oomDetectedThisFrame = false;

        // A fresh OOM from the driver starts (or extends) the cooldown.
        if (driverReportedOom)
        {
            if (_oomCooldownFrames == 0)
                Debug.OpenGLWarning($"[OOM Recovery] Entering {OomCooldownDuration}-frame cooldown � suspending GPU allocations.");
            _oomCooldownFrames = OomCooldownDuration;
        }

        if (_oomCooldownFrames > 0)
        {
            _oomCooldownFrames--;
            SuppressDrawsForOomRecovery = true;
            UploadStageStats.Record("OomCooldownSkip", 0.0, 0, 0);
            return;
        }

        SuppressDrawsForOomRecovery = false;

        long _tUpload = Stopwatch.GetTimestamp();
        UploadQueue.ProcessUploads();
        int uploadPendingAfter = UploadQueue.PendingCount;
        UploadStageStats.Record(
            "UploadQueue.ProcessUploads",
            ElapsedMs(_tUpload),
            UploadQueue.LastDequeuedItems,
            uploadPendingAfter);

        int meshPendingBefore = MeshGenerationQueue.PendingCount;
        long _tMesh = Stopwatch.GetTimestamp();
        MeshGenerationQueue.ProcessGeneration();
        int meshPendingAfter = MeshGenerationQueue.PendingCount;
        UploadStageStats.Record(
            "MeshGenerationQueue.ProcessGeneration",
            ElapsedMs(_tMesh),
            Math.Max(0, meshPendingBefore - meshPendingAfter),
            meshPendingAfter);
    }

    private static double ElapsedMs(long startTicks)
        => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Upload-pipeline per-stage timing aggregator (env-gated: XRE_UPLOAD_STAGE_LOGGING=1, or
    /// hard-on while debugging). Bypasses the engine profiler because nested BeginTiming scopes
    /// only surface as drop-log HotPath leaves, hiding cumulative cost. Flushes a one-line
    /// summary once per second to Build/Logs/upload-stage-stats.log.
    /// </summary>
    private static class UploadStageStats
    {
        private static readonly bool Enabled =
            string.Equals(Environment.GetEnvironmentVariable("XRE_UPLOAD_STAGE_LOGGING"), "1", StringComparison.OrdinalIgnoreCase)
            || Debugger.IsAttached;

        private readonly struct Entry
        {
            public Entry(long calls, double totalMs, double maxMs, long processed, long pendingMax)
            {
                Calls = calls; TotalMs = totalMs; MaxMs = maxMs; Processed = processed; PendingMax = pendingMax;
            }
            public long Calls { get; }
            public double TotalMs { get; }
            public double MaxMs { get; }
            public long Processed { get; }
            public long PendingMax { get; }
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<string, Entry> _stats = new();
        private static long _lastFlushTicks;
        private static string? _logPath;

        public static bool IsEnabled()
            => Enabled;

        public static void Record(string stage, double ms, int processedItems, int pendingAfter)
        {
            if (!IsEnabled())
                return;
            lock (_lock)
            {
                if (_stats.TryGetValue(stage, out var e))
                {
                    _stats[stage] = new Entry(
                        e.Calls + 1,
                        e.TotalMs + ms,
                        ms > e.MaxMs ? ms : e.MaxMs,
                        e.Processed + processedItems,
                        pendingAfter > e.PendingMax ? pendingAfter : e.PendingMax);
                }
                else
                {
                    _stats[stage] = new Entry(1, ms, ms, processedItems, pendingAfter);
                }

                long now = Stopwatch.GetTimestamp();
                if (_lastFlushTicks == 0)
                    _lastFlushTicks = now;
                double sinceFlushSec = (now - _lastFlushTicks) / (double)Stopwatch.Frequency;
                if (sinceFlushSec >= 1.0)
                    FlushLocked(sinceFlushSec, now);
            }
        }

        private static void FlushLocked(double windowSec, long nowTicks)
        {
            _lastFlushTicks = nowTicks;
            if (_stats.Count == 0)
                return;
            _logPath ??= System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Build", "Logs", "upload-stage-stats.log");
            try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_logPath)!); } catch { }
            var sb = new StringBuilder(256);
            sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] window=")
              .Append(windowSec.ToString("F2")).Append('s');
            foreach (var kv in _stats)
            {
                double avg = kv.Value.TotalMs / Math.Max(1, kv.Value.Calls);
                sb.Append(' ').Append(kv.Key).Append('=')
                  .Append(kv.Value.Calls).Append("c,")
                  .Append(kv.Value.TotalMs.ToString("F2")).Append("ms,avg=")
                  .Append(avg.ToString("F3")).Append(",max=")
                  .Append(kv.Value.MaxMs.ToString("F3"))
                  .Append(",done=").Append(kv.Value.Processed)
                  .Append(",pendMax=").Append(kv.Value.PendingMax);
            }
            sb.AppendLine();
            try { System.IO.File.AppendAllText(_logPath, sb.ToString()); } catch { }
            _stats.Clear();
        }
    }

    protected override void WindowRenderCallback(double delta)
    {

    }
}

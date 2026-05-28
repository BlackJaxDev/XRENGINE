namespace XREngine.Rendering;

/// <summary>
/// Central, runtime-toggleable home for the engine's diagnostic on/off flags.
///
/// These flags were previously read directly from <c>XRE_*</c> environment variables at
/// type-init time in each consumer, which forced a process restart to enable/disable a
/// trace and made them invisible to the editor. They are now exposed here as
/// <c>volatile bool</c> fields that any consumer reads on each use, and the editor mirrors
/// each flag as a <see cref="XREngine.Editor.EditorDebugOptions"/> property under the
/// "Diagnostics" category. Env-var activation is preserved as an initial seed for
/// non-editor scenarios (servers, headless tools, CI runs).
///
/// All flags here are <c>volatile</c>; per-call reads are a single cheap memory load.
/// Toggling a flag at runtime takes effect on the next read; consumers that lazily open
/// log files etc. will still do so on the first <c>true</c> observation, and a subsequent
/// <c>false</c> simply causes them to short-circuit — log files are not closed.
///
/// To add a new flag: declare a <c>volatile bool</c> field, seed it from the matching env
/// var in <see cref="SeedFromEnvironment"/>, expose a <c>Set</c> method, and add a
/// corresponding property on <c>EditorDebugOptions</c> that calls the <c>Set</c> method.
/// </summary>
public static class RenderDiagnosticsFlags
{
    /// <summary>BVH/HiZ overflow tracing. Seed: <c>XRE_HIZ_CULL_TRACE=1</c>.</summary>
    public static volatile bool HiZCullTrace;

    /// <summary>Vendor upscale resolve/blit diagnostic logging. Seed: <c>XRE_DIAG_VENDOR_UPSCALE=1</c>.</summary>
    public static volatile bool DiagVendorUpscale;

    /// <summary>Quad-blit pass diagnostic logging. Seed: <c>XRE_DIAG_QUAD_BLIT=1</c>.</summary>
    public static volatile bool DiagQuadBlit;

    /// <summary>Clear the default framebuffer to magenta to confirm present-path binding. Seed: <c>XRE_DEBUG_PRESENT_CLEAR=1</c>.</summary>
    public static volatile bool DebugPresentClear;

    /// <summary>Per-buffer PushSubData 1Hz aggregate dump to log_rendering.txt. Seed: <c>XRE_PUSHSUBDATA_BREAKDOWN=1</c>.</summary>
    public static volatile bool PushSubDataBreakdown;

    /// <summary>Per-call PushSubData trace to <c>Build/Logs/pushsubdata-trace.log</c>. Seed: <c>XRE_PUSHSUBDATA_TRACE=1</c>.</summary>
    public static volatile bool PushSubDataTrace;

    /// <summary>Per-dispatch compute shader trace to <c>Build/Logs/dispatch-trace.log</c>. Seed: <c>XRE_DISPATCH_TRACE=1</c>.</summary>
    public static volatile bool DispatchTrace;

    /// <summary>Issue <c>glFinish</c> after each compute dispatch to pinpoint TDRs. Seed: <c>XRE_DISPATCH_FINISH=1</c>.</summary>
    public static volatile bool DispatchFinish;

    /// <summary>1Hz upload-pipeline per-stage timing dump to <c>Build/Logs/upload-stage-stats.log</c>. Seed: <c>XRE_UPLOAD_STAGE_LOGGING=1</c> or debugger attached.</summary>
    public static volatile bool UploadStageLogging;

    /// <summary>Synchronous breadcrumb writes (+ <c>glFinish</c>) around suspect GL calls. Seed: <c>XRE_CRASH_BREADCRUMBS=1</c>.</summary>
    public static volatile bool CrashBreadcrumbs;

    /// <summary>
    /// Deferred-lighting debug visualization mode for newly-created <c>DefaultRenderPipeline</c>
    /// instances. Values: 0=Disabled, 1=RawAlbedo, 2=DirectLighting, 3=Rmse, 4=Normal, 5=Depth
    /// (see <c>DeferredLightCombine.fs</c>). Existing pipelines retain their per-instance setting;
    /// change takes effect on the next pipeline construction (e.g. swapping cameras / scenes).
    /// Seed: <c>XRE_DEFERRED_DEBUG=&lt;0..5&gt;</c>.
    /// </summary>
    public static volatile int DeferredDebugView;

    /// <summary>
    /// When false, <c>ModelRenderDiagnostics</c> short-circuits all trace work. Default is false;
    /// seed <c>XRE_DEBUG_MODEL_RENDER=1</c> or <c>XRE_MODEL_RENDER_DIAG=1</c> to enable.
    /// </summary>
    public static volatile bool ModelRenderDiagEnabled;

    /// <summary>
    /// Directional shadow atlas/cascade audit tracing. Default is false; seed
    /// <c>XRE_DIRECTIONAL_SHADOW_AUDIT=1</c> or <c>XRE_SHADOW_AUDIT=1</c> to enable.
    /// </summary>
    public static volatile bool DirectionalShadowAudit;

    /// <summary>
    /// Bypasses vendor upscaler resolve/blit and routes the final present from the raw scene FBO.
    /// Read on each viewport-target-command construction; toggling at runtime affects subsequent
    /// pipeline (re)builds. Seed: <c>XRE_BYPASS_VENDOR_UPSCALE=1</c>.
    /// </summary>
    public static volatile bool BypassVendorUpscale;

    /// <summary>
    /// Master "GL debug" toggle. Requests a debug GL context at startup, enables indirect-draw
    /// parameter dumps before each <c>glMultiDrawElementsIndirect[Count]</c>, and enables FBO
    /// attach/detach trace logging. Heavy &mdash; diagnostic only. Seed: <c>XRE_GL_DEBUG=1</c>.
    /// </summary>
    public static volatile bool GLDebug;

    /// <summary>
    /// Forces viewport rendering to cover the entire window, ignoring scene-panel-driven sub-rects.
    /// Read per-frame from <c>RuntimeRenderingHostServices.ForceFullViewport</c>. Seed:
    /// <c>XRE_FORCE_FULL_VIEWPORT=1</c>.
    /// </summary>
    public static volatile bool ForceFullViewport;

    /// <summary>
    /// When true, the engine substitutes a minimal <c>DebugOpaqueRenderPipeline</c> for the
    /// default pipeline (forward-only opaque, no post). Used to isolate pipeline-stage faults.
    /// Seed: <c>XRE_FORCE_DEBUG_OPAQUE_PIPELINE=1</c>.
    /// </summary>
    public static volatile bool ForceDebugOpaquePipeline;

    /// <summary>
    /// HiZ dirty-rect bypass for GPU culling. Default <b>on</b>; setting to false routes GPU
    /// culling through the legacy dirty-range path. Seed: <c>XRE_GPU_HIZ_DIRTY_BYPASS=0</c> or
    /// <c>=false</c> disables (any other value or unset keeps it on).
    /// </summary>
    public static volatile bool GpuHiZDirtyBypass = true;

    /// <summary>
    /// Optional override naming an internal FBO to use as the final present source (debug only).
    /// Empty/null means the pipeline picks the source by its normal rules. Seed:
    /// <c>XRE_OUTPUT_SOURCE_FBO=&lt;name&gt;</c>.
    /// </summary>
    public static volatile string? OutputSourceFboOverride;

    /// <summary>
    /// Vulkan auto-uniform-rewrite pass. Default <b>true</b>. When false, the rewriter still runs
    /// but skips the early SPIR-V opaque-uniform rewrite (legacy path). Seed:
    /// <c>XRE_VK_ENABLE_AUTO_UNIFORM_REWRITE=0</c> disables; any other value (or unset) keeps it on.
    /// </summary>
    public static volatile bool VkEnableAutoUniformRewrite = true;

    /// <summary>Append shader source preview to Vulkan shader compile error messages. Seed: <c>XRE_VK_DUMP_SHADER_ON_ERROR=1</c>.</summary>
    public static volatile bool VkDumpShaderOnError;

    /// <summary>Log every Vulkan graphics pipeline creation with full stage/format details. Seed: <c>XRE_VK_TRACE_PIPECREATE=1</c>.</summary>
    public static volatile bool VkTracePipeCreate;

    /// <summary>Verbose trace for swapchain (dynamic-rendering) draws only. Seed: <c>XRE_VK_TRACE_SWAPDRAW=1</c>.</summary>
    public static volatile bool VkTraceSwapDraw;

    /// <summary>Verbose trace for every Vulkan draw call (including FBO-targeted UI batches). Seed: <c>XRE_VK_TRACE_DRAW=1</c>.</summary>
    public static volatile bool VkTraceDraw;

    /// <summary>Skip UI-pipeline ops on the Vulkan command buffer to isolate UI from scene faults. Seed: <c>XRE_SKIP_UI_PIPELINE=1</c>.</summary>
    public static volatile bool VkSkipUiPipeline;

    /// <summary>Clear the Vulkan swapchain to magenta after main composition (sanity check). Seed: <c>XRE_FORCE_SWAPCHAIN_MAGENTA=1</c>.</summary>
    public static volatile bool VkForceSwapchainMagenta;

    /// <summary>Skip the Vulkan ImGui overlay draw entirely. Seed: <c>XRE_SKIP_IMGUI=1</c>.</summary>
    public static volatile bool VkSkipImGui;

    static RenderDiagnosticsFlags()
    {
        SeedFromEnvironment();
    }

    private static void SeedFromEnvironment()
    {
        HiZCullTrace = ReadBool("XRE_HIZ_CULL_TRACE");
        DiagVendorUpscale = ReadBool("XRE_DIAG_VENDOR_UPSCALE");
        DiagQuadBlit = ReadBool("XRE_DIAG_QUAD_BLIT");
        DebugPresentClear = ReadBool("XRE_DEBUG_PRESENT_CLEAR");
        PushSubDataBreakdown = ReadBool("XRE_PUSHSUBDATA_BREAKDOWN");
        PushSubDataTrace = ReadBool("XRE_PUSHSUBDATA_TRACE");
        DispatchTrace = ReadBool("XRE_DISPATCH_TRACE");
        DispatchFinish = ReadBool("XRE_DISPATCH_FINISH");
        UploadStageLogging = ReadBool("XRE_UPLOAD_STAGE_LOGGING") || System.Diagnostics.Debugger.IsAttached;
        CrashBreadcrumbs = ReadBool("XRE_CRASH_BREADCRUMBS");

        try
        {
            string? raw = Environment.GetEnvironmentVariable("XRE_DEFERRED_DEBUG");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int mode) && mode >= 0 && mode <= 5)
                DeferredDebugView = mode;
        }
        catch
        {
        }

        bool modelDiagEnabled = ReadBool("XRE_DEBUG_MODEL_RENDER") || ReadBool("XRE_MODEL_RENDER_DIAG");
        bool modelDiagDisabled = ReadBool("XRE_DEBUG_MODEL_RENDER_ZERO") || ReadBool("XRE_MODEL_RENDER_DIAG_ZERO");
        // The legacy env-var contract was "set to 0 to disable"; preserve that by checking for the
        // exact literal "0".
        try
        {
            string? a = Environment.GetEnvironmentVariable("XRE_DEBUG_MODEL_RENDER");
            string? b = Environment.GetEnvironmentVariable("XRE_MODEL_RENDER_DIAG");
            if (string.Equals(a, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(b, "1", StringComparison.OrdinalIgnoreCase))
                modelDiagEnabled = true;
            if (string.Equals(a, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(b, "0", StringComparison.OrdinalIgnoreCase))
                modelDiagDisabled = true;
        }
        catch
        {
        }
        ModelRenderDiagEnabled = modelDiagEnabled && !modelDiagDisabled;
        DirectionalShadowAudit = ReadBool("XRE_DIRECTIONAL_SHADOW_AUDIT") || ReadBool("XRE_SHADOW_AUDIT");

        BypassVendorUpscale = ReadBool("XRE_BYPASS_VENDOR_UPSCALE");
        GLDebug = ReadBool("XRE_GL_DEBUG");
        ForceFullViewport = ReadBool("XRE_FORCE_FULL_VIEWPORT");
        ForceDebugOpaquePipeline = ReadBool("XRE_FORCE_DEBUG_OPAQUE_PIPELINE");

        try
        {
            string? raw = Environment.GetEnvironmentVariable("XRE_GPU_HIZ_DIRTY_BYPASS");
            if (!string.IsNullOrEmpty(raw))
                GpuHiZDirtyBypass = !(raw == "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
        }

        try
        {
            string? raw = Environment.GetEnvironmentVariable("XRE_OUTPUT_SOURCE_FBO");
            if (!string.IsNullOrWhiteSpace(raw))
                OutputSourceFboOverride = raw.Trim();
        }
        catch
        {
        }

        // VK_ENABLE_AUTO_UNIFORM_REWRITE legacy contract: default ON, env="0" disables.
        try
        {
            string? raw = Environment.GetEnvironmentVariable("XRE_VK_ENABLE_AUTO_UNIFORM_REWRITE");
            if (string.Equals(raw, "0", StringComparison.Ordinal))
                VkEnableAutoUniformRewrite = false;
        }
        catch
        {
        }

        VkDumpShaderOnError = ReadBool("XRE_VK_DUMP_SHADER_ON_ERROR");
        VkTracePipeCreate = ReadBool("XRE_VK_TRACE_PIPECREATE");
        VkTraceSwapDraw = ReadBool("XRE_VK_TRACE_SWAPDRAW");
        VkTraceDraw = ReadBool("XRE_VK_TRACE_DRAW");
        VkSkipUiPipeline = ReadBool("XRE_SKIP_UI_PIPELINE");
        VkForceSwapchainMagenta = ReadBool("XRE_FORCE_SWAPCHAIN_MAGENTA");
        VkSkipImGui = ReadBool("XRE_SKIP_IMGUI");
    }

    private static bool ReadBool(string name)
    {
        try
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return false;
            return raw == "1"
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetHiZCullTrace(bool value) => HiZCullTrace = value;
    public static void SetDiagVendorUpscale(bool value) => DiagVendorUpscale = value;
    public static void SetDiagQuadBlit(bool value) => DiagQuadBlit = value;
    public static void SetDebugPresentClear(bool value) => DebugPresentClear = value;
    public static void SetPushSubDataBreakdown(bool value) => PushSubDataBreakdown = value;
    public static void SetPushSubDataTrace(bool value) => PushSubDataTrace = value;
    public static void SetDispatchTrace(bool value) => DispatchTrace = value;
    public static void SetDispatchFinish(bool value) => DispatchFinish = value;
    public static void SetUploadStageLogging(bool value) => UploadStageLogging = value;
    public static void SetCrashBreadcrumbs(bool value) => CrashBreadcrumbs = value;

    /// <summary>Set the default deferred debug view for newly-created pipelines (0..5).</summary>
    public static void SetDeferredDebugView(int value)
    {
        if (value < 0) value = 0;
        else if (value > 5) value = 5;
        DeferredDebugView = value;
    }

    public static void SetModelRenderDiagEnabled(bool value) => ModelRenderDiagEnabled = value;
    public static void SetDirectionalShadowAudit(bool value) => DirectionalShadowAudit = value;

    public static void SetBypassVendorUpscale(bool value) => BypassVendorUpscale = value;
    public static void SetGLDebug(bool value) => GLDebug = value;
    public static void SetForceFullViewport(bool value) => ForceFullViewport = value;
    public static void SetForceDebugOpaquePipeline(bool value) => ForceDebugOpaquePipeline = value;
    public static void SetGpuHiZDirtyBypass(bool value) => GpuHiZDirtyBypass = value;
    public static void SetOutputSourceFboOverride(string? value)
        => OutputSourceFboOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static void SetVkEnableAutoUniformRewrite(bool value) => VkEnableAutoUniformRewrite = value;
    public static void SetVkDumpShaderOnError(bool value) => VkDumpShaderOnError = value;
    public static void SetVkTracePipeCreate(bool value) => VkTracePipeCreate = value;
    public static void SetVkTraceSwapDraw(bool value) => VkTraceSwapDraw = value;
    public static void SetVkTraceDraw(bool value) => VkTraceDraw = value;
    public static void SetVkSkipUiPipeline(bool value) => VkSkipUiPipeline = value;
    public static void SetVkForceSwapchainMagenta(bool value) => VkForceSwapchainMagenta = value;
    public static void SetVkSkipImGui(bool value) => VkSkipImGui = value;
}

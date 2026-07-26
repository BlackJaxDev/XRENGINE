using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Editor.HotReload;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    [XRMcp(Name = "get_renderer_reload_status", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Caller)]
    [Description("Return active backend generation, reload state, counters, timings, and the last actionable error.")]
    public static Task<McpToolResponse> GetRendererReloadStatusAsync()
    {
        RendererHotReloadService service = RendererHotReloadService.Current;
        RendererReloadSnapshot snapshot = service.Snapshot;
        RendererBackendBuildResult? build = service.LastBuild;
        return Task.FromResult(new McpToolResponse(
            snapshot.Status,
            new
            {
                backend = snapshot.BackendId.Value,
                generation = snapshot.Generation,
                state = snapshot.State.ToString(),
                failure_kind = snapshot.FailureKind.ToString(),
                snapshot.Status,
                last_error = snapshot.LastError,
                active_build_hash = service.ActiveBuildHash,
                load_context = service.ActiveLoadContextName ?? "Default/static",
                successful_reloads = snapshot.SuccessfulReloads,
                failed_reloads = snapshot.FailedReloads,
                rollbacks = snapshot.LastGoodRollbacks,
                unload_leaks = snapshot.UnloadLeaks,
                phase_durations_ms = snapshot.PhaseDurations.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.TotalMilliseconds),
                managed_hot_reload_mechanism = RendererManagedHotReload.LastMechanism,
                shader_invalidations = ShaderHotReload.PublishedInvalidations,
                stale_shader_results_rejected = ShaderHotReload.StaleNotificationsRejected,
                last_build = build is null
                    ? null
                    : new
                    {
                        backend = build.BackendId.Value,
                        build.Generation,
                        build.Succeeded,
                        build.Cancelled,
                        duration_ms = build.Duration.TotalMilliseconds,
                        manifest_path = build.ManifestPath,
                        diagnostics = build.Diagnostics,
                    },
            }));
    }

    [XRMcp(Name = "reload_renderer_shaders", Permission = McpPermissionLevel.Mutate)]
    [McpThreadAffinity(McpThreadAffinity.Caller)]
    [Description("Invalidate all loaded shader dependency roots while retaining each backend's last-good programs and pipelines.")]
    public static Task<McpToolResponse> ReloadRendererShadersAsync()
    {
        int count = RendererHotReloadService.Current.ReloadShaders();
        return Task.FromResult(new McpToolResponse(
            $"Invalidated {count} loaded renderer shader source(s).",
            new { invalidated_shader_count = count }));
    }

    [XRMcp(
        Name = "restart_renderer",
        Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Quiesces, tears down, and recreates every window using the selected renderer backend.")]
    [McpThreadAffinity(McpThreadAffinity.Caller)]
    [Description("Transactionally restart the selected active backend generation. Active OpenXR requires restart_openxr_session=true; active OpenVR remains blocked.")]
    public static async Task<McpToolResponse> RestartRendererAsync(
        [McpName("backend"), Description("Renderer backend: opengl or vulkan.")] string backend,
        [McpName("restart_openxr_session"), Description("Stop and recreate active OpenXR presentation while preserving editor/world state.")] bool restartOpenXrSession = false,
        [McpName("first_frame_timeout_ms"), Description("Rollback timeout while awaiting the first valid frame.")] int firstFrameTimeoutMs = 15000,
        CancellationToken token = default)
    {
        if (!TryParseBackend(backend, out RendererBackendId backendId, out string? error))
            return new McpToolResponse(error!, isError: true);

        TimeSpan timeout = TimeSpan.FromMilliseconds(Math.Clamp(firstFrameTimeoutMs, 1000, 120000));
        RendererReplacementResult result = restartOpenXrSession
            ? await RendererHotReloadService.Current.RestartCurrentGenerationWithOpenXrSessionAsync(
                backendId,
                timeout,
                token)
            : await RendererHotReloadService.Current.RestartCurrentGenerationAsync(
                backendId,
                timeout,
                token);
        return ReplacementResponse(result);
    }

    [XRMcp(
        Name = "build_and_reload_renderer",
        Permission = McpPermissionLevel.Arbitrary,
        PermissionReason = "Invokes a targeted dotnet build, stages a collectible backend module, and transactionally replaces the active renderer.")]
    [McpThreadAffinity(McpThreadAffinity.Caller)]
    [Description("Build one backend leaf project and activate exactly one validated collectible generation.")]
    public static async Task<McpToolResponse> BuildAndReloadRendererAsync(
        [McpName("backend"), Description("Renderer backend: opengl or vulkan.")] string backend,
        [McpName("configuration"), Description("Build configuration, normally Debug.")] string configuration = "Debug",
        [McpName("first_frame_timeout_ms"), Description("Rollback timeout while awaiting the first valid frame.")] int firstFrameTimeoutMs = 15000,
        CancellationToken token = default)
    {
        if (!TryParseBackend(backend, out RendererBackendId backendId, out string? error))
            return new McpToolResponse(error!, isError: true);

        RendererReplacementResult result =
            await RendererHotReloadService.Current.BuildAndReloadAsync(
                backendId,
                configuration,
                TimeSpan.FromMilliseconds(Math.Clamp(firstFrameTimeoutMs, 1000, 120000)),
                token);
        return ReplacementResponse(result);
    }

    private static McpToolResponse ReplacementResponse(RendererReplacementResult result)
        => new(
            result.Succeeded
                ? $"Renderer {result.ActiveRegistration.Metadata.Id} generation {result.ActiveRegistration.Metadata.Generation} is active."
                : result.Error ?? "Renderer replacement failed.",
            new
            {
                result.Succeeded,
                backend = result.ActiveRegistration.Metadata.Id.Value,
                generation = result.ActiveRegistration.Metadata.Generation,
                failure_kind = result.FailureKind.ToString(),
                result.RolledBack,
                result.Error,
            },
            isError: !result.Succeeded);

    private static bool TryParseBackend(
        string value,
        out RendererBackendId backendId,
        out string? error)
    {
        if (string.Equals(value, "opengl", StringComparison.OrdinalIgnoreCase))
        {
            backendId = RendererBackendId.OpenGL;
            error = null;
            return true;
        }
        if (string.Equals(value, "vulkan", StringComparison.OrdinalIgnoreCase))
        {
            backendId = RendererBackendId.Vulkan;
            error = null;
            return true;
        }

        backendId = default;
        error = $"Unknown renderer backend '{value}'. Expected 'opengl' or 'vulkan'.";
        return false;
    }
}

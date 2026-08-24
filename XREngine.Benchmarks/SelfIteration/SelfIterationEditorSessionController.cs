using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Owns one named MCP editor session for reload validation and crash recovery.
/// </summary>
public sealed class SelfIterationEditorSessionController : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        new Dictionary<string, object?>();

    private readonly string _workspaceRoot;
    private readonly SelfIterationConfiguration _configuration;
    private readonly SelfIterationProcessRunner _processRunner;
    private readonly HttpClient _httpClient = new();
    private readonly string _sessionOwnerToken = Guid.NewGuid().ToString("N")[..12];
    private string? _sessionName;
    private SelfIterationScenario? _scenario;
    private int _commandIndex;

    public SelfIterationEditorSessionController(
        string workspaceRoot,
        SelfIterationConfiguration configuration,
        SelfIterationProcessRunner processRunner)
    {
        _workspaceRoot = workspaceRoot;
        _configuration = configuration;
        _processRunner = processRunner;
        _httpClient.Timeout = TimeSpan.FromSeconds(90);
    }

    public async Task PrepareAsync(
        SelfIterationScenario scenario,
        string evidenceDirectory,
        CancellationToken token)
    {
        if (!_configuration.Measurement.ValidateHotReload)
            return;
        await StopAsync(evidenceDirectory, token);
        _scenario = scenario;
        _sessionName = BuildSessionName(
            _configuration.CampaignId,
            scenario.Name,
            _sessionOwnerToken);
        await StartAsync(evidenceDirectory, token);
    }

    public async Task<SelfIterationReloadResult> ApplyAndValidateAsync(
        SelfIterationReloadMode requestedMode,
        IReadOnlyList<string> changedPaths,
        string evidenceDirectory,
        CancellationToken token)
    {
        if (!_configuration.Measurement.ValidateHotReload)
        {
            return new SelfIterationReloadResult
            {
                Succeeded = true,
                RequestedMode = requestedMode,
                EffectiveMode = SelfIterationReloadMode.EditorRestart,
                Details = "Live reload validation disabled; formal measurement rebuild will validate the candidate.",
            };
        }
        if (_scenario is null || string.IsNullOrWhiteSpace(_sessionName))
            throw new InvalidOperationException("Editor validation session was not prepared.");

        SelfIterationReloadMode effectiveMode = ResolveReloadMode(
            requestedMode,
            changedPaths,
            _scenario.RenderBackend);
        bool relaunched = false;
        bool recoveryAttempted = false;
        string details;
        try
        {
            if (effectiveMode == SelfIterationReloadMode.EditorRestart)
            {
                await StopAsync(evidenceDirectory, token);
                await StartAsync(evidenceDirectory, token);
                relaunched = true;
                details = "Editor rebuilt and relaunched.";
            }
            else
            {
                await WaitForRenderReadinessAsync(evidenceDirectory, token);
                string toolName;
                Dictionary<string, object?> arguments;
                switch (effectiveMode)
                {
                    case SelfIterationReloadMode.ShaderReload:
                        toolName = "reload_renderer_shaders";
                        arguments = [];
                        break;
                    case SelfIterationReloadMode.BuildAndReloadRenderer:
                        toolName = "build_and_reload_renderer";
                        arguments = new Dictionary<string, object?>
                        {
                            ["backend"] = _scenario.RenderBackend.ToLowerInvariant(),
                            ["configuration"] = _configuration.Measurement.Configuration,
                            ["first_frame_timeout_ms"] =
                                _configuration.Measurement.ReloadFirstFrameTimeoutMilliseconds,
                        };
                        break;
                    case SelfIterationReloadMode.RendererRestart:
                        toolName = "restart_renderer";
                        arguments = new Dictionary<string, object?>
                        {
                            ["backend"] = _scenario.RenderBackend.ToLowerInvariant(),
                            ["first_frame_timeout_ms"] =
                                _configuration.Measurement.ReloadFirstFrameTimeoutMilliseconds,
                        };
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported reload mode {effectiveMode}.");
                }

                JsonDocument response = await InvokeToolAsync(
                    toolName,
                    arguments,
                    evidenceDirectory,
                    token);
                using (response)
                {
                    if (IsToolError(response.RootElement))
                        throw new InvalidOperationException($"{toolName} returned an MCP error.");
                }
                details = $"Applied {effectiveMode} through MCP.";
            }
        }
        catch (Exception reloadException)
        {
            await StopAsync(evidenceDirectory, token);
            await StartAsync(evidenceDirectory, token);
            recoveryAttempted = true;
            relaunched = true;
            effectiveMode = SelfIterationReloadMode.EditorRestart;
            details =
                $"Requested reload failed ({reloadException.Message}); editor rebuilt and relaunched successfully.";
        }

        try
        {
            await ValidateActiveSessionAsync(evidenceDirectory, token);
        }
        catch (Exception validationException) when (!recoveryAttempted)
        {
            await StopAsync(evidenceDirectory, token);
            await StartAsync(evidenceDirectory, token);
            recoveryAttempted = true;
            relaunched = true;
            effectiveMode = SelfIterationReloadMode.EditorRestart;
            details =
                $"Post-reload validation failed ({validationException.Message}); " +
                "editor rebuilt and relaunched successfully.";
            await ValidateActiveSessionAsync(evidenceDirectory, token);
        }
        return new SelfIterationReloadResult
        {
            Succeeded = true,
            RequestedMode = requestedMode,
            EffectiveMode = effectiveMode,
            EditorRelaunched = relaunched,
            Details = details,
        };
    }

    public async Task StopAsync(string evidenceDirectory, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_sessionName))
            return;

        SelfIterationProcessResult status = await RunSessionCommandAsync(
            ["Status", "-Name", _sessionName, "-AsJson"],
            evidenceDirectory,
            "session-status-before-stop",
            token);
        if (status.Succeeded &&
            SessionOutputReportsState(status.StandardOutput, "Stopped"))
        {
            return;
        }

        SelfIterationProcessResult stop = await RunSessionCommandAsync(
            ["Stop", "-Name", _sessionName, "-AsJson"],
            evidenceDirectory,
            "session-stop",
            token);
        if (!stop.Succeeded ||
            !SessionOutputReportsState(stop.StandardOutput, "Stopped"))
        {
            throw new InvalidOperationException(
                $"Named editor session '{_sessionName}' did not stop cleanly. " +
                $"See {stop.StandardErrorPath}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            string output = Path.Combine(
                _workspaceRoot,
                "Build",
                "_AgentValidation",
                "00000000-000000-shared",
                "self-iteration-shutdown",
                "_session-shutdown");
            await StopAsync(output, CancellationToken.None);
        }
        catch
        {
            // The named session manager remains the recovery surface if shutdown reporting fails.
        }
        _httpClient.Dispose();
    }

    private async Task StartAsync(string evidenceDirectory, CancellationToken token)
    {
        if (_scenario is null || string.IsNullOrWhiteSpace(_sessionName))
            throw new InvalidOperationException("No scenario is selected for the editor session.");

        string environmentPath = Path.Combine(evidenceDirectory, "session-environment.json");
        Directory.CreateDirectory(evidenceDirectory);
        SelfIterationMeasurementConfiguration measurement = _configuration.Measurement;
        SelfIterationScenarioMeasurementOverrides overrides = _scenario.Overrides;
        Dictionary<string, string> environment =
            new(_scenario.Environment, StringComparer.OrdinalIgnoreCase)
            {
                ["XRE_WORLD_MODE"] = "UnitTesting",
                ["XRE_UNIT_TEST_WORLD_SETTINGS_PATH"] = _scenario.UnitTestingWorldSettingsPath,
                ["XRE_UNIT_TEST_RENDER_API"] = _scenario.RenderBackend,
                ["XRE_FORCE_MESH_SUBMISSION_STRATEGY"] = _scenario.MeshSubmissionStrategy,
                ["XRE_ZERO_READBACK_MATERIAL_DRAW_PATH"] =
                    overrides.ZeroReadbackMaterialDrawPath ??
                    measurement.ZeroReadbackMaterialDrawPath,
                ["XRE_PROFILER_ENABLED"] = "1",
                // Auto-dump enables bounded GPU pipeline history without the continuous
                // NDJSON stream produced by launch-time profile capture.
                ["XRE_PROFILE_CAPTURE"] = "0",
                ["XRE_PROFILE_AUTO_DUMP"] = "1",
                ["XRE_PROFILE_MODE"] = measurement.DiagnosticProfileMode,
                ["XRE_PROFILE_RUN_LABEL"] = _sessionName,
                ["XRE_PROFILE_CACHE_MODE"] = measurement.CacheMode,
                ["XRE_SHADER_CACHE_MODE"] = measurement.CacheMode,
                ["XRE_TEXTURE_CACHE_MODE"] = measurement.CacheMode,
                ["XRE_GPU_CLOCK_POLICY"] =
                    overrides.GpuClockPolicy ?? measurement.GpuClockPolicy,
                ["XRE_GPU_TIMESTAMP_DENSE"] =
                    measurement.DiagnosticGpuTimestampDense ? "1" : "0",
                ["XRE_VULKAN_COMMAND_BUFFER_LABELS"] =
                    measurement.DiagnosticVulkanCommandBufferLabels ? "1" : "0",
            };
        SetConfigured(
            environment,
            "XRE_UNIT_TEST_VR_MODE",
            overrides.UnitTestVrMode ?? measurement.UnitTestVrMode);
        SetConfigured(
            environment,
            "XRE_VK_RENDER_TARGET_MODE",
            overrides.VulkanRenderTargetMode ?? measurement.VulkanRenderTargetMode);
        SetTriState(
            environment,
            "XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE",
            overrides.VulkanPrimaryReuse ?? measurement.VulkanPrimaryReuse);
        SetTriState(
            environment,
            "XRE_VULKAN_COMMAND_CHAINS",
            overrides.VulkanCommandChains ?? measurement.VulkanCommandChains);
        SetDisabledFlag(
            environment,
            "XRE_VULKAN_DISABLE_PARALLEL_CHAIN_RECORDING",
            overrides.VulkanParallelCommandChainRecording ??
                measurement.VulkanParallelCommandChainRecording);
        SetDisabledFlag(
            environment,
            "XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING",
            overrides.VulkanParallelSecondaryRecording ??
                measurement.VulkanParallelSecondaryRecording);
        SetConfigured(
            environment,
            "XRE_VULKAN_DIAGNOSTIC_PRESET",
            overrides.VulkanDiagnosticPreset ?? measurement.VulkanDiagnosticPreset);
        SetConfigured(
            environment,
            "XRE_OCCLUSION_CULLING_MODE",
            overrides.OcclusionCullingMode ?? measurement.OcclusionCullingMode);
        SetOptional(
            environment,
            "XRE_PROFILE_SCENE",
            overrides.ProfileScene ?? measurement.ProfileScene);
        SetOptional(
            environment,
            "XRE_PROFILE_CAMERA",
            overrides.ProfileCamera ?? measurement.ProfileCamera);
        SetOptional(
            environment,
            "XRE_PROFILE_LIGHTS",
            overrides.ProfileLights ?? measurement.ProfileLights);
        SetOptional(
            environment,
            "XRE_PROFILE_VIEWPORT",
            overrides.ProfileViewport ?? measurement.ProfileViewport);
        SetOptional(
            environment,
            "XRE_PROFILE_RENDER_SCALE",
            overrides.RenderScale ?? measurement.RenderScale);
        double targetRefreshHz = overrides.TargetRefreshHz ?? measurement.TargetRefreshHz;
        if (targetRefreshHz > 0.0)
        {
            environment["XRE_TARGET_REFRESH_HZ"] =
                targetRefreshHz.ToString(CultureInfo.InvariantCulture);
        }
        await File.WriteAllTextAsync(
            environmentPath,
            JsonSerializer.Serialize(environment, new JsonSerializerOptions { WriteIndented = true }),
            token);

        SelfIterationProcessResult result = await RunSessionCommandAsync(
            [
                "Start",
                "-Name",
                _sessionName,
                "-Configuration",
                _configuration.Measurement.Configuration,
                "-PermissionPolicy",
                "AllowAll",
                "-SessionEnvironmentFile",
                environmentPath,
                "-RendererDevelopment",
                "-StartupTimeoutSeconds",
                "180",
                "-AsJson",
            ],
            evidenceDirectory,
            "session-start",
            token);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Named editor session failed to start. See {result.StandardErrorPath}.");
        }
    }

    private async Task CaptureValidationEvidenceAsync(
        string evidenceDirectory,
        CancellationToken token)
    {
        using JsonDocument reloadStatus = await InvokeToolAsync(
            "get_renderer_reload_status",
            EmptyArguments,
            evidenceDirectory,
            token);
        if (IsToolError(reloadStatus.RootElement))
            throw new InvalidOperationException("get_renderer_reload_status returned an MCP error.");

        await WaitForGpuTimingsAsync(evidenceDirectory, token);

        using JsonDocument cpuDump = await InvokeToolAsync(
            "dump_cpu_frame_profile",
            EmptyArguments,
            evidenceDirectory,
            token);
        if (_configuration.Acceptance.RequireCpuAndGpuDiagnosticDumps &&
            IsToolError(cpuDump.RootElement))
        {
            throw new InvalidOperationException("dump_cpu_frame_profile returned an MCP error.");
        }

        using JsonDocument gpuDump = await InvokeToolAsync(
            "dump_gpu_render_pipeline_profile",
            new Dictionary<string, object?> { ["all_pipelines"] = true },
            evidenceDirectory,
            token);
        if (_configuration.Acceptance.RequireCpuAndGpuDiagnosticDumps &&
            IsToolError(gpuDump.RootElement))
        {
            throw new InvalidOperationException(
                "dump_gpu_render_pipeline_profile returned an MCP error.");
        }
        if (_configuration.Measurement.CaptureScreenshotAfterReload)
        {
            string captureDirectory = Path.Combine(evidenceDirectory, "mcp-captures");
            Directory.CreateDirectory(captureDirectory);
            try
            {
                await InvokeToolAsync(
                    "capture_viewport_screenshot",
                    new Dictionary<string, object?> { ["output_dir"] = captureDirectory },
                    evidenceDirectory,
                    token);
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(evidenceDirectory, "screenshot-capture-error.txt"),
                    exception.ToString(),
                    token);
            }
        }
    }

    private async Task ValidateActiveSessionAsync(
        string evidenceDirectory,
        CancellationToken token)
    {
        await WaitForRenderReadinessAsync(evidenceDirectory, token);
        await EnableValidationProfilingAsync(evidenceDirectory, token);
        await CaptureValidationEvidenceAsync(evidenceDirectory, token);
    }

    private async Task WaitForRenderReadinessAsync(
        string evidenceDirectory,
        CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            _configuration.Measurement.ReloadFirstFrameTimeoutMilliseconds);
        do
        {
            using JsonDocument response = await InvokeToolAsync(
                "get_render_state",
                EmptyArguments,
                evidenceDirectory,
                token,
                fixedOutputName: "mcp-render-readiness.json");
            if (!IsToolError(response.RootElement) &&
                IsRenderStateReady(response.RootElement))
            {
                return;
            }
            await Task.Delay(250, token);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException(
            "The editor did not expose an active rendering viewport before the reload deadline.");
    }

    private async Task EnableValidationProfilingAsync(
        string evidenceDirectory,
        CancellationToken token)
    {
        string[] preferencePaths =
        [
            "Debug.EnableProfilerFrameLogging",
            "Debug.EnableRenderStatisticsTracking",
            "Debug.EnableGpuRenderPipelineProfiling",
        ];
        foreach (string propertyName in preferencePaths)
        {
            using JsonDocument response = await InvokeToolAsync(
                "set_editor_preference",
                new Dictionary<string, object?>
                {
                    ["property_name"] = propertyName,
                    ["value"] = true,
                },
                evidenceDirectory,
                token);
            if (IsToolError(response.RootElement))
            {
                throw new InvalidOperationException(
                    $"Unable to enable the validation preference '{propertyName}'.");
            }
        }
    }

    private async Task WaitForGpuTimingsAsync(
        string evidenceDirectory,
        CancellationToken token)
    {
        bool required = _configuration.Acceptance.RequireCpuAndGpuDiagnosticDumps;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            _configuration.Measurement.ReloadFirstFrameTimeoutMilliseconds);
        do
        {
            using JsonDocument response = await InvokeToolAsync(
                "get_render_profiler_stats",
                EmptyArguments,
                evidenceDirectory,
                token,
                fixedOutputName: "mcp-gpu-readiness.json");
            if (!IsToolError(response.RootElement) &&
                TryGetGpuTimingState(
                    response.RootElement,
                    out bool supported,
                    out bool ready))
            {
                if (ready)
                    return;
                if (!supported && required)
                {
                    throw new InvalidOperationException(
                        "The active renderer does not support GPU pipeline timings.");
                }
            }
            if (!required)
                return;
            await Task.Delay(250, token);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException(
            "GPU render-pipeline timings did not become ready after reload.");
    }

    private async Task<JsonDocument> InvokeToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        string evidenceDirectory,
        CancellationToken token,
        string? fixedOutputName = null)
    {
        Uri endpoint = ReadSessionEndpoint();
        var request = new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method = "tools/call",
            @params = new { name, arguments },
        };
        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(endpoint, request, token);
        string json = await response.Content.ReadAsStringAsync(token);
        response.EnsureSuccessStatusCode();
        string outputPath = Path.Combine(
            evidenceDirectory,
            fixedOutputName ??
            $"mcp-{Interlocked.Increment(ref _commandIndex):D2}-{name}.json");
        await File.WriteAllTextAsync(outputPath, json, token);
        return JsonDocument.Parse(json);
    }

    private Uri ReadSessionEndpoint()
    {
        string sessionsRoot = Path.Combine(
            _workspaceRoot,
            "Build",
            "_AgentValidation",
            "00000000-000000-shared",
            "mcp-sessions");
        string manifestPath = Directory.EnumerateFiles(
                sessionsRoot,
                "session.json",
                SearchOption.AllDirectories)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .First(path =>
            {
                using JsonDocument candidate = JsonDocument.Parse(File.ReadAllText(path));
                return string.Equals(
                    candidate.RootElement.GetProperty("name").GetString(),
                    _sessionName,
                    StringComparison.Ordinal);
            });
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        string endpoint = manifest.RootElement.GetProperty("endpoint").GetString()
            ?? throw new InvalidDataException($"Session manifest has no endpoint: {manifestPath}");
        return new Uri(endpoint);
    }

    private Task<SelfIterationProcessResult> RunSessionCommandAsync(
        IReadOnlyList<string> sessionArguments,
        string evidenceDirectory,
        string stem,
        CancellationToken token)
    {
        List<string> arguments =
        [
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            Path.Combine(_workspaceRoot, "Tools", "Manage-McpEditorSession.ps1"),
        ];
        arguments.AddRange(sessionArguments);
        return _processRunner.RunAsync(
            "powershell.exe",
            arguments,
            _workspaceRoot,
            TimeSpan.FromMinutes(20),
            evidenceDirectory,
            $"{stem}-{Interlocked.Increment(ref _commandIndex):D2}",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MSBUILDDISABLENODEREUSE"] = "1",
            },
            cancellationToken: token);
    }

    private static SelfIterationReloadMode ResolveReloadMode(
        SelfIterationReloadMode requestedMode,
        IReadOnlyList<string> changedPaths,
        string backend)
    {
        bool shaderOnly = changedPaths.Count > 0 && changedPaths.All(IsShaderPath);
        bool openGlLeafOnly = changedPaths.Count > 0 && changedPaths.All(path =>
            SelfIterationConfiguration.NormalizeRelativePath(path).StartsWith(
                "XREngine.Runtime.Rendering.OpenGL/",
                StringComparison.OrdinalIgnoreCase));
        bool compiledSourceChanged = changedPaths.Any(path =>
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".targets", StringComparison.OrdinalIgnoreCase);
        });

        SelfIterationReloadMode mode = requestedMode == SelfIterationReloadMode.Auto
            ? shaderOnly
                ? SelfIterationReloadMode.ShaderReload
                : backend.Equals("OpenGL", StringComparison.OrdinalIgnoreCase) && openGlLeafOnly
                    ? SelfIterationReloadMode.BuildAndReloadRenderer
                    : SelfIterationReloadMode.EditorRestart
            : requestedMode;

        if (mode == SelfIterationReloadMode.BuildAndReloadRenderer &&
            (!backend.Equals("OpenGL", StringComparison.OrdinalIgnoreCase) || !openGlLeafOnly))
        {
            return SelfIterationReloadMode.EditorRestart;
        }
        if (mode == SelfIterationReloadMode.RendererRestart && compiledSourceChanged)
            return SelfIterationReloadMode.EditorRestart;
        if (mode == SelfIterationReloadMode.ShaderReload && !shaderOnly)
            return SelfIterationReloadMode.EditorRestart;
        return mode;
    }

    private static bool IsShaderPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".glsl", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".vert", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frag", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".comp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".geom", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tesc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tese", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".shader", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsToolError(JsonElement root)
    {
        if (root.TryGetProperty("error", out _))
            return true;
        return root.TryGetProperty("result", out JsonElement result) &&
            result.TryGetProperty("isError", out JsonElement isError) &&
            isError.ValueKind == JsonValueKind.True;
    }

    private static bool SessionOutputReportsState(string json, string expectedState)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("State", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return expectedState.Equals(
                        property.Value.GetString(),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    private static bool TryGetGpuTimingState(
        JsonElement root,
        out bool supported,
        out bool ready)
    {
        supported = false;
        ready = false;
        if (!root.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("structuredContent", out JsonElement content) ||
            !content.TryGetProperty("gpu_pipeline", out JsonElement pipeline))
        {
            return false;
        }

        supported = pipeline.TryGetProperty("supported", out JsonElement supportedValue) &&
            supportedValue.ValueKind == JsonValueKind.True;
        ready = pipeline.TryGetProperty("timings_ready", out JsonElement readyValue) &&
            readyValue.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool IsRenderStateReady(JsonElement root)
    {
        if (!root.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("structuredContent", out JsonElement content) ||
            !content.TryGetProperty("renderFrameId", out JsonElement frameId) ||
            !frameId.TryGetUInt64(out ulong frameNumber) ||
            frameNumber == 0 ||
            !content.TryGetProperty("activeViewports", out JsonElement viewports) ||
            viewports.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement viewport in viewports.EnumerateArray())
        {
            if (viewport.TryGetProperty("pipelineType", out JsonElement pipelineType) &&
                pipelineType.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(pipelineType.GetString()))
            {
                return true;
            }
        }
        return false;
    }

    private static void SetConfigured(
        IDictionary<string, string> environment,
        string name,
        string value)
    {
        if (!value.Equals("Configured", StringComparison.Ordinal))
            environment[name] = value;
    }

    private static void SetTriState(
        IDictionary<string, string> environment,
        string name,
        string value)
    {
        if (value.Equals("Configured", StringComparison.Ordinal))
            return;
        environment[name] = value.Equals("Enabled", StringComparison.Ordinal) ? "1" : "0";
    }

    private static void SetDisabledFlag(
        IDictionary<string, string> environment,
        string name,
        string value)
    {
        if (value.Equals("Configured", StringComparison.Ordinal))
            return;
        environment[name] = value.Equals("Disabled", StringComparison.Ordinal) ? "1" : "0";
    }

    private static void SetOptional(
        IDictionary<string, string> environment,
        string name,
        string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            environment[name] = value;
    }

    private static string BuildSessionName(
        string campaign,
        string scenario,
        string ownerToken)
    {
        string raw = $"selfit-{campaign}-{scenario}";
        string safe = new(
            raw.Select(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                        ? character
                        : '-')
                .ToArray());
        string suffix = $"-{ownerToken}";
        int maximumBaseLength = 64 - suffix.Length;
        string boundedBase = safe.Length <= maximumBaseLength
            ? safe
            : safe[..maximumBaseLength];
        return boundedBase.TrimEnd('-', '_', '.') + suffix;
    }
}

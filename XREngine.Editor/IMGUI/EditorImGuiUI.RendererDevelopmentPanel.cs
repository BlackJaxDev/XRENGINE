using ImGuiNET;
using System.Numerics;
using XREngine.Editor.HotReload;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static Task<RendererReplacementResult>? _rendererReloadOperation;
    private static int _rendererDevelopmentBackendIndex;
    private static string? _rendererDevelopmentActionError;

    public static void EnableRendererDevelopmentMode()
        => _showRendererDevelopment = true;

    private static void DrawRendererReloadOverlay()
    {
        RendererReloadSnapshot snapshot = RendererHotReloadService.Current.Snapshot;
        if (snapshot.State is RendererReloadState.Idle or
            RendererReloadState.Failed or
            RendererReloadState.FailedStopped)
        {
            return;
        }

        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        Vector2 padding = new(18.0f, 12.0f);
        string text = $"Reloading {snapshot.BackendId} renderer: {snapshot.Status}";
        Vector2 textSize = ImGui.CalcTextSize(text);
        Vector2 minimum = viewport.WorkPos + new Vector2(
            MathF.Max(0.0f, (viewport.WorkSize.X - textSize.X) * 0.5f) - padding.X,
            20.0f);
        Vector2 maximum = minimum + textSize + (padding * 2.0f);
        ImDrawListPtr drawList = ImGui.GetForegroundDrawList(viewport);
        drawList.AddRectFilled(
            minimum,
            maximum,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.06f, 0.07f, 0.09f, 0.94f)),
            8.0f);
        drawList.AddRect(
            minimum,
            maximum,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.70f, 1.0f, 1.0f)),
            8.0f,
            ImDrawFlags.None,
            2.0f);
        drawList.AddText(
            minimum + padding,
            ImGui.ColorConvertFloat4ToU32(Vector4.One),
            text);
    }

    private static void DrawRendererDevelopmentPanel()
    {
        if (!ImGui.Begin("Renderer Development", ref _showRendererDevelopment))
        {
            ImGui.End();
            return;
        }

        RendererHotReloadService service = RendererHotReloadService.Current;
        RendererReloadSnapshot snapshot = service.Snapshot;
        RendererBackendId backendId = _rendererDevelopmentBackendIndex == 0
            ? RendererBackendId.OpenGL
            : RendererBackendId.Vulkan;
        RendererBackendRegistration registration =
            RuntimeRenderingHostServices.Factories.RendererBackends.GetRequired(backendId);
        RendererBackendMetadata metadata = registration.Metadata;

        ImGui.TextUnformatted($"State: {snapshot.State}");
        ImGui.TextUnformatted($"Status: {snapshot.Status}");
        ImGui.TextUnformatted($"Active backend: {metadata.DisplayName} ({metadata.Id})");
        ImGui.TextUnformatted($"Generation: {metadata.Generation}");
        ImGui.TextUnformatted($"Build hash: {DisplayOrStatic(service.ActiveBuildHash ?? metadata.BuildHash)}");
        ImGui.TextUnformatted($"Load context: {service.ActiveLoadContextName ?? "Default/static"}");
        ImGui.TextUnformatted($"Managed Hot Reload: {RendererManagedHotReload.LastMechanism}");
        ImGui.TextUnformatted($"Managed invalidations: {RendererManagedHotReload.AppliedUpdateCount}");
        ImGui.TextUnformatted(
            $"Success: {snapshot.SuccessfulReloads}  Failed: {snapshot.FailedReloads}  Rollbacks: {snapshot.LastGoodRollbacks}  Unload leaks: {snapshot.UnloadLeaks}");

        if (metadata.ReloadLimitations != RendererBackendReloadLimitations.None)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.75f, 0.25f, 1.0f));
            ImGui.TextWrapped(
                $"Current limitations: {metadata.ReloadLimitations}. {metadata.ReloadLimitationDescription}");
            ImGui.PopStyleColor();
        }

        DrawRendererReloadPhaseTimings(snapshot);
        DrawRendererBackendSelector();

        EditorPreferences preferences = Engine.GlobalEditorPreferences;
        bool automaticShaderReload = preferences.RendererAutomaticShaderReload;
        if (ImGui.Checkbox("Automatic shader reload", ref automaticShaderReload))
            preferences.RendererAutomaticShaderReload = automaticShaderReload;

        bool automaticBackendReload = preferences.RendererAutomaticBackendReload;
        if (ImGui.Checkbox("Automatic backend build and reload (opt-in)", ref automaticBackendReload))
            preferences.RendererAutomaticBackendReload = automaticBackendReload;

        int debounce = preferences.RendererBackendReloadDebounceMs;
        ImGui.SetNextItemWidth(160.0f);
        if (ImGui.InputInt("Source debounce (ms)", ref debounce, 50, 250))
            preferences.RendererBackendReloadDebounceMs = debounce;

        service.ConfigureAutomaticReload(
            preferences.RendererAutomaticBackendReload,
            backendId,
            preferences.RendererBackendReloadDebounceMs);

        bool operationRunning = _rendererReloadOperation is { IsCompleted: false };
        if (operationRunning)
            ImGui.BeginDisabled();

        if (ImGui.Button("Reload Shaders"))
            _rendererDevelopmentActionError = $"Reloaded {service.ReloadShaders()} loaded shader source(s).";
        ImGui.SameLine();
        if (ImGui.Button("Restart Renderer"))
            StartRendererOperation(service.RestartCurrentGenerationAsync(backendId, FirstFrameTimeout(preferences)));
        ImGui.SameLine();
        if (ImGui.Button("Build and Reload Renderer"))
            StartRendererOperation(service.BuildAndReloadAsync(backendId, firstFrameTimeout: FirstFrameTimeout(preferences)));

        if (ImGui.Button("Retry Candidate"))
            StartRendererOperation(service.RetryCandidateAsync(FirstFrameTimeout(preferences)));
        ImGui.SameLine();
        if (ImGui.Button("Rollback"))
            StartRendererOperation(service.RollBackAsync(FirstFrameTimeout(preferences)));
        ImGui.SameLine();
        if (ImGui.Button("Cancel Pending Build"))
            service.CancelPendingBuild();

        if (RuntimeEngine.VRState.IsInVR)
        {
            if (ImGui.Button("Restart Renderer And OpenXR Session"))
            {
                StartRendererOperation(
                    service.RestartCurrentGenerationWithOpenXrSessionAsync(
                        backendId,
                        FirstFrameTimeout(preferences)));
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(
                RuntimeEngine.VRState.IsOpenXRActive
                    ? "World/editor state is preserved; OpenXR presentation is recreated."
                    : "OpenVR reload remains safely blocked; stop OpenVR presentation first.");
        }

        if (operationRunning)
            ImGui.EndDisabled();

        ObserveRendererOperation();
        if (_rendererReloadOperation is { IsCompleted: false })
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Working...");
        }

        string? error = snapshot.LastError ?? _rendererDevelopmentActionError;
        if (!string.IsNullOrWhiteSpace(error))
        {
            ImGui.SeparatorText("Diagnostics");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(error);
            ImGui.PopTextWrapPos();
            if (ImGui.Button("Copy Diagnostics"))
                ImGui.SetClipboardText(BuildRendererDiagnostics(service, snapshot, error));
        }

        RendererBackendBuildResult? build = service.LastBuild;
        if (build is not null)
        {
            ImGui.SeparatorText("Last Build");
            ImGui.TextUnformatted(
                $"Generation {build.Generation}: {(build.Succeeded ? "succeeded" : build.Cancelled ? "cancelled" : "failed")} in {build.Duration.TotalMilliseconds:F0} ms");
            for (int i = 0; i < build.Diagnostics.Count; i++)
            {
                RendererBackendBuildDiagnostic diagnostic = build.Diagnostics[i];
                ImGui.BulletText(
                    $"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        ImGui.End();
    }

    private static void DrawRendererBackendSelector()
    {
        ImGui.SetNextItemWidth(160.0f);
        if (!ImGui.BeginCombo(
                "Backend",
                _rendererDevelopmentBackendIndex == 0 ? "OpenGL" : "Vulkan"))
        {
            return;
        }

        if (ImGui.Selectable("OpenGL", _rendererDevelopmentBackendIndex == 0))
            _rendererDevelopmentBackendIndex = 0;
        if (ImGui.Selectable("Vulkan", _rendererDevelopmentBackendIndex == 1))
            _rendererDevelopmentBackendIndex = 1;
        ImGui.EndCombo();
    }

    private static void DrawRendererReloadPhaseTimings(RendererReloadSnapshot snapshot)
    {
        if (snapshot.PhaseDurations.Count == 0 ||
            !ImGui.TreeNode("Transaction timings"))
        {
            return;
        }

        foreach ((string phase, TimeSpan duration) in snapshot.PhaseDurations)
            ImGui.BulletText($"{phase}: {duration.TotalMilliseconds:F1} ms");
        ImGui.TreePop();
    }

    private static void StartRendererOperation(Task<RendererReplacementResult> operation)
    {
        _rendererDevelopmentActionError = null;
        _rendererReloadOperation = operation;
    }

    private static void ObserveRendererOperation()
    {
        Task<RendererReplacementResult>? operation = _rendererReloadOperation;
        if (operation is null || !operation.IsCompleted)
            return;

        _rendererReloadOperation = null;
        if (operation.IsCanceled)
        {
            _rendererDevelopmentActionError = "Renderer reload was cancelled.";
            return;
        }

        if (operation.IsFaulted)
        {
            _rendererDevelopmentActionError =
                operation.Exception?.GetBaseException().ToString() ?? "Renderer reload failed.";
            return;
        }

        RendererReplacementResult result = operation.GetAwaiter().GetResult();
        _rendererDevelopmentActionError = result.Succeeded
            ? "Renderer reload completed successfully."
            : result.Error;
    }

    private static string BuildRendererDiagnostics(
        RendererHotReloadService service,
        RendererReloadSnapshot snapshot,
        string error)
    {
        RendererBackendBuildResult? build = service.LastBuild;
        return
            $"Backend: {snapshot.BackendId}{Environment.NewLine}" +
            $"Generation: {snapshot.Generation}{Environment.NewLine}" +
            $"State: {snapshot.State}{Environment.NewLine}" +
            $"Failure: {snapshot.FailureKind}{Environment.NewLine}" +
            $"Status: {snapshot.Status}{Environment.NewLine}" +
            $"ALC: {service.ActiveLoadContextName ?? "Default/static"}{Environment.NewLine}" +
            $"Build manifest: {build?.ManifestPath ?? "<none>"}{Environment.NewLine}" +
            $"Error: {error}{Environment.NewLine}{Environment.NewLine}" +
            (build?.Output ?? string.Empty);
    }

    private static TimeSpan FirstFrameTimeout(EditorPreferences preferences)
        => TimeSpan.FromMilliseconds(preferences.RendererBackendFirstFrameTimeoutMs);

    private static string DisplayOrStatic(string value)
        => string.IsNullOrWhiteSpace(value) ? "static" : value;
}

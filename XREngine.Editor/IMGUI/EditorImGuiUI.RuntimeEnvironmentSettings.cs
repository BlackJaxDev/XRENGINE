using ImGuiNET;
using System.Numerics;
using XREngine.Editor.HotReload;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly Dictionary<string, string> RuntimeEnvironmentEditBuffers =
        new(StringComparer.OrdinalIgnoreCase);
    private static string _runtimeEnvironmentSearch = string.Empty;
    private static bool _runtimeEnvironmentRendererRestartPending;
    private static bool _runtimeEnvironmentOpenXrRestartPending;
    private static bool _runtimeEnvironmentProcessRestartPending;
    private static string? _runtimeEnvironmentStatus;

    private static void DrawRuntimeEnvironmentPreferences(EditorRuntimeEnvironmentPreferences preferences)
    {
        ImGui.TextWrapped(
            "These overrides affect this process only. Clearing an override restores the value captured at launch; " +
            "saving editor preferences never persists validation, diagnostics, or feature downgrades.");

        ImGui.SetNextItemWidth(420.0f);
        ImGui.InputTextWithHint(
            "##RuntimeEnvironmentSearch",
            "Filter by variable, field, or category...",
            ref _runtimeEnvironmentSearch,
            256);
        ImGui.SameLine();
        if (ImGui.Button("Refresh Process Values"))
        {
            XREnvironment.RefreshFromProcess();
            RuntimeEnvironmentEditBuffers.Clear();
            ReapplyEditorRuntimeEnvironment();
            _runtimeEnvironmentStatus = "Refreshed values changed outside the runtime settings facade.";
        }

        DrawRuntimeEnvironmentRestartActions();

        int overrideCount = 0;
        IReadOnlyList<RuntimeEnvironmentVariableDescriptor> descriptors = preferences.Variables;
        for (int i = 0; i < descriptors.Count; i++)
            if (XREnvironment.GetState(descriptors[i].Name).HasRuntimeOverride)
                overrideCount++;

        ImGui.TextDisabled(
            $"{descriptors.Count} declared variables; {overrideCount} temporary runtime override(s). " +
            "Unset diagnostics/validation and downgrade switches are off by default.");

        if (overrideCount > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear All Overrides"))
            {
                for (int i = 0; i < descriptors.Count; i++)
                    XREnvironment.ClearRuntimeOverride(descriptors[i].Name);

                RuntimeEnvironmentEditBuffers.Clear();
                ReapplyEditorRuntimeEnvironment();
                _runtimeEnvironmentStatus = "Cleared all runtime overrides and restored launch values.";
            }
        }

        if (!string.IsNullOrWhiteSpace(_runtimeEnvironmentStatus))
            ImGui.TextWrapped(_runtimeEnvironmentStatus);

        ImGuiTableFlags flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollX |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable(
                "RuntimeEnvironmentVariables",
                6,
                flags,
                new Vector2(0.0f, MathF.Max(320.0f, ImGui.GetContentRegionAvail().Y))))
        {
            return;
        }

        try
        {
            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 90.0f);
            ImGui.TableSetupColumn("Variable", ImGuiTableColumnFlags.WidthFixed, 310.0f);
            ImGui.TableSetupColumn("Launch", ImGuiTableColumnFlags.WidthFixed, 170.0f);
            ImGui.TableSetupColumn("Runtime override", ImGuiTableColumnFlags.WidthFixed, 300.0f);
            ImGui.TableSetupColumn("Effective", ImGuiTableColumnFlags.WidthFixed, 170.0f);
            ImGui.TableSetupColumn("Applies", ImGuiTableColumnFlags.WidthFixed, 150.0f);
            ImGui.TableHeadersRow();

            string search = _runtimeEnvironmentSearch.Trim();
            for (int i = 0; i < descriptors.Count; i++)
            {
                RuntimeEnvironmentVariableDescriptor descriptor = descriptors[i];
                if (!MatchesRuntimeEnvironmentSearch(descriptor, search))
                    continue;

                DrawRuntimeEnvironmentVariableRow(descriptor);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private static void DrawRuntimeEnvironmentRestartActions()
    {
        bool operationRunning = _rendererReloadOperation is { IsCompleted: false };
        if (_runtimeEnvironmentRendererRestartPending || _runtimeEnvironmentOpenXrRestartPending)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                new Vector4(1.0f, 0.72f, 0.18f, 1.0f),
                _runtimeEnvironmentOpenXrRestartPending
                    ? "One or more changes require a renderer and OpenXR session restart."
                    : "One or more changes require a renderer restart.");

            using (new ImGuiDisabledScope(operationRunning))
            {
                RendererBackendId backendId = RuntimeEngine.Rendering.State.IsVulkan
                    ? RendererBackendId.Vulkan
                    : RendererBackendId.OpenGL;
                EditorPreferences preferences = Engine.GlobalEditorPreferences;
                if (_runtimeEnvironmentOpenXrRestartPending && RuntimeEngine.VRState.IsOpenXRActive)
                {
                    if (ImGui.Button("Apply: Restart Renderer And OpenXR"))
                    {
                        StartRendererOperation(
                            RendererHotReloadService.Current.RestartCurrentGenerationWithOpenXrSessionAsync(
                                backendId,
                                FirstFrameTimeout(preferences)));
                        _runtimeEnvironmentRendererRestartPending = false;
                        _runtimeEnvironmentOpenXrRestartPending = false;
                    }
                }
                else if (ImGui.Button("Apply: Restart Renderer"))
                {
                    StartRendererOperation(
                        RendererHotReloadService.Current.RestartCurrentGenerationAsync(
                            backendId,
                            FirstFrameTimeout(preferences)));
                    _runtimeEnvironmentRendererRestartPending = false;
                    _runtimeEnvironmentOpenXrRestartPending = false;
                }
            }
        }

        if (_runtimeEnvironmentProcessRestartPending)
        {
            ImGui.TextColored(
                new Vector4(1.0f, 0.72f, 0.18f, 1.0f),
                "One or more launch/bootstrap values require an application restart. " +
                "The override is active in the process environment now, but initialized subsystems retain their current state.");
        }
    }

    private static void DrawRuntimeEnvironmentVariableRow(RuntimeEnvironmentVariableDescriptor descriptor)
    {
        RuntimeEnvironmentVariableState state = XREnvironment.GetState(descriptor.Name);

        ImGui.PushID(descriptor.Name);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(descriptor.Category.ToString());

        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(descriptor.Name);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"{descriptor.FieldName}\n{descriptor.DefaultBehavior}\n" +
                $"Kind: {descriptor.ValueKind}; apply: {FormatApplyMode(descriptor.ApplyMode)}");
        }

        ImGui.TableSetColumnIndex(2);
        DrawRuntimeEnvironmentValue(state.LaunchValue, descriptor.ValueKind);

        ImGui.TableSetColumnIndex(3);
        if (descriptor.ValueKind == RuntimeEnvironmentValueKind.Boolean)
            DrawRuntimeEnvironmentBooleanEditor(descriptor, state);
        else
            DrawRuntimeEnvironmentTextEditor(descriptor, state);

        ImGui.TableSetColumnIndex(4);
        DrawRuntimeEnvironmentValue(state.EffectiveValue, descriptor.ValueKind);

        ImGui.TableSetColumnIndex(5);
        ImGui.TextUnformatted(FormatApplyMode(descriptor.ApplyMode));
        if (descriptor.IsDiagnosticOrValidation)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("[opt-in]");
        }
        else if (descriptor.IsDowngradeOverride)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("[downgrade]");
        }

        ImGui.PopID();
    }

    private static void DrawRuntimeEnvironmentBooleanEditor(
        RuntimeEnvironmentVariableDescriptor descriptor,
        RuntimeEnvironmentVariableState state)
    {
        if (state.HasRuntimeOverride)
        {
            bool enabled = XREnvironment.IsEnabled(descriptor.Name);
            if (ImGui.Checkbox("##Value", ref enabled))
                SetRuntimeEnvironmentOverride(descriptor, enabled ? "1" : "0");

            ImGui.SameLine();
            if (ImGui.SmallButton("Inherit launch"))
                ClearRuntimeEnvironmentOverride(descriptor);
            return;
        }

        if (ImGui.SmallButton("Override on"))
            SetRuntimeEnvironmentOverride(descriptor, "1");
        ImGui.SameLine();
        if (ImGui.SmallButton("Override off"))
            SetRuntimeEnvironmentOverride(descriptor, "0");
        ImGui.SameLine();
        ImGui.TextDisabled("inheriting");
    }

    private static void DrawRuntimeEnvironmentTextEditor(
        RuntimeEnvironmentVariableDescriptor descriptor,
        RuntimeEnvironmentVariableState state)
    {
        if (!RuntimeEnvironmentEditBuffers.TryGetValue(descriptor.Name, out string? buffer))
        {
            buffer = state.HasRuntimeOverride
                ? state.RuntimeOverrideValue ?? string.Empty
                : state.EffectiveValue ?? string.Empty;
        }

        ImGui.SetNextItemWidth(190.0f);
        ImGuiInputTextFlags inputFlags = descriptor.ValueKind == RuntimeEnvironmentValueKind.Secret
            ? ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue
            : ImGuiInputTextFlags.EnterReturnsTrue;
        bool submitted = ImGui.InputText("##Value", ref buffer, 4096, inputFlags);
        RuntimeEnvironmentEditBuffers[descriptor.Name] = buffer;

        ImGui.SameLine();
        if ((submitted || ImGui.SmallButton("Apply")) &&
            !string.Equals(buffer, state.RuntimeOverrideValue, StringComparison.Ordinal))
        {
            SetRuntimeEnvironmentOverride(
                descriptor,
                string.IsNullOrEmpty(buffer) ? null : buffer);
        }

        if (state.HasRuntimeOverride)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Inherit"))
                ClearRuntimeEnvironmentOverride(descriptor);
        }
    }

    private static void SetRuntimeEnvironmentOverride(
        RuntimeEnvironmentVariableDescriptor descriptor,
        string? value)
    {
        try
        {
            XREnvironment.SetRuntimeOverride(descriptor.Name, value);
            ReapplyEditorRuntimeEnvironment();
            MarkRuntimeEnvironmentRestartPending(descriptor.ApplyMode);
            _runtimeEnvironmentStatus =
                $"{descriptor.Name} now {(value is null ? "explicitly unset" : $"overridden to '{MaskIfSensitive(value, descriptor.ValueKind)}")}.";
        }
        catch (Exception ex)
        {
            _runtimeEnvironmentStatus = $"Failed to override {descriptor.Name}: {ex.Message}";
        }
    }

    private static void ClearRuntimeEnvironmentOverride(RuntimeEnvironmentVariableDescriptor descriptor)
    {
        try
        {
            if (!XREnvironment.ClearRuntimeOverride(descriptor.Name))
                return;

            RuntimeEnvironmentEditBuffers.Remove(descriptor.Name);
            ReapplyEditorRuntimeEnvironment();
            MarkRuntimeEnvironmentRestartPending(descriptor.ApplyMode);
            _runtimeEnvironmentStatus = $"Restored launch value for {descriptor.Name}.";
        }
        catch (Exception ex)
        {
            _runtimeEnvironmentStatus = $"Failed to restore {descriptor.Name}: {ex.Message}";
        }
    }

    private static void ReapplyEditorRuntimeEnvironment()
    {
        Engine.EditorPreferences.ApplyRuntimeSideEffects();
        EffectiveSettingsEnvOverrides.ReloadFromEnvironment();
    }

    private static void MarkRuntimeEnvironmentRestartPending(RuntimeEnvironmentApplyMode applyMode)
    {
        if (applyMode == RuntimeEnvironmentApplyMode.RendererRestart)
            _runtimeEnvironmentRendererRestartPending = true;
        else if (applyMode == RuntimeEnvironmentApplyMode.OpenXrSessionRestart)
        {
            _runtimeEnvironmentRendererRestartPending = true;
            _runtimeEnvironmentOpenXrRestartPending = true;
        }
        else if (applyMode == RuntimeEnvironmentApplyMode.ProcessRestart)
            _runtimeEnvironmentProcessRestartPending = true;
    }

    private static bool MatchesRuntimeEnvironmentSearch(
        RuntimeEnvironmentVariableDescriptor descriptor,
        string search)
        => string.IsNullOrWhiteSpace(search) ||
           descriptor.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
           descriptor.FieldName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
           descriptor.Category.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);

    private static void DrawRuntimeEnvironmentValue(
        string? value,
        RuntimeEnvironmentValueKind valueKind)
    {
        if (value is null)
        {
            ImGui.TextDisabled("<unset>");
            return;
        }

        ImGui.TextUnformatted(MaskIfSensitive(value, valueKind));
        if (valueKind == RuntimeEnvironmentValueKind.Secret && ImGui.IsItemHovered())
            ImGui.SetTooltip("Sensitive value is masked.");
    }

    private static string MaskIfSensitive(string value, RuntimeEnvironmentValueKind valueKind)
        => valueKind == RuntimeEnvironmentValueKind.Secret
            ? value.Length == 0 ? "<empty>" : "********"
            : value;

    private static string FormatApplyMode(RuntimeEnvironmentApplyMode applyMode)
        => applyMode switch
        {
            RuntimeEnvironmentApplyMode.Immediate => "Immediately",
            RuntimeEnvironmentApplyMode.NextOperation => "Next operation",
            RuntimeEnvironmentApplyMode.RendererRestart => "Renderer restart",
            RuntimeEnvironmentApplyMode.OpenXrSessionRestart => "OpenXR restart",
            RuntimeEnvironmentApplyMode.ProcessRestart => "App restart",
            _ => applyMode.ToString(),
        };
}

using ImGuiNET;
using System.Numerics;
using XREngine.Editor.HotReload;
using XREngine.Editor.Settings;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly Dictionary<string, string> RuntimeEnvironmentEditBuffers =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly RuntimeEnvironmentCategory[] RuntimeEnvironmentCategories =
        Enum.GetValues<RuntimeEnvironmentCategory>();
    private static readonly string[] RuntimeEnvironmentCategoryLabels =
        BuildRuntimeEnvironmentCategoryLabels();

    private static readonly Vector4 RuntimeEnvironmentOverrideColor = new(1.0f, 0.84f, 0.35f, 1.0f);
    private static readonly Vector4 RuntimeEnvironmentActiveColor = new(0.55f, 0.92f, 0.62f, 1.0f);
    private static readonly Vector4 RuntimeEnvironmentUnsupportedColor = new(1.0f, 0.38f, 0.35f, 1.0f);

    private static string _runtimeEnvironmentSearch = string.Empty;
    private static int _runtimeEnvironmentCategoryFilterIndex;
    private static bool _runtimeEnvironmentOnlyActive;
    private static bool _runtimeEnvironmentOnlyOverrides;
    private static bool _runtimeEnvironmentOnlyUnsupported;
    private static bool _runtimeEnvironmentRendererRestartPending;
    private static bool _runtimeEnvironmentOpenXrRestartPending;
    private static bool _runtimeEnvironmentProcessRestartPending;
    private static string? _runtimeEnvironmentStatus;

    private static string[] BuildRuntimeEnvironmentCategoryLabels()
    {
        string[] labels = new string[RuntimeEnvironmentCategories.Length + 1];
        labels[0] = "All Categories";
        for (int i = 0; i < RuntimeEnvironmentCategories.Length; i++)
            labels[i + 1] = RuntimeEnvironmentCategories[i].ToString();

        return labels;
    }

    private static void DrawRuntimeEnvironmentPanel()
    {
        if (!_showRuntimeEnvironment)
            return;

        ImGui.SetNextWindowSize(new Vector2(1280.0f, 680.0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Runtime Environment", ref _showRuntimeEnvironment))
        {
            ImGui.End();
            return;
        }

        DrawRuntimeEnvironmentPreferences(
            EditorPreferencesService.Current.RuntimeEnvironment,
            standaloneWindow: true);
        ImGui.End();
    }

    private static void DrawRuntimeEnvironmentPreferences(
        EditorRuntimeEnvironmentPreferences preferences,
        bool standaloneWindow = false)
    {
        if (!standaloneWindow)
        {
            if (ImGui.Button("Open Runtime Environment Panel"))
                _showRuntimeEnvironment = true;
            ImGui.SameLine();
            ImGui.TextDisabled("Opens the full resizable environment-variable browser window.");
        }

        ImGui.TextWrapped(
            "Temporary runtime overrides affect this process only. Clearing an override restores the launch value; " +
            "saving editor preferences never persists validation, diagnostics, or feature downgrades.");

        float searchWidth = standaloneWindow ? 320.0f : 200.0f;
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint(
            "##RuntimeEnvironmentSearch",
            "Search variable, field, category, or value...",
            ref _runtimeEnvironmentSearch,
            256);
        if (!string.IsNullOrEmpty(_runtimeEnvironmentSearch))
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear##RuntimeEnvironmentSearchClear"))
                _runtimeEnvironmentSearch = string.Empty;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150.0f);
        ImGui.Combo(
            "##RuntimeEnvironmentCategory",
            ref _runtimeEnvironmentCategoryFilterIndex,
            RuntimeEnvironmentCategoryLabels,
            RuntimeEnvironmentCategoryLabels.Length);

        ImGui.SameLine();
        ImGui.Checkbox("Only set/overridden", ref _runtimeEnvironmentOnlyActive);
        ImGui.SameLine();
        ImGui.Checkbox("Only overrides", ref _runtimeEnvironmentOnlyOverrides);
        ImGui.SameLine();
        ImGui.Checkbox("Only unsupported", ref _runtimeEnvironmentOnlyUnsupported);

        ImGui.SameLine();
        if (ImGui.Button("Refresh Process Values"))
        {
            EditorPreferencesService.Current.RefreshRuntimeEnvironment();
            RuntimeEnvironmentEditBuffers.Clear();
            _runtimeEnvironmentStatus = "Refreshed values changed outside the runtime settings facade.";
        }

        DrawRuntimeEnvironmentRestartActions();

        int overrideCount = 0;
        int activeCount = 0;
        int unsupportedCount = 0;
        int visibleCount = 0;
        string search = _runtimeEnvironmentSearch.Trim();
        RuntimeEnvironmentCategory? categoryFilter = _runtimeEnvironmentCategoryFilterIndex > 0 &&
            _runtimeEnvironmentCategoryFilterIndex <= RuntimeEnvironmentCategories.Length
            ? RuntimeEnvironmentCategories[_runtimeEnvironmentCategoryFilterIndex - 1]
            : null;

        IReadOnlyList<RuntimeEnvironmentVariableDescriptor> descriptors = preferences.Variables;
        for (int i = 0; i < descriptors.Count; i++)
        {
            RuntimeEnvironmentVariableDescriptor descriptor = descriptors[i];
            RuntimeEnvironmentVariableState state = XREnvironment.GetState(descriptor.Name);
            bool hasUnsupported = HasUnsupportedValue(descriptor, state);
            if (state.HasRuntimeOverride)
                overrideCount++;
            if (state.HasRuntimeOverride || state.EffectiveValue is not null)
                activeCount++;
            if (hasUnsupported)
                unsupportedCount++;
            if (MatchesRuntimeEnvironmentFilters(descriptor, state, hasUnsupported, search, categoryFilter))
                visibleCount++;
        }

        ImGui.TextDisabled(
            $"Showing {visibleCount} / {descriptors.Count} variables ({activeCount} set, {overrideCount} runtime override(s)).");
        if (unsupportedCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(
                RuntimeEnvironmentUnsupportedColor,
                $"{unsupportedCount} unsupported value(s)");
        }

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
        float tableHeight = standaloneWindow
            ? MathF.Max(260.0f, ImGui.GetContentRegionAvail().Y)
            : MathF.Max(320.0f, MathF.Min(560.0f, ImGui.GetContentRegionAvail().Y));
        if (!ImGui.BeginTable(
                "RuntimeEnvironmentVariables",
                6,
                flags,
                new Vector2(0.0f, tableHeight)))
        {
            return;
        }

        try
        {
            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 95.0f);
            ImGui.TableSetupColumn("Variable", ImGuiTableColumnFlags.WidthFixed, 320.0f);
            ImGui.TableSetupColumn("Launch", ImGuiTableColumnFlags.WidthFixed, 145.0f);
            ImGui.TableSetupColumn("Value / Override", ImGuiTableColumnFlags.WidthFixed, 330.0f);
            ImGui.TableSetupColumn("Effective", ImGuiTableColumnFlags.WidthFixed, 180.0f);
            ImGui.TableSetupColumn("Applies", ImGuiTableColumnFlags.WidthFixed, 165.0f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < descriptors.Count; i++)
            {
                RuntimeEnvironmentVariableDescriptor descriptor = descriptors[i];
                RuntimeEnvironmentVariableState state = XREnvironment.GetState(descriptor.Name);
                bool hasUnsupported = HasUnsupportedValue(descriptor, state);
                if (!MatchesRuntimeEnvironmentFilters(descriptor, state, hasUnsupported, search, categoryFilter))
                    continue;

                DrawRuntimeEnvironmentVariableRow(descriptor, state);
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

    private static void DrawRuntimeEnvironmentVariableRow(
        RuntimeEnvironmentVariableDescriptor descriptor,
        RuntimeEnvironmentVariableState state)
    {
        bool launchSupported = descriptor.IsValueSupported(state.LaunchValue);
        bool effectiveSupported = descriptor.IsValueSupported(state.EffectiveValue);

        ImGui.PushID(descriptor.Name);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(descriptor.Category.ToString());

        ImGui.TableSetColumnIndex(1);
        if (!effectiveSupported)
            ImGui.TextColored(RuntimeEnvironmentUnsupportedColor, descriptor.Name);
        else if (state.HasRuntimeOverride)
            ImGui.TextColored(RuntimeEnvironmentOverrideColor, descriptor.Name);
        else
            ImGui.TextUnformatted(descriptor.Name);

        if (ImGui.IsItemHovered())
        {
            string supportedLine = descriptor.AllowedValues.Count > 0 ||
                descriptor.ValueKind is RuntimeEnvironmentValueKind.Boolean or RuntimeEnvironmentValueKind.Integer or RuntimeEnvironmentValueKind.Number
                ? $"\nSupported: {descriptor.FormatSupportedValuesSummary()}"
                : string.Empty;
            ImGui.SetTooltip(
                $"{descriptor.FieldName}\n{descriptor.DefaultBehavior}\n" +
                $"Kind: {descriptor.ValueKind}; apply: {FormatApplyMode(descriptor.ApplyMode)}{supportedLine}");
        }

        ImGui.TableSetColumnIndex(2);
        DrawRuntimeEnvironmentValue(descriptor, state.LaunchValue, isHighlighted: false, isSupported: launchSupported);

        ImGui.TableSetColumnIndex(3);
        if (descriptor.AllowedValues.Count > 0)
            DrawRuntimeEnvironmentEnumEditor(descriptor, state, effectiveSupported);
        else if (descriptor.ValueKind == RuntimeEnvironmentValueKind.Boolean)
            DrawRuntimeEnvironmentBooleanEditor(descriptor, state, effectiveSupported);
        else
            DrawRuntimeEnvironmentTextEditor(descriptor, state, effectiveSupported);

        ImGui.TableSetColumnIndex(4);
        DrawRuntimeEnvironmentValue(
            descriptor,
            state.EffectiveValue,
            isHighlighted: state.HasRuntimeOverride,
            isSupported: effectiveSupported);

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

    private static void DrawRuntimeEnvironmentEnumEditor(
        RuntimeEnvironmentVariableDescriptor descriptor,
        RuntimeEnvironmentVariableState state,
        bool effectiveSupported)
    {
        string? effective = state.EffectiveValue;
        string preview;
        if (string.IsNullOrWhiteSpace(effective))
            preview = "<unset (default)>";
        else if (!effectiveSupported)
            preview = $"Unsupported: {effective.Trim()}";
        else
            preview = effective.Trim();

        ImGui.SetNextItemWidth(220.0f);
        if (!effectiveSupported)
            ImGui.PushStyleColor(ImGuiCol.Text, RuntimeEnvironmentUnsupportedColor);

        bool comboOpen = ImGui.BeginCombo("##EnumValue", preview);
        if (!effectiveSupported)
            ImGui.PopStyleColor();

        if (!effectiveSupported && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Unsupported value '{effective}'.\nSupported values: {descriptor.FormatSupportedValuesSummary()}");
        }

        if (comboOpen)
        {
            bool isUnset = string.IsNullOrWhiteSpace(effective);
            if (ImGui.Selectable("<unset (default)>", isUnset))
            {
                if (state.LaunchValue is null)
                    ClearRuntimeEnvironmentOverride(descriptor);
                else
                    SetRuntimeEnvironmentOverride(descriptor, null);
            }

            if (isUnset)
                ImGui.SetItemDefaultFocus();

            IReadOnlyList<string> allowedValues = descriptor.AllowedValues;
            for (int i = 0; i < allowedValues.Count; i++)
            {
                string option = allowedValues[i];
                bool selected = !isUnset &&
                    effective!.Trim().Equals(option, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(option, selected))
                    SetRuntimeEnvironmentOverride(descriptor, option);
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (state.HasRuntimeOverride)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"))
                ClearRuntimeEnvironmentOverride(descriptor);
        }
        else if (!effectiveSupported)
        {
            ImGui.SameLine();
            ImGui.TextColored(RuntimeEnvironmentUnsupportedColor, "[invalid]");
        }
    }

    private static void DrawRuntimeEnvironmentBooleanEditor(
        RuntimeEnvironmentVariableDescriptor descriptor,
        RuntimeEnvironmentVariableState state,
        bool effectiveSupported)
    {
        bool enabled = state.IsEnabled();
        if (ImGui.Checkbox("##Value", ref enabled))
            SetRuntimeEnvironmentOverride(descriptor, enabled ? "1" : "0");

        if (!effectiveSupported)
        {
            ImGui.SameLine();
            ImGui.TextColored(RuntimeEnvironmentUnsupportedColor, "[unsupported]");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Unsupported value '{state.EffectiveValue}'.\nSupported values: {descriptor.FormatSupportedValuesSummary()}");
            }
        }

        if (state.HasRuntimeOverride)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"))
                ClearRuntimeEnvironmentOverride(descriptor);
            ImGui.SameLine();
            ImGui.TextDisabled("override");
            return;
        }

        if (state.EffectiveValue is null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Force Off"))
                SetRuntimeEnvironmentOverride(descriptor, "0");
            ImGui.SameLine();
            ImGui.TextDisabled("default");
            return;
        }

        if (effectiveSupported)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("launch");
        }
    }

    private static void DrawRuntimeEnvironmentTextEditor(
        RuntimeEnvironmentVariableDescriptor descriptor,
        RuntimeEnvironmentVariableState state,
        bool effectiveSupported)
    {
        if (!RuntimeEnvironmentEditBuffers.TryGetValue(descriptor.Name, out string? buffer))
        {
            buffer = state.HasRuntimeOverride
                ? state.RuntimeOverrideValue ?? string.Empty
                : state.EffectiveValue ?? string.Empty;
        }

        ImGui.SetNextItemWidth(175.0f);
        ImGuiInputTextFlags inputFlags = descriptor.ValueKind == RuntimeEnvironmentValueKind.Secret
            ? ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue
            : ImGuiInputTextFlags.EnterReturnsTrue;
        if (!effectiveSupported)
            ImGui.PushStyleColor(ImGuiCol.Text, RuntimeEnvironmentUnsupportedColor);

        bool submitted = ImGui.InputText("##Value", ref buffer, 4096, inputFlags);
        if (!effectiveSupported)
            ImGui.PopStyleColor();

        RuntimeEnvironmentEditBuffers[descriptor.Name] = buffer;

        if (!effectiveSupported && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Unsupported value '{state.EffectiveValue}'.\nExpected: {descriptor.FormatSupportedValuesSummary()}");
        }

        ImGui.SameLine();
        if (submitted || ImGui.SmallButton("Set"))
        {
            string? targetValue = string.IsNullOrEmpty(buffer) ? null : buffer;
            if (!state.HasRuntimeOverride || !string.Equals(targetValue, state.RuntimeOverrideValue, StringComparison.Ordinal))
                SetRuntimeEnvironmentOverride(descriptor, targetValue);
        }

        if (state.EffectiveValue is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Unset"))
            {
                RuntimeEnvironmentEditBuffers[descriptor.Name] = string.Empty;
                SetRuntimeEnvironmentOverride(descriptor, null);
            }
        }

        if (state.HasRuntimeOverride)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"))
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
            if (!descriptor.IsValueSupported(value))
            {
                _runtimeEnvironmentStatus =
                    $"{descriptor.Name} set to unsupported value '{MaskIfSensitive(value ?? string.Empty, descriptor.ValueKind)}'. " +
                    $"Expected: {descriptor.FormatSupportedValuesSummary()}.";
            }
            else
            {
                _runtimeEnvironmentStatus =
                    $"{descriptor.Name} now {(value is null ? "explicitly unset" : $"overridden to '{MaskIfSensitive(value, descriptor.ValueKind)}")}.";
            }
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

    private static bool HasUnsupportedValue(
        RuntimeEnvironmentVariableDescriptor descriptor,
        RuntimeEnvironmentVariableState state)
        => !descriptor.IsValueSupported(state.EffectiveValue) ||
           !descriptor.IsValueSupported(state.LaunchValue);

    private static bool MatchesRuntimeEnvironmentFilters(
        RuntimeEnvironmentVariableDescriptor descriptor,
        RuntimeEnvironmentVariableState state,
        bool hasUnsupported,
        string search,
        RuntimeEnvironmentCategory? categoryFilter)
    {
        if (categoryFilter.HasValue && descriptor.Category != categoryFilter.Value)
            return false;

        if (_runtimeEnvironmentOnlyUnsupported && !hasUnsupported)
            return false;

        if (_runtimeEnvironmentOnlyOverrides && !state.HasRuntimeOverride)
            return false;

        if (_runtimeEnvironmentOnlyActive && !state.HasRuntimeOverride && state.EffectiveValue is null)
            return false;

        if (string.IsNullOrWhiteSpace(search))
            return true;

        if (descriptor.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            descriptor.FieldName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            descriptor.Category.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (int i = 0; i < descriptor.AllowedValues.Count; i++)
            if (descriptor.AllowedValues[i].Contains(search, StringComparison.OrdinalIgnoreCase))
                return true;

        if (descriptor.ValueKind != RuntimeEnvironmentValueKind.Secret)
        {
            if (state.EffectiveValue is not null &&
                state.EffectiveValue.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (state.LaunchValue is not null &&
                state.LaunchValue.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void DrawRuntimeEnvironmentValue(
        RuntimeEnvironmentVariableDescriptor descriptor,
        string? value,
        bool isHighlighted,
        bool isSupported)
    {
        if (value is null)
        {
            ImGui.TextDisabled("<unset>");
            return;
        }

        string display = MaskIfSensitive(value, descriptor.ValueKind);
        if (!isSupported)
        {
            ImGui.TextColored(RuntimeEnvironmentUnsupportedColor, $"{display} [unsupported]");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Unsupported value for {descriptor.Name}.\nSupported: {descriptor.FormatSupportedValuesSummary()}");
            }

            return;
        }

        if (isHighlighted)
            ImGui.TextColored(RuntimeEnvironmentActiveColor, display);
        else
            ImGui.TextUnformatted(display);

        if (descriptor.ValueKind == RuntimeEnvironmentValueKind.Secret && ImGui.IsItemHovered())
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

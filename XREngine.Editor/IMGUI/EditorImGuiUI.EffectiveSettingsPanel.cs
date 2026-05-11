using ImGuiNET;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const string GlobalEngineDefaultsInspectorTitle = "Global Engine Defaults";
    private const string ProjectEngineDefaultsInspectorTitle = "Project Engine Defaults";
    private static readonly EffectiveSettingsInspectorTarget EffectiveSettingsTarget = new();
    private static EffectiveSettingsRow[]? _effectiveSettingsRows;

    private sealed class EffectiveSettingsInspectorTarget { }
    private sealed record EffectiveSettingsRow(string Name, PropertyInfo? EffectiveProperty, PropertyInfo? EngineSettingsProperty);
    private readonly record struct EffectiveSettingCascadeValues(
        string GlobalDefault,
        string ProjectDefault,
        string GameSetting,
        string UserSetting,
        string EditorOverride);

    private static void OpenEffectiveSettingsInInspector()
    {
        _showInspector = true;
        _inspectorAssetContext = null;
        SetInspectorStandaloneTarget(EffectiveSettingsTarget, "Effective Settings");
    }

    private static bool IsEngineDefaultsTarget(object? target)
        => target is Engine.Rendering.EngineSettings;

    private static void DrawEngineDefaultsInspectorNote(Engine.Rendering.EngineSettings settings)
    {
        if (ReferenceEquals(settings, Engine.Rendering.ProjectDefaultSettings) && Engine.CurrentProject is not null)
        {
            ImGui.TextDisabled("Project engine defaults override the global engine defaults for this project.");
            if (ImGui.Button("Save Project Engine Defaults"))
                Engine.SaveProjectEngineDefaults();
            ImGui.SameLine();
            ImGui.TextDisabled($"(Project: {Engine.CurrentProject.ProjectName})");
        }
        else if (ReferenceEquals(settings, Engine.Rendering.GlobalDefaultSettings))
        {
            ImGui.TextDisabled("Global engine defaults are saved outside any project and seed new project defaults.");
            if (ImGui.Button("Save Global Engine Defaults"))
                Engine.SaveGlobalEngineDefaults();
        }
        else
        {
            ImGui.TextDisabled("Engine defaults asset.");
        }

        ImGui.TextDisabled("Game Settings and User Settings can still override the effective values that use the cascading settings layer.");
        ImGui.Separator();
    }

    private static void DrawEffectiveSettingsInspector()
    {
        ImGui.TextDisabled("Read-only resolved runtime values after applying editor, user, game, project-default, and global-default layers.");
        ImGui.TextDisabled("Change the owning source asset to persist a value.");
        ImGui.Separator();

        EffectiveSettingsRow[] rows = GetEffectiveSettingsRows();
        string search = _inspectorPropertySearch ?? string.Empty;
        bool hasSearch = !string.IsNullOrWhiteSpace(search);

        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollX |
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("EffectiveSettingsTable", 9, flags))
            return;

        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 110.0f);
        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthFixed, 230.0f);
        ImGui.TableSetupColumn("Effective", ImGuiTableColumnFlags.WidthFixed, 160.0f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 150.0f);
        ImGui.TableSetupColumn("Global Default", ImGuiTableColumnFlags.WidthFixed, 160.0f);
        ImGui.TableSetupColumn("Project Default", ImGuiTableColumnFlags.WidthFixed, 160.0f);
        ImGui.TableSetupColumn("Game Settings", ImGuiTableColumnFlags.WidthFixed, 170.0f);
        ImGui.TableSetupColumn("User Settings", ImGuiTableColumnFlags.WidthFixed, 190.0f);
        ImGui.TableSetupColumn("Editor Override", ImGuiTableColumnFlags.WidthFixed, 170.0f);
        ImGui.TableHeadersRow();

        foreach (EffectiveSettingsRow row in rows)
        {
            string category = GetEffectiveSettingCategory(row);
            string displayName = FormatEffectiveSettingName(row.Name);
            string source = ResolveEffectiveSettingSource(row);
            EffectiveSettingCascadeValues cascade = ResolveEffectiveSettingCascade(row.Name);
            string valueText = TryReadEffectiveSettingValue(row, out object? value)
                ? FormatEffectiveSettingValue(value)
                : "<error>";

            if (hasSearch &&
                !ContainsIgnoreCase(category, search) &&
                !ContainsIgnoreCase(displayName, search) &&
                !ContainsIgnoreCase(row.Name, search) &&
                !ContainsIgnoreCase(valueText, search) &&
                !ContainsIgnoreCase(source, search) &&
                !ContainsIgnoreCase(cascade.GlobalDefault, search) &&
                !ContainsIgnoreCase(cascade.ProjectDefault, search) &&
                !ContainsIgnoreCase(cascade.GameSetting, search) &&
                !ContainsIgnoreCase(cascade.UserSetting, search) &&
                !ContainsIgnoreCase(cascade.EditorOverride, search))
            {
                continue;
            }

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(category);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(displayName);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(valueText);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(source);
            ImGui.TableSetColumnIndex(4);
            DrawEffectiveSettingsTableCell(cascade.GlobalDefault);
            ImGui.TableSetColumnIndex(5);
            DrawEffectiveSettingsTableCell(cascade.ProjectDefault);
            ImGui.TableSetColumnIndex(6);
            DrawEffectiveSettingsTableCell(cascade.GameSetting);
            ImGui.TableSetColumnIndex(7);
            DrawEffectiveSettingsTableCell(cascade.UserSetting);
            ImGui.TableSetColumnIndex(8);
            DrawEffectiveSettingsTableCell(cascade.EditorOverride);
        }

        ImGui.EndTable();
    }

    private static void DrawEffectiveSettingsTableCell(string value)
    {
        ImGui.TextUnformatted(value);
        if (ImGui.IsItemHovered() && value.Length > 0)
            ImGui.SetTooltip(value);
    }

    private static EffectiveSettingsRow[] GetEffectiveSettingsRows()
    {
        if (_effectiveSettingsRows is not null)
            return _effectiveSettingsRows;

        Dictionary<string, PropertyInfo> effectiveProperties = typeof(Engine.EffectiveSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(static p => p.GetMethod is not null && p.GetIndexParameters().Length == 0)
            .ToDictionary(static p => p.Name, StringComparer.Ordinal);

        Dictionary<string, PropertyInfo> engineSettingsProperties = typeof(Engine.Rendering.EngineSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.DeclaringType == typeof(Engine.Rendering.EngineSettings))
            .Where(static p => p.GetMethod is not null && p.GetIndexParameters().Length == 0)
            .ToDictionary(static p => p.Name, StringComparer.Ordinal);

        _effectiveSettingsRows = effectiveProperties.Keys
            .Concat(engineSettingsProperties.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(name =>
            {
                effectiveProperties.TryGetValue(name, out PropertyInfo? effectiveProperty);
                engineSettingsProperties.TryGetValue(name, out PropertyInfo? engineSettingsProperty);
                return new EffectiveSettingsRow(name, effectiveProperty, engineSettingsProperty);
            })
            .OrderBy(static row => GetEffectiveSettingCategoryOrder(row))
            .ThenBy(static row => row.Name, StringComparer.Ordinal)
            .ToArray();

        return _effectiveSettingsRows;
    }

    private static bool TryReadEffectiveSettingValue(EffectiveSettingsRow row, out object? value)
    {
        if (row.EffectiveProperty is not null)
            return TryReadPropertyValue(row.EffectiveProperty, null, out value);

        if (row.EngineSettingsProperty is not null)
            return TryReadPropertyValue(row.EngineSettingsProperty, Engine.Rendering.Settings, out value);

        value = null;
        return false;
    }

    private static bool TryReadPropertyValue(PropertyInfo property, object? owner, out object? value)
    {
        try
        {
            value = property.GetValue(owner);
            return true;
        }
        catch (Exception ex)
        {
            value = ex.GetType().Name;
            return false;
        }
    }

    private static string ResolveEffectiveSettingSource(EffectiveSettingsRow row)
    {
        if (row.EffectiveProperty is null && row.EngineSettingsProperty is not null)
            return EngineDefaultSourceName();

        return ResolveEffectiveSettingSource(row.Name);
    }

    private static string ResolveEffectiveSettingSource(string settingName)
    {
        string overridePropertyName = settingName + "Override";

        if (TryResolveEnvironmentSettingSource(settingName, out string environmentSource))
            return environmentSource;

        if (IsZeroReadbackEditorDebugSource(settingName))
            return "Editor Preferences";

        if (TryGetActiveOverride(Engine.EditorPreferencesOverrides, overridePropertyName))
            return "Editor Override";

        if (TryGetActiveOverride(Engine.UserSettings, overridePropertyName))
            return "User Settings";

        if (TryGetActiveOverride(Engine.GameSettings, overridePropertyName))
            return "Game Settings";

        return settingName switch
        {
            nameof(Engine.EffectiveSettings.GPURenderDispatch) => Engine.GameSettings is null ? "Built-in Default" : "Game Settings",
            nameof(Engine.EffectiveSettings.TargetUpdatesPerSecond) => Engine.GameSettings is null ? "No Project Value" : "Game Settings",
            nameof(Engine.EffectiveSettings.TargetFramesPerSecond) => Engine.GameSettings is null ? "No Project Value" : "Game Settings",
            nameof(Engine.EffectiveSettings.FixedFramesPerSecond) => Engine.GameSettings is null ? "Built-in Default" : "Game Settings",
            nameof(Engine.EffectiveSettings.UnfocusedTargetFramesPerSecond) => ResolveUnfocusedTargetFrameSource(),
            _ when IsUserPrimaryEffectiveSetting(settingName) && HasReadableProperty(Engine.UserSettings, settingName) => "User Settings",
            _ => EngineDefaultSourceName(),
        };
    }

    private static string EngineDefaultSourceName()
        => Engine.Rendering.ProjectDefaultSettings is not null ? "Project Engine Defaults" : "Global Engine Defaults";

    private static string ResolveUnfocusedTargetFrameSource()
    {
        if (TryGetActiveOverride(Engine.UserSettings, nameof(UserSettings.UnfocusedTargetFramesPerSecondOverride)))
            return "User Settings";

        if (TryGetPropertyValue(Engine.GameSettings, nameof(GameStartupSettings.UnfocusedTargetFramesPerSecond), out object? projectValue) &&
            projectValue is not null)
        {
            return "Game Settings";
        }

        return ResolveEffectiveSettingSource(nameof(Engine.EffectiveSettings.TargetFramesPerSecond));
    }

    private static EffectiveSettingCascadeValues ResolveEffectiveSettingCascade(string settingName)
    {
        string overridePropertyName = settingName + "Override";

        return new EffectiveSettingCascadeValues(
            GlobalDefault: ResolveGlobalEngineDefaultValue(settingName),
            ProjectDefault: ResolveProjectEngineDefaultValue(settingName),
            GameSetting: ResolveGameSettingValue(settingName, overridePropertyName),
            UserSetting: ResolveUserSettingValue(settingName, overridePropertyName),
            EditorOverride: ResolveEditorOverrideValue(settingName, overridePropertyName));
    }

    private static string ResolveGlobalEngineDefaultValue(string settingName)
        => TryFormatReadablePropertyValue(Engine.Rendering.GlobalDefaultSettings, settingName, out string value)
            ? value
            : "-";

    private static string ResolveProjectEngineDefaultValue(string settingName)
    {
        Engine.Rendering.EngineSettings? projectDefaults = Engine.Rendering.ProjectDefaultSettings;
        if (projectDefaults is null)
            return "<none>";

        return TryFormatReadablePropertyValue(projectDefaults, settingName, out string value)
            ? value
            : "-";
    }

    private static string ResolveGameSettingValue(string settingName, string overridePropertyName)
    {
        if (TryFormatOverrideableSettingValue(Engine.GameSettings, overridePropertyName, out string overrideValue))
            return overrideValue;

        return TryFormatReadablePropertyValue(Engine.GameSettings, settingName, out string value)
            ? value
            : "-";
    }

    private static string ResolveUserSettingValue(string settingName, string overridePropertyName)
    {
        bool hasOverrideProperty = TryFormatOverrideableSettingValue(Engine.UserSettings, overridePropertyName, out string overrideValue);

        if (IsUserPrimaryEffectiveSetting(settingName) &&
            TryFormatReadablePropertyValue(Engine.UserSettings, settingName, out string primaryValue))
        {
            return hasOverrideProperty
                ? $"Preference: {primaryValue}; {overrideValue}"
                : primaryValue;
        }

        return hasOverrideProperty ? overrideValue : "-";
    }

    private static string ResolveEditorOverrideValue(string settingName, string overridePropertyName)
    {
        if (TryFormatOverrideableSettingValue(Engine.EditorPreferencesOverrides, overridePropertyName, out string overrideValue))
            return overrideValue;

        if (TryFormatZeroReadbackEditorDebugValue(settingName, out string debugValue))
            return debugValue;

        return "-";
    }

    private static bool TryFormatZeroReadbackEditorDebugValue(string settingName, out string value)
    {
        value = string.Empty;

        if (!IsZeroReadbackEditorDebugSource(settingName))
            return false;

        if (!TryFormatReadablePropertyValue(Engine.EditorPreferences?.Debug, settingName, out string debugValue))
            return false;

        value = $"Debug: {debugValue}";
        return true;
    }

    private static bool TryResolveEnvironmentSettingSource(string settingName, out string source)
    {
        return settingName switch
        {
            nameof(Engine.EffectiveSettings.ZeroReadbackMaterialDrawPath)
                => TryResolveEnvironmentEnumSource<EZeroReadbackMaterialDrawPath>("XRE_ZERO_READBACK_MATERIAL_DRAW_PATH", out source),
            nameof(Engine.EffectiveSettings.ForceMeshSubmissionStrategy)
                => TryResolveEnvironmentEnumSource<EMeshSubmissionStrategy>("XRE_FORCE_MESH_SUBMISSION_STRATEGY", out source),
            _ => NoEnvironmentSource(out source),
        };
    }

    private static bool TryResolveEnvironmentEnumSource<TEnum>(string variableName, out string source)
        where TEnum : struct, Enum
    {
        source = string.Empty;
        string? raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw) ||
            !Enum.TryParse(raw.Trim(), ignoreCase: true, out TEnum _))
        {
            return false;
        }

        source = "Environment";
        return true;
    }

    private static bool NoEnvironmentSource(out string source)
    {
        source = string.Empty;
        return false;
    }

    private static bool IsZeroReadbackEditorDebugSource(string settingName)
    {
        if (settingName != nameof(Engine.EffectiveSettings.ZeroReadbackMaterialDrawPath))
            return false;

        EditorDebugOptions? editorDebug = Engine.EditorPreferences?.Debug;
        return editorDebug is not null &&
            (editorDebug.EnableZeroReadbackMaterialScatter ||
             editorDebug.ZeroReadbackMaterialDrawPath != EZeroReadbackMaterialDrawPath.FullBucketScan);
    }

    private static bool IsUserPrimaryEffectiveSetting(string settingName)
        => settingName is
            nameof(Engine.EffectiveSettings.VSync) or
            nameof(Engine.EffectiveSettings.GlobalIlluminationMode) or
            nameof(Engine.EffectiveSettings.AudioTransport) or
            nameof(Engine.EffectiveSettings.AudioEffects) or
            nameof(Engine.EffectiveSettings.AudioArchitectureV2) or
            nameof(Engine.EffectiveSettings.AudioSampleRate);

    private static bool TryGetActiveOverride(object? owner, string propertyName)
    {
        if (!TryGetPropertyValue(owner, propertyName, out object? candidate))
            return false;

        return candidate is IOverrideableSetting { HasOverride: true };
    }

    private static bool TryFormatOverrideableSettingValue(object? owner, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetPropertyValue(owner, propertyName, out object? candidate) || candidate is not IOverrideableSetting setting)
            return false;

        value = setting.HasOverride
            ? $"Override: {FormatEffectiveSettingValue(setting.BoxedValue)}"
            : "No override";
        return true;
    }

    private static bool TryFormatReadablePropertyValue(object? owner, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetPropertyValue(owner, propertyName, out object? candidate))
            return false;

        value = FormatEffectiveSettingValue(candidate);
        return true;
    }

    private static bool HasReadableProperty(object? owner, string propertyName)
        => owner?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetMethod is not null;

    private static bool TryGetPropertyValue(object? owner, string propertyName, out object? value)
    {
        value = null;
        if (owner is null)
            return false;

        PropertyInfo? property = owner.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetMethod is null)
            return false;

        try
        {
            value = property.GetValue(owner);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetEffectiveSettingCategory(EffectiveSettingsRow row)
    {
        if (row.EffectiveProperty is not null)
            return GetEffectiveSettingCategory(row.Name);

        CategoryAttribute? category = row.EngineSettingsProperty?.GetCustomAttribute<CategoryAttribute>(true);
        return string.IsNullOrWhiteSpace(category?.Category)
            ? "Engine Defaults"
            : category.Category;
    }

    private static string GetEffectiveSettingCategory(string settingName)
        => settingName switch
        {
            nameof(Engine.EffectiveSettings.JobWorkers) or
            nameof(Engine.EffectiveSettings.JobWorkerCap) or
            nameof(Engine.EffectiveSettings.JobQueueLimit) or
            nameof(Engine.EffectiveSettings.JobQueueWarningThreshold) => "Threading",

            nameof(Engine.EffectiveSettings.TickGroupedItemsInParallel) or
            nameof(Engine.EffectiveSettings.TargetUpdatesPerSecond) or
            nameof(Engine.EffectiveSettings.TargetFramesPerSecond) or
            nameof(Engine.EffectiveSettings.UnfocusedTargetFramesPerSecond) or
            nameof(Engine.EffectiveSettings.FixedFramesPerSecond) => "Performance",

            nameof(Engine.EffectiveSettings.AllowShaderPipelines) or
            nameof(Engine.EffectiveSettings.AllowSkinning) or
            nameof(Engine.EffectiveSettings.UseIntegerWeightingIds) or
            nameof(Engine.EffectiveSettings.RecalcChildMatricesLoopType) or
            nameof(Engine.EffectiveSettings.SkinnedBoundsRecomputePolicy) or
            nameof(Engine.EffectiveSettings.AllowInitialSkinnedBoundsBuildWhenNever) or
            nameof(Engine.EffectiveSettings.CalculateSkinningInComputeShader) or
            nameof(Engine.EffectiveSettings.CalculateBlendshapesInComputeShader) or
            nameof(Engine.EffectiveSettings.UseDetailPreservingComputeMipmaps) => "Technical",

            nameof(Engine.EffectiveSettings.TransformReplicationKeyframeIntervalSec) or
            nameof(Engine.EffectiveSettings.TimeBetweenReplications) => "Networking",

            nameof(Engine.EffectiveSettings.OutputVerbosity) => "Debug",

            nameof(Engine.EffectiveSettings.AudioTransport) or
            nameof(Engine.EffectiveSettings.AudioEffects) or
            nameof(Engine.EffectiveSettings.AudioArchitectureV2) or
            nameof(Engine.EffectiveSettings.AudioSampleRate) => "Audio",

            _ => "Rendering",
        };

    private static int GetEffectiveSettingCategoryOrder(EffectiveSettingsRow row)
        => GetCategoryOrder(GetEffectiveSettingCategory(row));

    private static int GetCategoryOrder(string category)
        => category switch
        {
            "Threading" => 0,
            "Rendering" => 1,
            "Performance" => 2,
            "Technical" => 3,
            "Networking" => 4,
            "Debug" => 5,
            "Audio" => 6,
            "GPU Rendering" => 7,
            "Vulkan" => 8,
            "VR" => 9,
            "Physics" => 10,
            _ => 20,
        };

    private static string FormatEffectiveSettingValue(object? value)
        => value switch
        {
            null => "<null>",
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };

    private static string FormatEffectiveSettingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var builder = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 &&
                char.IsUpper(c) &&
                (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                builder.Append(' ');
            }

            builder.Append(c);
        }

        string result = builder.ToString();
        foreach (KeyValuePair<string, string> replacement in EffectiveSettingDisplayReplacements)
            result = result.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);

        return result;
    }

    private static bool ContainsIgnoreCase(string value, string search)
        => value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

    private static readonly Dictionary<string, string> EffectiveSettingDisplayReplacements = new(StringComparer.Ordinal)
    {
        ["G P U"] = "GPU",
        ["B V H"] = "BVH",
        ["D L S S"] = "DLSS",
        ["X E S S"] = "XeSS",
        ["M S A A"] = "MSAA",
        ["V Sync"] = "VSync",
        ["Vulkan Gpu"] = "Vulkan GPU",
        ["Gpu "] = "GPU ",
        ["Bvh"] = "BVH",
        ["Dlss"] = "DLSS",
        ["Msaa"] = "MSAA",
        ["Xess"] = "XeSS",
    };
}

using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using XREngine;
using XREngine.Data.Core;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const string RuntimeEngineDefaultsInspectorTitle = "Runtime Engine Defaults (Session)";
    private static readonly EffectiveSettingsInspectorTarget EffectiveSettingsTarget = new();
    private static PropertyInfo[]? _effectiveSettingsProperties;

    private sealed class EffectiveSettingsInspectorTarget { }

    private static void OpenEffectiveSettingsInInspector()
    {
        _showInspector = true;
        _inspectorAssetContext = null;
        SetInspectorStandaloneTarget(EffectiveSettingsTarget, "Effective Settings");
    }

    private static bool IsRuntimeEngineDefaultsTarget(object? target)
        => target is Engine.Rendering.EngineSettings;

    private static void DrawRuntimeEngineDefaultsInspectorNote()
    {
        ImGui.TextDisabled("Runtime baseline. Not saved.");
        ImGui.TextDisabled("Persist project/user overrides in Game Settings and User Settings; editor-only project overrides live in Editor Preferences Overrides.");
        ImGui.Separator();
    }

    private static void DrawEffectiveSettingsInspector()
    {
        ImGui.TextDisabled("Read-only resolved runtime values after applying editor, user, project, and engine-default layers.");
        ImGui.TextDisabled("Change the owning source asset to persist a value.");
        ImGui.Separator();

        PropertyInfo[] properties = GetEffectiveSettingsProperties();
        string search = _inspectorPropertySearch ?? string.Empty;
        bool hasSearch = !string.IsNullOrWhiteSpace(search);

        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("EffectiveSettingsTable", 4, flags))
            return;

        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 130.0f);
        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthStretch, 0.34f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.36f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 145.0f);
        ImGui.TableHeadersRow();

        foreach (PropertyInfo property in properties)
        {
            string category = GetEffectiveSettingCategory(property.Name);
            string displayName = FormatEffectiveSettingName(property.Name);
            string source = ResolveEffectiveSettingSource(property.Name);
            string valueText = TryReadEffectiveSettingValue(property, out object? value)
                ? FormatEffectiveSettingValue(value)
                : "<error>";

            if (hasSearch &&
                !ContainsIgnoreCase(category, search) &&
                !ContainsIgnoreCase(displayName, search) &&
                !ContainsIgnoreCase(property.Name, search) &&
                !ContainsIgnoreCase(valueText, search) &&
                !ContainsIgnoreCase(source, search))
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
        }

        ImGui.EndTable();
    }

    private static PropertyInfo[] GetEffectiveSettingsProperties()
    {
        return _effectiveSettingsProperties ??= typeof(Engine.EffectiveSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(static p => p.GetMethod is not null && p.GetIndexParameters().Length == 0)
            .OrderBy(static p => GetEffectiveSettingCategoryOrder(p.Name))
            .ThenBy(static p => p.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryReadEffectiveSettingValue(PropertyInfo property, out object? value)
    {
        try
        {
            value = property.GetValue(null);
            return true;
        }
        catch (Exception ex)
        {
            value = ex.GetType().Name;
            return false;
        }
    }

    private static string ResolveEffectiveSettingSource(string settingName)
    {
        string overridePropertyName = settingName + "Override";

        if (TryGetActiveOverride(Engine.EditorPreferencesOverrides, overridePropertyName))
            return "Editor Override";

        if (TryGetActiveOverride(Engine.UserSettings, overridePropertyName))
            return "User Settings";

        if (TryGetActiveOverride(Engine.GameSettings, overridePropertyName))
            return "Project Settings";

        return settingName switch
        {
            nameof(Engine.EffectiveSettings.GPURenderDispatch) => "Project Settings",
            nameof(Engine.EffectiveSettings.TargetUpdatesPerSecond) => Engine.GameSettings is null ? "Engine Default" : "Project Settings",
            nameof(Engine.EffectiveSettings.TargetFramesPerSecond) => Engine.GameSettings is null ? "Engine Default" : "Project Settings",
            nameof(Engine.EffectiveSettings.FixedFramesPerSecond) => Engine.GameSettings is null ? "Engine Default" : "Project Settings",
            nameof(Engine.EffectiveSettings.UnfocusedTargetFramesPerSecond) => ResolveUnfocusedTargetFrameSource(),
            _ when IsUserPrimaryEffectiveSetting(settingName) && HasReadableProperty(Engine.UserSettings, settingName) => "User Settings",
            _ => "Engine Default",
        };
    }

    private static string ResolveUnfocusedTargetFrameSource()
    {
        if (TryGetActiveOverride(Engine.UserSettings, nameof(UserSettings.UnfocusedTargetFramesPerSecondOverride)))
            return "User Settings";

        if (TryGetPropertyValue(Engine.GameSettings, nameof(GameStartupSettings.UnfocusedTargetFramesPerSecond), out object? projectValue) &&
            projectValue is not null)
        {
            return "Project Settings";
        }

        return ResolveEffectiveSettingSource(nameof(Engine.EffectiveSettings.TargetFramesPerSecond));
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

    private static int GetEffectiveSettingCategoryOrder(string settingName)
        => GetEffectiveSettingCategory(settingName) switch
        {
            "Threading" => 0,
            "Rendering" => 1,
            "Performance" => 2,
            "Technical" => 3,
            "Networking" => 4,
            "Debug" => 5,
            "Audio" => 6,
            _ => 10,
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

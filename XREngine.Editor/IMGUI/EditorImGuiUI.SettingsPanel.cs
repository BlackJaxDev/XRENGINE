using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine;
using XREngine.Core.Files;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        private static void DrawGlobalEditorPreferencesPanel()
        {
            if (!_showGlobalEditorPreferences) return;
            if (!ImGui.Begin("Global Editor Preferences", ref _showGlobalEditorPreferences))
            {
                ImGui.End();
                return;
            }

            // Ensure Global Editor Preferences participates in dirty tracking even if no project is loaded yet.
            if (Engine.GlobalEditorPreferences is XRAsset editorPreferencesAsset && Engine.Assets is not null)
            {
                editorPreferencesAsset.Name ??= "Global Editor Preferences";

                Engine.Assets.EnsureTracked(editorPreferencesAsset.SourceAsset);
            }

            if (ImGui.Button("Save Global Editor Preferences"))
                Engine.SaveGlobalEditorPreferences();
            ImGui.Separator();

            DrawSettingsTabContent(Engine.GlobalEditorPreferences, "Global Editor Preferences");
            ImGui.End();
        }

        private static void DrawEditorPreferencesOverridesPanel()
        {
            if (!_showEditorPreferencesOverrides) return;
            if (!ImGui.Begin("Editor Preferences Overrides", ref _showEditorPreferencesOverrides))
            {
                ImGui.End();
                return;
            }

            // Ensure Overrides asset participates in dirty tracking even if no project is loaded yet.
            if (Engine.EditorPreferencesOverrides is XRAsset overridesAsset && Engine.Assets is not null)
            {
                overridesAsset.Name ??= "Editor Preferences Overrides";
                if (Engine.CurrentProject?.EngineSettingsPath is string engineSettingsPath)
                    overridesAsset.FilePath = engineSettingsPath;

                Engine.Assets.EnsureTracked(overridesAsset.SourceAsset);
            }

            if (Engine.CurrentProject is not null)
            {
                if (ImGui.Button("Save Editor Preferences Overrides"))
                    Engine.SaveProjectEditorPreferencesOverrides();
                ImGui.SameLine();
                ImGui.TextDisabled($"(Project: {Engine.CurrentProject.ProjectName})");
                ImGui.Separator();
            }
            else
            {
                ImGui.TextDisabled("Sandbox mode - overrides are saved globally.");
                ImGui.Separator();
            }

            DrawSettingsTabContent(Engine.EditorPreferencesOverrides, "Editor Preferences Overrides");
            ImGui.End();
        }

        private static void DrawUserSettingsPanel()
        {
            if (!_showUserSettings) return;
            if (!ImGui.Begin("User Settings", ref _showUserSettings))
            {
                ImGui.End();
                return;
            }

            if (Engine.UserSettings is XRAsset userSettingsAsset && Engine.Assets is not null)
            {
                var rootAsset = userSettingsAsset.SourceAsset ?? userSettingsAsset;
                rootAsset.Name ??= "User Settings";
                Engine.Assets.EnsureTracked(rootAsset);
            }

            // Save button at the top
            if (Engine.CurrentProject is not null)
            {
                if (ImGui.Button("Save User Settings"))
                    Engine.SaveProjectUserSettings();
                ImGui.SameLine();
                ImGui.TextDisabled($"(Project: {Engine.CurrentProject.ProjectName})");
                ImGui.Separator();
            }
            else
            {
                ImGui.TextDisabled("Sandbox mode - settings are saved globally.");
                ImGui.Separator();
            }

            DrawSettingsTabContent(Engine.UserSettings, "User Settings");
            ImGui.End();
        }

        private static void DrawBuildSettingsPanel()
        {
            if (!_showBuildSettings) return;
            if (!ImGui.Begin("Build Settings", ref _showBuildSettings))
            {
                ImGui.End();
                return;
            }

            if (Engine.CurrentProject is not null)
            {
                if (ImGui.Button("Save Build Settings"))
                    Engine.SaveProjectBuildSettings();
                ImGui.SameLine();
                ImGui.TextDisabled($"(Project: {Engine.CurrentProject.ProjectName})");
                ImGui.Separator();
            }
            else
            {
                ImGui.TextDisabled("Sandbox mode - settings are saved globally.");
                ImGui.Separator();
            }

            DrawSettingsTabContent(Engine.BuildSettings, "Build Settings");
            ImGui.End();
        }

        private static void DrawSettingsTabContent(object? settingsRoot, string headerLabel)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSettingsTabContent");
            if (settingsRoot is null)
            {
                ImGui.TextDisabled($"{headerLabel} unavailable.");
                return;
            }

            Vector2 childSize = ImGui.GetContentRegionAvail();
            if (childSize.Y < 0.0f)
                childSize.Y = 0.0f;

            ImGui.BeginChild($"SettingsScroll##{headerLabel}", childSize, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
            try
            {
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                DrawSettingsObject(new InspectorTargetSet(new[] { settingsRoot }, settingsRoot.GetType()), headerLabel, null, visited, true);
            }
            finally
            {
                ImGui.EndChild();
            }
        }
}

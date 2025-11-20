using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private static void DrawEngineSettingsPanel()
        {
            if (!_showEngineSettings) return;
            if (!ImGui.Begin("Engine Settings", ref _showEngineSettings))
            {
                ImGui.End();
                return;
            }
            DrawSettingsTabContent(Engine.Rendering.Settings, "Engine Settings");
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
            DrawSettingsTabContent(Engine.UserSettings, "User Settings");
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
                DrawSettingsObject(settingsRoot, headerLabel, null, visited, true);
            }
            finally
            {
                ImGui.EndChild();
            }
        }
    }
}

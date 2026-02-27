using ImGuiNET;
using System.Numerics;
using XREngine.Components;

namespace XREngine.Editor.ComponentEditors;

/// <summary>
/// Custom ImGui inspector for <see cref="SteamAudioProbeComponent"/>.
/// Provides generation controls, baking buttons, and status readout.
/// </summary>
public sealed class SteamAudioProbeComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not SteamAudioProbeComponent probes)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(probes, visited, "Steam Audio Probes"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(probes.GetHashCode());

        DrawStatusSection(probes);
        DrawGenerationSection(probes);
        DrawActionsSection(probes);
        DrawBakingSection(probes);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawStatusSection(SteamAudioProbeComponent probes)
    {
        if (!ImGui.CollapsingHeader("Status", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled($"Probe Count: {probes.ProbeCount}");
        ImGui.TextDisabled($"Committed: {probes.IsCommitted}");
        ImGui.TextDisabled($"Attached: {probes.IsAttached}");
        ImGui.TextDisabled($"Bake Status: {probes.BakeStatus}");
    }

    private static void DrawGenerationSection(SteamAudioProbeComponent probes)
    {
        if (!ImGui.CollapsingHeader("Probe Generation", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Generation mode combo
        var mode = (int)probes.GenerationMode;
        string[] modeNames = ["Uniform Floor", "Manual"];
        if (ImGui.Combo("Generation Mode", ref mode, modeNames, modeNames.Length))
            probes.GenerationMode = (SteamAudioProbeComponent.EProbeGenerationMode)mode;

        bool autoGen = probes.AutoGenerate;
        if (ImGui.Checkbox("Auto Generate", ref autoGen))
            probes.AutoGenerate = autoGen;

        bool autoAttach = probes.AutoAttach;
        if (ImGui.Checkbox("Auto Attach", ref autoAttach))
            probes.AutoAttach = autoAttach;

        ImGui.Separator();

        if (probes.GenerationMode == SteamAudioProbeComponent.EProbeGenerationMode.UniformFloor)
        {
            float spacing = probes.ProbeSpacing;
            if (ImGui.DragFloat("Probe Spacing (m)", ref spacing, 0.1f, 0.1f, 50.0f, "%.1f"))
                probes.ProbeSpacing = spacing;

            float height = probes.ProbeHeight;
            if (ImGui.DragFloat("Probe Height (m)", ref height, 0.1f, 0.0f, 20.0f, "%.1f"))
                probes.ProbeHeight = height;

            Vector3 extents = probes.VolumeExtents;
            if (ImGui.DragFloat3("Volume Extents", ref extents, 0.5f, 0.1f, 500.0f, "%.1f"))
                probes.VolumeExtents = extents;
        }
        else
        {
            float radius = probes.ManualProbeRadius;
            if (ImGui.DragFloat("Probe Radius (m)", ref radius, 0.1f, 0.01f, 50.0f, "%.2f"))
                probes.ManualProbeRadius = radius;
        }
    }

    private static void DrawActionsSection(SteamAudioProbeComponent probes)
    {
        if (!ImGui.CollapsingHeader("Actions", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (ImGui.Button("Regenerate Probes"))
        {
            bool success = probes.RegenerateProbes();
            if (!success)
                ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Generation failed — check console.");
        }

        ImGui.SameLine();

        bool canAttach = probes.ProbeBatch is not null && probes.IsCommitted && !probes.IsAttached;
        if (!canAttach) ImGui.BeginDisabled();
        if (ImGui.Button("Attach"))
            probes.AttachToProcessor();
        if (!canAttach) ImGui.EndDisabled();

        ImGui.SameLine();

        bool canDetach = probes.IsAttached;
        if (!canDetach) ImGui.BeginDisabled();
        if (ImGui.Button("Detach"))
            probes.DetachFromProcessor();
        if (!canDetach) ImGui.EndDisabled();
    }

    private static void DrawBakingSection(SteamAudioProbeComponent probes)
    {
        if (!ImGui.CollapsingHeader("Baking"))
            return;

        bool canBake = probes.IsAttached && probes.IsCommitted;

        ImGui.TextDisabled("Baking is a blocking operation and may take time.");

        if (!canBake)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f),
                "Probes must be generated, committed, and attached to bake.");
        }

        if (!canBake) ImGui.BeginDisabled();

        if (ImGui.Button("Bake Reflections"))
            probes.BakeReflections();

        ImGui.SameLine();

        if (ImGui.Button("Bake Pathing"))
            probes.BakePathing();

        if (!canBake) ImGui.EndDisabled();
    }
}

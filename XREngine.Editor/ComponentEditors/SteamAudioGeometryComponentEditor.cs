using ImGuiNET;
using System.Numerics;
using XREngine.Audio.Steam;
using XREngine.Components;

namespace XREngine.Editor.ComponentEditors;

/// <summary>
/// Custom ImGui inspector for <see cref="SteamAudioGeometryComponent"/>.
/// Provides a material preset picker and per-band absorption/transmission sliders.
/// </summary>
public sealed class SteamAudioGeometryComponentEditor : IXRComponentEditor
{
    private static readonly (string Name, SteamAudioMaterial Material)[] Presets =
    [
        ("Default",  SteamAudioMaterial.Default),
        ("Concrete", SteamAudioMaterial.Concrete),
        ("Wood",     SteamAudioMaterial.Wood),
        ("Glass",    SteamAudioMaterial.Glass),
        ("Metal",    SteamAudioMaterial.Metal),
        ("Carpet",   SteamAudioMaterial.Carpet),
        ("Dirt",     SteamAudioMaterial.Dirt),
        ("Custom",   null!), // Sentinel for user-defined values
    ];

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not SteamAudioGeometryComponent geo)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(geo, visited, "Steam Audio Geometry"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(geo.GetHashCode());

        DrawStatusSection(geo);
        DrawDynamicSection(geo);
        DrawMaterialSection(geo);
        DrawActionsSection(geo);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawStatusSection(SteamAudioGeometryComponent geo)
    {
        if (!ImGui.CollapsingHeader("Status", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled($"Registered: {geo.IsRegistered}");
    }

    private static void DrawDynamicSection(SteamAudioGeometryComponent geo)
    {
        if (!ImGui.CollapsingHeader("Geometry Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool isDynamic = geo.IsDynamic;
        if (ImGui.Checkbox("Dynamic", ref isDynamic))
            geo.IsDynamic = isDynamic;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, the acoustic mesh rebuilds when the transform changes.");
    }

    private static void DrawMaterialSection(SteamAudioGeometryComponent geo)
    {
        if (!ImGui.CollapsingHeader("Acoustic Material", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var mat = geo.Material;

        // Preset picker
        int currentPreset = FindMatchingPreset(mat);
        string presetLabel = currentPreset >= 0 ? Presets[currentPreset].Name : "Custom";

        if (ImGui.BeginCombo("Preset", presetLabel))
        {
            for (int i = 0; i < Presets.Length - 1; i++) // Skip "Custom" sentinel
            {
                bool selected = i == currentPreset;
                if (ImGui.Selectable(Presets[i].Name, selected))
                {
                    geo.Material = CloneMaterial(Presets[i].Material);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Separator();
        ImGui.TextDisabled("Per-band values: Low / Mid / High frequency.");

        // Absorption (3-band)
        Vector3 absorption = mat.Absorption;
        if (ImGui.DragFloat3("Absorption", ref absorption, 0.005f, 0.0f, 1.0f, "%.3f"))
        {
            mat.Absorption = Vector3.Clamp(absorption, Vector3.Zero, Vector3.One);
            geo.Material = mat; // Trigger dirty flag
        }

        // Scattering (scalar)
        float scattering = mat.Scattering;
        if (ImGui.SliderFloat("Scattering", ref scattering, 0.0f, 1.0f, "%.3f"))
        {
            mat.Scattering = scattering;
            geo.Material = mat;
        }

        // Transmission (3-band)
        Vector3 transmission = mat.Transmission;
        if (ImGui.DragFloat3("Transmission", ref transmission, 0.005f, 0.0f, 1.0f, "%.3f"))
        {
            mat.Transmission = Vector3.Clamp(transmission, Vector3.Zero, Vector3.One);
            geo.Material = mat;
        }
    }

    private static void DrawActionsSection(SteamAudioGeometryComponent geo)
    {
        if (!ImGui.CollapsingHeader("Actions"))
            return;

        if (ImGui.Button("Re-register Geometry"))
        {
            geo.UnregisterGeometry();
            geo.TryRegisterGeometry();
        }

        ImGui.SameLine();

        bool canUnregister = geo.IsRegistered;
        if (!canUnregister) ImGui.BeginDisabled();
        if (ImGui.Button("Unregister"))
            geo.UnregisterGeometry();
        if (!canUnregister) ImGui.EndDisabled();
    }

    /// <summary>
    /// Finds which preset (if any) matches the current material values.
    /// Returns -1 if no match (custom values).
    /// </summary>
    private static int FindMatchingPreset(SteamAudioMaterial mat)
    {
        for (int i = 0; i < Presets.Length - 1; i++)
        {
            var p = Presets[i].Material;
            if (Vector3.DistanceSquared(mat.Absorption, p.Absorption) < 1e-6f
                && MathF.Abs(mat.Scattering - p.Scattering) < 1e-6f
                && Vector3.DistanceSquared(mat.Transmission, p.Transmission) < 1e-6f)
            {
                return i;
            }
        }
        return -1;
    }

    private static SteamAudioMaterial CloneMaterial(SteamAudioMaterial source) => new()
    {
        Absorption = source.Absorption,
        Scattering = source.Scattering,
        Transmission = source.Transmission,
    };
}

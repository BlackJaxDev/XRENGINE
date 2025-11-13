using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Numerics;
using ImGuiNET;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;

namespace XREngine.Editor.ComponentEditors;

public sealed class ModelComponentEditor : IXRComponentEditor
{
    private sealed class AdvancedToggleState
    {
        public bool Enabled;
    }

    private static readonly ConditionalWeakTable<XRComponent, AdvancedToggleState> _advancedPropertiesState = new();
    private static readonly Vector4 ActiveLodHighlight = new(0.20f, 0.50f, 0.90f, 0.18f);

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not ModelComponent modelComponent)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            return;
        }

        bool advanced = GetAdvancedPropertiesState(modelComponent);

        ImGui.PushID(modelComponent.GetHashCode());
        if (ImGui.Checkbox("Advanced Properties", ref advanced))
            SetAdvancedPropertiesState(modelComponent, advanced);
        ImGui.PopID();

        if (advanced)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            return;
        }

        DrawModelOverview(modelComponent);
    }

    private static bool GetAdvancedPropertiesState(ModelComponent component)
        => _advancedPropertiesState.TryGetValue(component, out var state) && state.Enabled;

    private static void SetAdvancedPropertiesState(ModelComponent component, bool enabled)
    {
        if (enabled)
            _advancedPropertiesState.GetValue(component, _ => new AdvancedToggleState()).Enabled = true;
        else
            _advancedPropertiesState.Remove(component);
    }

    private static void DrawModelOverview(ModelComponent modelComponent)
    {
        Model? model = modelComponent.Model;
        if (model is null)
        {
            ImGui.TextDisabled("No model assigned.");
            return;
        }

        string displayName = string.IsNullOrEmpty(model.Name) ? "<unnamed model>" : model.Name;
        ImGui.TextUnformatted($"Model: {displayName}");
        ImGui.TextUnformatted("Submeshes: " + model.Meshes.Count.ToString(CultureInfo.InvariantCulture));

        if (model.Meshes.Count == 0)
            return;

        if (!ImGui.CollapsingHeader("Submeshes", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var runtimeMeshes = modelComponent.Meshes.ToArray();
        int submeshIndex = 0;
        foreach (SubMesh subMesh in model.Meshes)
        {
            RenderableMesh? runtimeMesh = submeshIndex < runtimeMeshes.Length ? runtimeMeshes[submeshIndex] : null;
            DrawSubmeshSection(submeshIndex, subMesh, runtimeMesh);
            submeshIndex++;
        }
    }

    private static void DrawSubmeshSection(int index, SubMesh subMesh, RenderableMesh? runtimeMesh)
    {
        string submeshName = string.IsNullOrEmpty(subMesh.Name) ? "<unnamed>" : subMesh.Name;
        string headerLabel = $"Submesh {index}: {submeshName} ({subMesh.LODs.Count} LOD{(subMesh.LODs.Count == 1 ? string.Empty : "s")})";

        if (!ImGui.TreeNodeEx(headerLabel, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var bounds = subMesh.Bounds;
        ImGui.TextUnformatted($"Bounds Min: ({bounds.Min.X:F2}, {bounds.Min.Y:F2}, {bounds.Min.Z:F2})");
        ImGui.TextUnformatted($"Bounds Max: ({bounds.Max.X:F2}, {bounds.Max.Y:F2}, {bounds.Max.Z:F2})");

        string commandLabel = FormatRenderCommandLabel(runtimeMesh);

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp
                                          | ImGuiTableFlags.RowBg
                                          | ImGuiTableFlags.BordersOuter
                                          | ImGuiTableFlags.BordersInnerV
                                          | ImGuiTableFlags.NoSavedSettings;

        if (ImGui.BeginTable($"Submesh{index}_LODs", 6, tableFlags))
        {
            ImGui.TableSetupColumn("LOD", ImGuiTableColumnFlags.WidthFixed, 60.0f);
            ImGui.TableSetupColumn("Asset Mesh", ImGuiTableColumnFlags.WidthStretch, 0.22f);
            ImGui.TableSetupColumn("Asset Material", ImGuiTableColumnFlags.WidthStretch, 0.22f);
            ImGui.TableSetupColumn("Runtime Mesh", ImGuiTableColumnFlags.WidthStretch, 0.22f);
            ImGui.TableSetupColumn("Runtime Material", ImGuiTableColumnFlags.WidthStretch, 0.22f);
            ImGui.TableSetupColumn("Render Command", ImGuiTableColumnFlags.WidthStretch, 0.30f);
            ImGui.TableHeadersRow();

            var runtimeLodNode = runtimeMesh?.LODs.First;
            int lodIndex = 0;

            foreach (var lod in subMesh.LODs)
            {
                var runtimeLod = runtimeLodNode?.Value;
                runtimeLodNode = runtimeLodNode?.Next;

                ImGui.TableNextRow();

                if (runtimeMesh is not null && runtimeMesh.CurrentLOD?.Value == runtimeLod)
                {
                    uint highlight = ImGui.ColorConvertFloat4ToU32(ActiveLodHighlight);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, highlight);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, highlight);
                }

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"#{lodIndex}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Max visible distance: {lod.MaxVisibleDistance.ToString("F2", CultureInfo.InvariantCulture)}");

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(FormatAssetLabel(lod.Mesh?.Name, lod.Mesh));

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(FormatAssetLabel(lod.Material?.Name, lod.Material));

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(FormatAssetLabel(runtimeLod?.Renderer?.Mesh?.Name, runtimeLod?.Renderer?.Mesh));

                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(FormatAssetLabel(runtimeLod?.Renderer?.Material?.Name, runtimeLod?.Renderer?.Material));

                ImGui.TableSetColumnIndex(5);
                if (lodIndex == 0)
                    ImGui.TextUnformatted(commandLabel);
                else
                    ImGui.TextDisabled("--");

                lodIndex++;
            }

            ImGui.EndTable();
        }

        ImGui.TreePop();
    }

    private static string FormatAssetLabel(string? preferredName, object? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferredName))
            return preferredName!;

        return fallback?.GetType().Name ?? "<none>";
    }

    private static string FormatRenderCommandLabel(RenderableMesh? runtimeMesh)
    {
        if (runtimeMesh is null)
            return "<none>";

        foreach (RenderCommand command in runtimeMesh.RenderInfo.RenderCommands)
        {
            if (command is null)
                continue;

            return $"{command.GetType().Name} (Pass {command.RenderPass})";
        }

        return "<none>";
    }
}

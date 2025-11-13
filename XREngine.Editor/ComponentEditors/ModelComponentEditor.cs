using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using AssetFieldOptions = XREngine.Editor.ImGuiAssetUtilities.AssetFieldOptions;

namespace XREngine.Editor.ComponentEditors;

public sealed class ModelComponentEditor : IXRComponentEditor
{
    private sealed class AdvancedToggleState
    {
        public bool Enabled;
    }

    private static readonly ConditionalWeakTable<XRComponent, AdvancedToggleState> _advancedPropertiesState = new();
    private static readonly Vector4 ActiveLodHighlight = new(0.20f, 0.50f, 0.90f, 0.18f);
    private static readonly Vector4 ActiveLodTextColor = new(0.35f, 0.75f, 1.00f, 1.00f);

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

    DrawComponentProperties(modelComponent);
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

    private static void DrawComponentProperties(ModelComponent modelComponent)
    {
        if (!ImGui.CollapsingHeader("Component", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;

        if (ImGui.BeginTable("ComponentProperties", 2, tableFlags))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Model");
            ImGui.TableSetColumnIndex(1);
            ImGuiAssetUtilities.DrawAssetField("ComponentModel", modelComponent.Model, asset =>
            {
                if (!ReferenceEquals(modelComponent.Model, asset))
                    modelComponent.Model = asset;
            });

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Render Bounds");
            ImGui.TableSetColumnIndex(1);
            bool renderBounds = modelComponent.RenderBounds;
            if (ImGui.Checkbox("##RenderBounds", ref renderBounds))
                modelComponent.RenderBounds = renderBounds;

            ImGui.EndTable();
        }

        ImGui.Spacing();
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
        var lodEntries = BuildLodEntries(subMesh, runtimeMesh);

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

            foreach (var entry in lodEntries)
            {
                var lod = entry.Lod;
                var runtimeNode = entry.RuntimeNode;
                var runtimeLod = runtimeNode?.Value;

                ImGui.TableNextRow();

                bool isActive = runtimeMesh is not null && runtimeMesh.CurrentLOD == runtimeNode;
                if (isActive)
                {
                    uint highlight = ImGui.ColorConvertFloat4ToU32(ActiveLodHighlight);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, highlight);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, highlight);
                }

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"#{entry.Index}");
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
                if (entry.Index == 0)
                    ImGui.TextUnformatted(commandLabel);
                else
                    ImGui.TextDisabled("--");
            }

            ImGui.EndTable();
        }

        DrawLodPropertyEditors(index, subMesh, lodEntries, runtimeMesh);

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

    private static IReadOnlyList<(int Index, SubMeshLOD Lod, LinkedListNode<RenderableMesh.RenderableLOD>? RuntimeNode)> BuildLodEntries(SubMesh subMesh, RenderableMesh? runtimeMesh)
    {
        List<(int, SubMeshLOD, LinkedListNode<RenderableMesh.RenderableLOD>?)> entries = new();
        var runtimeNode = runtimeMesh?.LODs.First;
        int lodIndex = 0;
        foreach (SubMeshLOD lod in subMesh.LODs)
        {
            var currentNode = runtimeNode;
            runtimeNode = runtimeNode?.Next;
            entries.Add((lodIndex, lod, currentNode));
            lodIndex++;
        }

        return entries;
    }

    private static void DrawLodPropertyEditors(
        int submeshIndex,
        SubMesh subMesh,
        IReadOnlyList<(int Index, SubMeshLOD Lod, LinkedListNode<RenderableMesh.RenderableLOD>? RuntimeNode)> lodEntries,
        RenderableMesh? runtimeMesh)
    {
        if (lodEntries.Count == 0)
            return;

        ImGui.SeparatorText($"LOD Details (Submesh {submeshIndex})");

        foreach (var entry in lodEntries)
            DrawLodEditor(submeshIndex, subMesh, entry, runtimeMesh);
    }

    private static void DrawLodEditor(
        int submeshIndex,
        SubMesh subMesh,
        (int Index, SubMeshLOD Lod, LinkedListNode<RenderableMesh.RenderableLOD>? RuntimeNode) entry,
        RenderableMesh? runtimeMesh)
    {
        var lod = entry.Lod;
        var runtimeNode = entry.RuntimeNode;
        var runtimeLod = runtimeNode?.Value;
        bool isActive = runtimeMesh is not null && runtimeMesh.CurrentLOD == runtimeNode;

        ImGui.PushID($"Submesh{submeshIndex}_LOD{entry.Index}");

        string displayLabel = $"LOD #{entry.Index} ({lod.MaxVisibleDistance.ToString("F2", CultureInfo.InvariantCulture)}m)";
        if (isActive)
            displayLabel += " [Active]";
        string idLabel = $"{displayLabel}##Submesh{submeshIndex}_LOD{entry.Index}";

        if (isActive)
            ImGui.PushStyleColor(ImGuiCol.Text, ActiveLodTextColor);

        bool open = ImGui.TreeNodeEx(idLabel, ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.SpanFullWidth);

        if (isActive)
            ImGui.PopStyleColor();

        if (open)
        {
            DrawLodEditorContent(subMesh, runtimeMesh, lod, runtimeNode, runtimeLod);
            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private static void DrawLodEditorContent(
        SubMesh subMesh,
        RenderableMesh? runtimeMesh,
        SubMeshLOD lod,
        LinkedListNode<RenderableMesh.RenderableLOD>? runtimeNode,
        RenderableMesh.RenderableLOD? runtimeLod)
    {
        const ImGuiTableFlags propertyTableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;

        if (ImGui.BeginTable("AssetProperties", 2, propertyTableFlags))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Max Visible Distance");
            ImGui.TableSetColumnIndex(1);
            float maxDistance = lod.MaxVisibleDistance;
            if (ImGui.InputFloat("##MaxDistance", ref maxDistance, 0.0f, 0.0f, "%.2f"))
            {
                maxDistance = MathF.Max(0.0f, maxDistance);
                if (!subMesh.LODs.Any(other => !ReferenceEquals(other, lod) && MathF.Abs(other.MaxVisibleDistance - maxDistance) < 0.0001f))
                    UpdateLodDistance(subMesh, lod, maxDistance, runtimeNode);
            }

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Asset Mesh");
            ImGui.TableSetColumnIndex(1);
            ImGuiAssetUtilities.DrawAssetField("AssetMesh", lod.Mesh, asset =>
            {
                lod.Mesh = asset;
                subMesh.Bounds = subMesh.CalculateBoundingBox();
            }, AssetFieldOptions.ForMeshes());

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Asset Material");
            ImGui.TableSetColumnIndex(1);
            ImGuiAssetUtilities.DrawAssetField("AssetMaterial", lod.Material, asset => lod.Material = asset, AssetFieldOptions.ForMaterials());

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.SeparatorText("Runtime Renderer");

        var renderer = runtimeLod?.Renderer;
        if (renderer is null)
        {
            ImGui.TextDisabled("Runtime renderer not available.");
            return;
        }

        if (ImGui.BeginTable("RuntimeProperties", 2, propertyTableFlags))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Generate Async");
            ImGui.TableSetColumnIndex(1);
            bool generateAsync = renderer.GenerateAsync;
            if (ImGui.Checkbox("##GenerateAsync", ref generateAsync))
                renderer.GenerateAsync = generateAsync;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Runtime Mesh");
            ImGui.TableSetColumnIndex(1);
            ImGuiAssetUtilities.DrawAssetField("RuntimeMesh", renderer.Mesh, asset => renderer.Mesh = asset, AssetFieldOptions.ForMeshes());

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Runtime Material");
            ImGui.TableSetColumnIndex(1);
            ImGuiAssetUtilities.DrawAssetField("RuntimeMaterial", renderer.Material, asset => renderer.Material = asset, AssetFieldOptions.ForMaterials());

            ImGui.EndTable();
        }

        if (runtimeMesh is not null)
            runtimeMesh.RenderInfo.LocalCullingVolume = subMesh.CullingBounds ?? subMesh.Bounds;
    }

    private static void UpdateLodDistance(
        SubMesh subMesh,
        SubMeshLOD lod,
        float newDistance,
        LinkedListNode<RenderableMesh.RenderableLOD>? runtimeNode)
    {
        if (MathF.Abs(lod.MaxVisibleDistance - newDistance) < 0.0001f)
            return;

        lod.MaxVisibleDistance = newDistance;

        var resorted = subMesh.LODs.ToList();
        subMesh.LODs.Clear();
        foreach (var entry in resorted.OrderBy(x => x.MaxVisibleDistance))
            subMesh.LODs.Add(entry);

        if (runtimeNode is not null)
        {
            var current = runtimeNode.Value;
            runtimeNode.Value = current with { MaxVisibleDistance = newDistance };
        }
    }
}

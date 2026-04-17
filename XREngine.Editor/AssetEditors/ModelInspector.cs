using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Rendering.Models;

namespace XREngine.Editor.AssetEditors;

public sealed class ModelInspector : IXRAssetInspector
{
    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.65f, 0.2f, 1f);
    private static readonly Vector4 SectionLabelColor = new(0.85f, 0.85f, 0.85f, 1f);

    public void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects)
    {
        var models = targets.Targets.OfType<Model>().Cast<object>().ToList();
        if (models.Count == 0)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(targets, visitedObjects);
            return;
        }

        if (targets.HasMultipleTargets)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(models, targets.CommonType), visitedObjects);
            return;
        }

        var model = (Model)models[0];

        DrawHeader(model);
        DrawSubMeshList(model, visitedObjects);
        DrawAdvancedSection(model, visitedObjects);
    }

    private static void DrawHeader(Model model)
    {
        ImGui.TextUnformatted(GetDisplayName(model));
        string path = model.FilePath ?? "<unsaved asset>";
        ImGui.TextDisabled(path);

        if (model.IsDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(DirtyBadgeColor, "Modified");
        }

        ImGui.Separator();
    }

    private static string GetDisplayName(Model model)
    {
        if (!string.IsNullOrWhiteSpace(model.Name))
            return model.Name!;
        if (!string.IsNullOrWhiteSpace(model.FilePath))
            return Path.GetFileName(model.FilePath) ?? model.GetType().Name;
        return model.GetType().Name;
    }

    private static void DrawSubMeshList(Model model, HashSet<object> visitedObjects)
    {
        var meshes = model.Meshes;
        ImGui.TextColored(SectionLabelColor, $"Sub-Meshes ({meshes.Count})");
        ImGui.Spacing();

        if (meshes.Count == 0)
        {
            ImGui.TextDisabled("No sub-meshes.");
            ImGui.Separator();
            return;
        }

        for (int i = 0; i < meshes.Count; i++)
        {
            var sub = meshes[i];
            string subName = string.IsNullOrWhiteSpace(sub?.Name) ? $"SubMesh[{i}]" : sub!.Name!;
            int lodCount = sub?.LODs?.Count ?? 0;
            string label = $"{subName} ({lodCount} LOD{(lodCount == 1 ? string.Empty : "s")})##SubMesh_{i}";

            if (ImGui.TreeNode(label))
            {
                if (sub is null)
                {
                    ImGui.TextDisabled("<null>");
                }
                else
                {
                    ImGui.PushID($"SubMeshEntry_{i}");
                    EditorImGuiUI.DrawAssetInspectorInline(sub);
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }
        }

        ImGui.Separator();
    }

    private static void DrawAdvancedSection(Model model, HashSet<object> visitedObjects)
    {
        if (!ImGui.CollapsingHeader("Raw Properties"))
            return;

        ImGui.PushID("ModelRawProperties");
        EditorImGuiUI.DrawDefaultAssetInspector(model, visitedObjects);
        ImGui.PopID();
    }
}

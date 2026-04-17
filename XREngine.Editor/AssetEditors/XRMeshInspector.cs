using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Editor.AssetEditors;

public sealed class XRMeshInspector : IXRAssetInspector
{
    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.65f, 0.2f, 1f);
    private static readonly Vector4 SectionLabelColor = new(0.85f, 0.85f, 0.85f, 1f);

    public void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects)
    {
        var meshes = targets.Targets.OfType<XRMesh>().Cast<object>().ToList();
        if (meshes.Count == 0)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(targets, visitedObjects);
            return;
        }

        if (targets.HasMultipleTargets)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(meshes, targets.CommonType), visitedObjects);
            return;
        }

        var mesh = (XRMesh)meshes[0];

        DrawHeader(mesh);
        DrawGeometrySummary(mesh);
        DrawLayoutSummary(mesh);
        DrawSkinningSummary(mesh);
        DrawBlendshapeSummary(mesh);
        DrawAdvancedSection(mesh, visitedObjects);
    }

    private static void DrawHeader(XRMesh mesh)
    {
        ImGui.TextUnformatted(GetDisplayName(mesh));
        string path = mesh.FilePath ?? "<unsaved asset>";
        ImGui.TextDisabled(path);

        if (mesh.IsDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(DirtyBadgeColor, "Modified");
        }

        ImGui.Separator();
    }

    private static string GetDisplayName(XRMesh mesh)
    {
        if (!string.IsNullOrWhiteSpace(mesh.Name))
            return mesh.Name!;
        if (!string.IsNullOrWhiteSpace(mesh.FilePath))
            return Path.GetFileName(mesh.FilePath) ?? mesh.GetType().Name;
        return mesh.GetType().Name;
    }

    private static void DrawGeometrySummary(XRMesh mesh)
    {
        ImGui.TextColored(SectionLabelColor, "Geometry");
        ImGui.Spacing();

        ImGui.TextUnformatted($"Vertices: {mesh.VertexCount:N0}");
        ImGui.TextUnformatted($"Primitive: {mesh.Type}");

        int triangleCount = mesh.Triangles?.Count ?? 0;
        int lineCount = mesh.Lines?.Count ?? 0;
        int pointCount = mesh.Points?.Count ?? 0;
        ImGui.TextUnformatted($"Triangles: {triangleCount:N0}   Lines: {lineCount:N0}   Points: {pointCount:N0}");

        if (mesh.Type == EPrimitiveType.Patches)
            ImGui.TextUnformatted($"Patch Vertices: {mesh.PatchVertices}");

        var bounds = mesh.Bounds;
        ImGui.TextUnformatted($"Bounds Min: {FormatVector(bounds.Min)}");
        ImGui.TextUnformatted($"Bounds Max: {FormatVector(bounds.Max)}");
        ImGui.TextUnformatted($"Bounds Size: {FormatVector(bounds.Max - bounds.Min)}");

        ImGui.Separator();
    }

    private static void DrawLayoutSummary(XRMesh mesh)
    {
        ImGui.TextColored(SectionLabelColor, "Buffer Layout");
        ImGui.Spacing();

        ImGui.TextUnformatted($"Interleaved: {(mesh.Interleaved ? "yes" : "no")}");
        if (mesh.Interleaved)
            ImGui.TextUnformatted($"Stride: {mesh.InterleavedStride} bytes");

        ImGui.TextUnformatted($"Position Offset: {mesh.PositionOffset}");
        ImGui.TextUnformatted($"Normal Offset: {FormatOptional(mesh.NormalOffset)}");
        ImGui.TextUnformatted($"Tangent Offset: {FormatOptional(mesh.TangentOffset)}");
        ImGui.TextUnformatted($"Color Offset: {FormatOptional(mesh.ColorOffset)}   Count: {mesh.ColorCount}");
        ImGui.TextUnformatted($"TexCoord Offset: {FormatOptional(mesh.TexCoordOffset)}   Count: {mesh.TexCoordCount}");

        ImGui.Separator();
    }

    private static void DrawSkinningSummary(XRMesh mesh)
    {
        ImGui.TextColored(SectionLabelColor, "Skinning");
        ImGui.Spacing();

        if (mesh.IsUnskinned)
        {
            ImGui.TextDisabled("Not skinned");
        }
        else
        {
            ImGui.TextUnformatted($"Bones: {mesh.UtilizedBones.Length:N0}");
            ImGui.TextUnformatted($"Single Bound: {(mesh.IsSingleBound ? "yes" : "no")}");
            ImGui.TextUnformatted($"Max Weights/Vertex: {mesh.MaxWeightCount}");
            ImGui.TextUnformatted($"Convention: {mesh.SkinningShaderConvention}");
        }

        ImGui.Separator();
    }

    private static void DrawBlendshapeSummary(XRMesh mesh)
    {
        if (!mesh.HasBlendshapes)
            return;

        ImGui.TextColored(SectionLabelColor, "Blendshapes");
        ImGui.Spacing();

        ImGui.TextUnformatted($"Count: {mesh.BlendshapeCount:N0}");

        if (ImGui.TreeNode("Names##XRMeshBlendshapes"))
        {
            foreach (string name in mesh.BlendshapeNames)
                ImGui.BulletText(string.IsNullOrEmpty(name) ? "<unnamed>" : name);
            ImGui.TreePop();
        }

        ImGui.Separator();
    }

    private static void DrawAdvancedSection(XRMesh mesh, HashSet<object> visitedObjects)
    {
        if (!ImGui.CollapsingHeader("Raw Properties"))
            return;

        ImGui.PushID("XRMeshRawProperties");
        EditorImGuiUI.DrawDefaultAssetInspector(mesh, visitedObjects);
        ImGui.PopID();
    }

    private static string FormatOptional(uint? value) => value.HasValue ? value.Value.ToString() : "<none>";

    private static string FormatVector(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
}

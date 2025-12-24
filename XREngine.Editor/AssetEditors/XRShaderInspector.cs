using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.Editor.AssetEditors;

public sealed class XRShaderInspector : IXRAssetInspector
{
    private static readonly TextFileInspector _textInspector = new();
    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.65f, 0.2f, 1f);

    public void DrawInspector(XRAsset asset, HashSet<object> visitedObjects)
    {
        if (asset is not XRShader shader)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(asset, visitedObjects);
            return;
        }

        DrawHeader(shader);
        DrawCompilationSettings(shader);
        DrawSourceSection(shader, visitedObjects);
        DrawAdvancedSection(shader, visitedObjects);
    }

    private static void DrawHeader(XRShader shader)
    {
        ImGui.TextUnformatted(GetDisplayName(shader));
        string path = shader.FilePath ?? "<unsaved asset>";
        ImGui.TextDisabled(path);

        if (shader.IsDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(DirtyBadgeColor, "Modified");
        }

        ImGui.Separator();
    }

    private static string GetDisplayName(XRShader shader)
    {
        if (!string.IsNullOrWhiteSpace(shader.Name))
            return shader.Name!;
        if (!string.IsNullOrWhiteSpace(shader.FilePath))
            return Path.GetFileName(shader.FilePath) ?? shader.GetType().Name;
        return shader.GetType().Name;
    }

    private static void DrawCompilationSettings(XRShader shader)
    {
        ImGui.TextUnformatted("Compilation");
        ImGui.Spacing();

        EShaderType shaderType = shader.Type;
        string preview = shaderType.ToString();
        if (ImGui.BeginCombo("Shader Type", preview))
        {
            foreach (EShaderType value in Enum.GetValues<EShaderType>())
            {
                bool selected = value == shaderType;
                if (ImGui.Selectable(value.ToString(), selected) && !selected)
                    shader.Type = value;

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        bool generateAsync = shader.GenerateAsync;
        if (ImGui.Checkbox("Compile Asynchronously", ref generateAsync))
            shader.GenerateAsync = generateAsync;

        ImGui.Separator();
    }

    private static void DrawSourceSection(XRShader shader, HashSet<object> visitedObjects)
    {
        ImGui.TextUnformatted("Source");
        ImGui.Spacing();

        TextFile? source = shader.Source;
        if (source is null)
        {
            if (ImGui.Button("Create Embedded Source"))
                shader.Source = TextFile.FromText(string.Empty);
            ImGui.Separator();
            return;
        }

        bool isEmbedded = !ReferenceEquals(source.SourceAsset, source);
        string label = isEmbedded
            ? "<embedded text asset>"
            : (!string.IsNullOrWhiteSpace(source.FilePath) ? source.FilePath! : "<unsaved text asset>");
        ImGui.TextDisabled(label);

        if (!isEmbedded && !string.IsNullOrWhiteSpace(source.FilePath))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reload##ShaderSourceReload"))
                source.Reload();
        }

        ImGui.Separator();
        ImGui.PushID("XRShaderSourceInspector");
        _textInspector.DrawInspector(source, visitedObjects);
        ImGui.PopID();
        ImGui.Separator();
    }

    private static void DrawAdvancedSection(XRShader shader, HashSet<object> visitedObjects)
    {
        if (!ImGui.CollapsingHeader("Raw Properties"))
            return;

        ImGui.PushID("XRShaderRawProperties");
        EditorImGuiUI.DrawDefaultAssetInspector(shader, visitedObjects);
        ImGui.PopID();
    }
}

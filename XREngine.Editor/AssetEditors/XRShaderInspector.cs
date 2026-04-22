using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Linq;
using ImGuiNET;
using XREngine;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.Editor.AssetEditors;

public sealed class XRShaderInspector : IXRAssetInspector
{
    private static readonly TextFileInspector _textInspector = new();
    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.65f, 0.2f, 1f);
    private static readonly Vector4 ValidationErrorColor = new(0.93f, 0.36f, 0.31f, 1f);
    private static readonly Vector4 ValidationWarningColor = new(0.96f, 0.74f, 0.23f, 1f);
    private static readonly Vector4 ValidationOkColor = new(0.32f, 0.82f, 0.52f, 1f);

    public void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects)
    {
        var shaders = targets.Targets.OfType<XRShader>().Cast<object>().ToList();
        if (shaders.Count == 0)
        {
            foreach (var asset in targets.Targets.OfType<XRAsset>())
                EditorImGuiUI.DrawDefaultAssetInspector(asset, visitedObjects);
            return;
        }

        if (targets.HasMultipleTargets)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(shaders, targets.CommonType), visitedObjects);
            return;
        }

        var shader = (XRShader)shaders[0];

        DrawHeader(shader);
        DrawCompilationSettings(shader);
        DrawUiMetadataSection(shader);
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
        _textInspector.DrawInspector(new EditorImGuiUI.InspectorTargetSet(new object[] { source }, source.GetType()), visitedObjects);
        ImGui.PopID();
        ImGui.Separator();
    }

    private static void DrawUiMetadataSection(XRShader shader)
    {
        if (!ImGui.CollapsingHeader("UI Metadata", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ShaderUiManifest manifest = shader.GetUiManifest();
        int implicitFeatureCount = manifest.Features.Count(static x => !x.HasExplicitMetadata);
        int unannotatedPropertyCount = manifest.Properties.Count(static x => !x.HasExplicitMetadata);
        int issueCount = manifest.ValidationIssues.Count;

        if (issueCount == 0 && implicitFeatureCount == 0 && unannotatedPropertyCount == 0)
        {
            ImGui.TextColored(ValidationOkColor, "All discovered features and properties are explicitly annotated.");
        }
        else
        {
            if (issueCount > 0)
                ImGui.TextColored(ValidationErrorColor, $"{issueCount} validation issue(s)");

            if (implicitFeatureCount > 0)
                ImGui.TextColored(ValidationWarningColor, $"{implicitFeatureCount} feature guard(s) are inferred from preprocessor macros.");

            if (unannotatedPropertyCount > 0)
                ImGui.TextColored(ValidationWarningColor, $"{unannotatedPropertyCount} uniform(s) have no explicit @property annotation.");
        }

        ImGui.TextDisabled($"Features: {manifest.Features.Count} | Properties: {manifest.Properties.Count}");

        if (issueCount > 0 && ImGui.TreeNodeEx("Validation Issues", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (ShaderUiValidationIssue issue in manifest.ValidationIssues)
            {
                Vector4 color = issue.Severity switch
                {
                    EShaderUiValidationSeverity.Error => ValidationErrorColor,
                    EShaderUiValidationSeverity.Warning => ValidationWarningColor,
                    _ => ValidationOkColor,
                };

                ImGui.TextColored(color, $"L{issue.LineNumber}: {issue.Message}");
            }

            ImGui.TreePop();
        }

        if (implicitFeatureCount > 0 && ImGui.TreeNodeEx("Implicit Features", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (ShaderUiFeature feature in manifest.Features.Where(static x => !x.HasExplicitMetadata))
                ImGui.BulletText($"{feature.DisplayName} ({feature.GuardMacro ?? "<no guard>"})");

            ImGui.TreePop();
        }

        if (unannotatedPropertyCount > 0 && ImGui.TreeNodeEx("Unannotated Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (ShaderUiProperty property in manifest.Properties.Where(static x => !x.HasExplicitMetadata))
                ImGui.BulletText($"{property.Name} ({property.GlslType})");

            ImGui.TreePop();
        }

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

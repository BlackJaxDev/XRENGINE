using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector
{
    private static readonly Vector4 RequiredShaderColor = new(0.93f, 0.36f, 0.31f, 1.0f);
    private static readonly Vector4 ValidationWarningColor = new(0.96f, 0.74f, 0.23f, 1.0f);
    private static readonly Vector4 ValidationOkColor = new(0.32f, 0.82f, 0.52f, 1.0f);

    private static readonly ShaderStageSlotDefinition[] ShaderStageSlots =
    [
        new(EShaderType.Fragment, "Fragment", true),
        new(EShaderType.Vertex, "Vertex", false),
        new(EShaderType.Geometry, "Geometry", false),
        new(EShaderType.TessControl, "Tess Control", false),
        new(EShaderType.TessEvaluation, "Tess Evaluation", false),
        new(EShaderType.Task, "Task", false),
        new(EShaderType.Mesh, "Mesh", false),
        new(EShaderType.Compute, "Compute", false),
    ];

    private static void DrawShaderStageSection(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Shaders", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled("Each stage has one slot. Assigning a shader here updates that shader asset's Type to match the selected stage.");

        bool hasDuplicates = HasDuplicateShaderStages(material, out int duplicateCount);
        if (!material.HasFragmentShader)
            ImGui.TextColored(RequiredShaderColor, "Fragment shader is required for render materials.");
        else
            ImGui.TextColored(ValidationOkColor, "Fragment shader assigned.");

        if (hasDuplicates)
        {
            ImGui.SameLine();
            ImGui.TextColored(ValidationWarningColor, $"{duplicateCount} duplicate stage assignment{(duplicateCount == 1 ? string.Empty : "s")}");
        }

        if (ImGui.SmallButton("Refresh Shader Params"))
            material.RefreshShaderState();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-parses shader source and refreshes the material's synchronized uniform parameters.");

        if (hasDuplicates)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Normalize Duplicate Stages"))
                material.NormalizeShaderStages();
        }

        ImGui.Separator();

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg
            | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("ShaderStageSlots", 4, tableFlags))
            return;

        ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.WidthFixed, 130.0f);
        ImGui.TableSetupColumn("Shader", ImGuiTableColumnFlags.WidthStretch, 0.48f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 0.34f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.18f);
        ImGui.TableHeadersRow();

        foreach (ShaderStageSlotDefinition slot in ShaderStageSlots)
        {
            XRShader? shader = material.GetShader(slot.Type);
            int stageCount = material.GetShaderCount(slot.Type);

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(slot.Label);
            if (slot.Required)
            {
                ImGui.SameLine();
                ImGui.TextColored(RequiredShaderColor, "(required)");
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.PushID($"ShaderSlot_{slot.Type}");
            ImGuiAssetUtilities.DrawAssetField(
                "ShaderAsset",
                shader,
                selected => AssignShaderStage(material, slot, selected),
                allowClear: !slot.Required);
            ImGui.PopID();

            ImGui.TableSetColumnIndex(2);
            DrawShaderSourceSummary(shader);

            ImGui.TableSetColumnIndex(3);
            DrawShaderStatusSummary(slot, shader, stageCount);
        }

        ImGui.EndTable();
    }

    private static void DrawShaderSourceSummary(XRShader? shader)
    {
        if (shader is null)
        {
            ImGui.TextDisabled("<none>");
            return;
        }

        string? sourcePath = shader.Source?.FilePath;
        string? assetPath = shader.FilePath;
        string display = !string.IsNullOrWhiteSpace(sourcePath)
            ? sourcePath!
            : (!string.IsNullOrWhiteSpace(assetPath) ? assetPath! : "<embedded source>");

        ImGui.TextWrapped(display);

        if (!string.IsNullOrWhiteSpace(sourcePath) || !string.IsNullOrWhiteSpace(assetPath))
        {
            if (ImGui.SmallButton($"Copy Path##{shader.GetHashCode()}"))
                ImGui.SetClipboardText(sourcePath ?? assetPath!);

            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Reload##{shader.GetHashCode()}"))
                    shader.Reload();
            }
        }
    }

    private static void DrawShaderStatusSummary(ShaderStageSlotDefinition slot, XRShader? shader, int stageCount)
    {
        if (shader is null)
        {
            if (slot.Required)
                ImGui.TextColored(RequiredShaderColor, "Required");
            else
                ImGui.TextDisabled("Optional");
            return;
        }

        ImGui.TextUnformatted(shader.Type.ToString());
        if (shader.GenerateAsync)
            ImGui.TextDisabled("Async compile enabled");

        if (stageCount > 1)
            ImGui.TextColored(ValidationWarningColor, $"{stageCount} shaders assigned");

        string? sourceText = shader.Source?.Text;
        if (string.IsNullOrWhiteSpace(sourceText))
            ImGui.TextColored(ValidationWarningColor, "Source is empty");
    }

    private static bool HasDuplicateShaderStages(XRMaterial material, out int duplicateCount)
    {
        duplicateCount = 0;
        foreach (ShaderStageSlotDefinition slot in ShaderStageSlots)
        {
            int count = material.GetShaderCount(slot.Type);
            if (count > 1)
                duplicateCount += count - 1;
        }

        return duplicateCount > 0;
    }

    private static void AssignShaderStage(XRMaterial material, ShaderStageSlotDefinition slot, XRShader? shader)
    {
        if (shader is not null)
        {
            if (string.IsNullOrWhiteSpace(shader.Name) && string.IsNullOrWhiteSpace(shader.FilePath))
                shader.Name = $"{slot.Label} Shader";

            EnsureShaderStageScaffold(shader, slot.Type);
        }

        material.SetShader(slot.Type, shader, coerceShaderType: true);
    }

    private static void EnsureShaderStageScaffold(XRShader shader, EShaderType shaderType)
    {
        if (shader.Source is { Text.Length: > 0 })
            return;

        shader.Source = TextFile.FromText(CreateShaderTemplate(shaderType));
        shader.MarkDirty();
    }

    private static string CreateShaderTemplate(EShaderType shaderType)
    {
        return shaderType switch
        {
            EShaderType.Fragment => "#version 460 core\n\nout vec4 OutColor;\n\nvoid main()\n{\n    OutColor = vec4(1.0);\n}\n",
            EShaderType.Vertex => "#version 460 core\n\nvoid main()\n{\n}\n",
            EShaderType.Geometry => "#version 460 core\n\nvoid main()\n{\n}\n",
            EShaderType.TessControl => "#version 460 core\n\nvoid main()\n{\n}\n",
            EShaderType.TessEvaluation => "#version 460 core\n\nvoid main()\n{\n}\n",
            EShaderType.Task => "#version 460 core\n\nvoid main()\n{\n}\n",
            EShaderType.Mesh => "#version 460 core\n\nvoid main()\n{\n}\n",
            EShaderType.Compute => "#version 460 core\n\nlayout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;\n\nvoid main()\n{\n}\n",
            _ => "#version 460 core\n\nvoid main()\n{\n}\n"
        };
    }

    private static void DrawParameterDriveCell(XRMaterial material, ShaderVar? param, string preferredName, string typeLabel)
    {
        if (param is null)
        {
            ImGui.TextDisabled("Create a local parameter to edit or animate this uniform.");
            return;
        }

        if (TryGetParameterPath(material, param, out string parameterPath))
        {
            if (ImGui.SmallButton($"Copy Anim Path##{param.GetHashCode()}"))
                ImGui.SetClipboardText(parameterPath);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Material-relative property path. Use this from an owning component/object when wiring animation members.");
        }
        else
        {
            ImGui.TextDisabled("Path unavailable");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"Copy Setter##{param.GetHashCode()}"))
            ImGui.SetClipboardText(BuildSetterHint(param, preferredName, typeLabel));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies a helper setter call you can forward through a scene component for event callbacks.");
    }

    private static bool TryGetParameterPath(XRMaterial material, ShaderVar param, out string path)
    {
        for (int i = 0; i < material.Parameters.Length; i++)
        {
            if (!ReferenceEquals(material.Parameters[i], param))
                continue;

            path = $"Parameters[{i}].Value";
            return true;
        }

        path = string.Empty;
        return false;
    }

    private static string BuildSetterHint(ShaderVar param, string preferredName, string typeLabel)
    {
        return param switch
        {
            ShaderFloat => $"material.SetFloat(\"{preferredName}\", value);",
            ShaderInt => $"material.SetInt(\"{preferredName}\", value);",
            ShaderUInt => $"material.SetUInt(\"{preferredName}\", value);",
            ShaderVector2 => $"material.SetVector2(\"{preferredName}\", value);",
            ShaderVector3 => $"material.SetVector3(\"{preferredName}\", value);",
            ShaderVector4 => $"material.SetVector4(\"{preferredName}\", value);",
            ShaderMat4 => $"material.SetMatrix4(\"{preferredName}\", value);",
            ShaderBool => $"// No direct XRMaterial bool setter yet; set '{preferredName}' via material.Parameter<ShaderBool>(\"{preferredName}\")?.SetValue(value);",
            _ => $"// Unsupported setter helper for {typeLabel} '{preferredName}'."
        };
    }

    private static bool TryCreateMaterialParameter(XRMaterial material, string glslType, string preferredName)
    {
        if (!ShaderVar.GlslTypeMap.TryGetValue(glslType, out EShaderVarType shaderVarType))
            return false;

        ShaderVar? parameter = ShaderVar.CreateForType(shaderVarType, preferredName);
        if (parameter is null)
            return false;

        ShaderVar[] current = material.Parameters ?? [];
        Array.Resize(ref current, current.Length + 1);
        current[^1] = parameter;
        material.Parameters = current;
        material.MarkDirty();
        return true;
    }

    private static string ResolveParameterName(string memberName, string? blockName = null, string? instanceName = null)
        => !string.IsNullOrWhiteSpace(instanceName)
            ? $"{instanceName}.{memberName}"
            : (!string.IsNullOrWhiteSpace(blockName)
                ? $"{blockName}.{memberName}"
                : memberName);

    private static bool LooksLikeColorParameter(string uniformName, ShaderVar parameter)
    {
        if (parameter is not ShaderVector3 and not ShaderVector4)
            return false;

        return uniformName.Contains("color", StringComparison.OrdinalIgnoreCase)
            || uniformName.Contains("tint", StringComparison.OrdinalIgnoreCase)
            || uniformName.Contains("albedo", StringComparison.OrdinalIgnoreCase)
            || uniformName.Contains("emission", StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawSamplerDriveCell(XRMaterial material, SamplerBindingEntry binding)
    {
        if (binding.EngineBinding is not null)
        {
            ImGui.TextDisabled("Engine-driven");
            return;
        }

        int slotIndex = binding.MaterialTextureSlot ?? binding.FallbackTextureSlot;
        string path = $"Textures[{slotIndex}]";

        if (ImGui.SmallButton($"Copy Anim Path##SamplerPath_{slotIndex}"))
            ImGui.SetClipboardText(path);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Material-relative texture slot path. Use this from an owning component/object when wiring animation members.");

        if (binding.AssignedTexture is { FilePath.Length: > 0 } texture)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Copy Texture Path##SamplerFile_{slotIndex}"))
                ImGui.SetClipboardText(texture.FilePath!);
        }
    }

    private static void AssignTextureToBinding(XRMaterial material, SamplerBindingEntry binding, XRTexture? texture)
    {
        if (binding.EngineBinding is not null)
            return;

        int slotIndex = binding.MaterialTextureSlot ?? binding.FallbackTextureSlot;
        EnsureTextureSlots(material, slotIndex + 1);

        if (texture is not null)
        {
            string samplerName = GetBaseSamplerName(binding.Sampler.Name);
            if (!string.Equals(texture.SamplerName, samplerName, StringComparison.Ordinal))
            {
                texture.SamplerName = samplerName;
                texture.MarkDirty();
            }
        }

        material.Textures[slotIndex] = texture;
        material.MarkDirty();
    }

    private sealed record ShaderStageSlotDefinition(EShaderType Type, string Label, bool Required);
}
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using XREngine.Editor;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector : IXRAssetInspector
{
    private const float TexturePreviewMaxEdge = 128.0f;
    private const float TexturePreviewFallbackEdge = 64.0f;

    private static readonly Dictionary<string, string> RuntimeDrivenSamplerProviders = new(StringComparer.Ordinal)
    {
        [EngineShaderBindingNames.Samplers.PrevPeelDepth] = "Exact transparency depth peeling",
        [EngineShaderBindingNames.Samplers.PpllHeadPointers] = "Per-pixel linked list transparency"
    };

    /// <summary>
    /// Uniforms driven by the pipeline outside the RequiredEngineUniforms flag system.
    /// </summary>
    private static readonly Dictionary<string, string> RuntimeDrivenUniformProviders = new(StringComparer.Ordinal)
    {
        [EngineShaderBindingNames.Uniforms.PpllMaxNodes] = "Per-pixel linked list transparency",
        [EngineShaderBindingNames.Uniforms.DepthPeelLayerIndex] = "Exact transparency depth peeling",
        [EngineShaderBindingNames.Uniforms.DepthPeelEpsilon] = "Exact transparency depth peeling"
    };

    private static readonly Vector4 EngineTagColor = new(0.40f, 0.75f, 0.95f, 1.0f);
    private static readonly Vector4 EngineActiveColor = new(0.30f, 0.85f, 0.55f, 1.0f);
    private static readonly Vector4 EngineMissingFlagColor = new(0.95f, 0.75f, 0.25f, 1.0f);
    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.65f, 0.2f, 1.0f);

    private static bool _hideEngineDrivenUniforms;

    private static bool IsEngineUniform(string name, out EUniformRequirements requiredFlags)
    {
        requiredFlags = UniformRequirementsDetection.GetAllProviders(name);
        return requiredFlags != EUniformRequirements.None || RuntimeDrivenUniformProviders.ContainsKey(name);
    }

    private static bool HasAnyRequiredFlag(XRMaterial material, EUniformRequirements required)
        => (material.RenderOptions.RequiredEngineUniforms & required) != 0;

    private static string FormatFlagNames(EUniformRequirements flags)
    {
        var parts = new List<string>(4);
        foreach (EUniformRequirements value in Enum.GetValues<EUniformRequirements>())
        {
            if (value != EUniformRequirements.None && flags.HasFlag(value))
                parts.Add(value.ToString());
        }
        return string.Join(" or ", parts);
    }

    public void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects)
    {
        if (targets.Targets.Count != 1 || targets.PrimaryTarget is not XRMaterial material)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(targets, visitedObjects);
            return;
        }

        DrawHeader(material);
        DrawShaderStageSection(material);
        DrawTransparencySettings(material);
        DrawRenderOptions(material, visitedObjects);
        ImGui.Checkbox("Hide engine-driven", ref _hideEngineDrivenUniforms);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide uniforms and samplers that are automatically driven by the engine.");

        DrawUberInspector(material);
        DrawUniforms(material);
        DrawUniformBlocks(material);
        DrawSamplerList(material);
        DrawDefaultInspector(material, visitedObjects);
    }

    private static void DrawHeader(XRMaterial material)
    {
        ImGui.TextUnformatted(material.Name ?? "<unnamed material>");
        ImGui.TextDisabled(material.FilePath ?? "<unsaved asset>");

        if (!string.IsNullOrWhiteSpace(material.FilePath))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path##XRMaterial"))
                ImGui.SetClipboardText(material.FilePath);
        }

        if (material.IsDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(DirtyBadgeColor, "Modified");
        }

        ImGui.TextDisabled($"Render Pass: {DescribeRenderPass(material.RenderPass)}");

        ETransparencyMode inferred = material.InferTransparencyMode();
        if (inferred != material.TransparencyMode)
            ImGui.TextDisabled($"Inferred: {inferred} | Explicit: {material.TransparencyMode}");

        ImGui.Separator();
    }

    private static void DrawTransparencySettings(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Transparency", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ETransparencyMode transparencyMode = material.TransparencyMode;
        if (ImGui.BeginCombo("Mode", transparencyMode.ToString()))
        {
            foreach (ETransparencyMode value in Enum.GetValues<ETransparencyMode>())
            {
                bool selected = value == transparencyMode;
                if (ImGui.Selectable(value.ToString(), selected) && !selected)
                {
                    material.TransparencyMode = value;
                    material.MarkDirty();
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (material.TransparencyMode is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage)
        {
            float alphaCutoff = material.AlphaCutoff;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderFloat("Alpha Cutoff", ref alphaCutoff, 0.0f, 1.0f))
            {
                material.AlphaCutoff = alphaCutoff;
                material.MarkDirty();
            }
        }

        int sortPriority = material.TransparentSortPriority;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.DragInt("Sort Priority", ref sortPriority))
        {
            material.TransparentSortPriority = sortPriority;
            material.MarkDirty();
        }

        string overridePreview = material.TransparentTechniqueOverride?.ToString() ?? "<none>";
        if (ImGui.BeginCombo("Technique Override", overridePreview))
        {
            bool isNoneSelected = material.TransparentTechniqueOverride is null;
            if (ImGui.Selectable("<none>", isNoneSelected) && !isNoneSelected)
            {
                material.TransparentTechniqueOverride = null;
                material.MarkDirty();
            }
            if (isNoneSelected)
                ImGui.SetItemDefaultFocus();

            foreach (ETransparencyMode value in Enum.GetValues<ETransparencyMode>())
            {
                bool selected = material.TransparentTechniqueOverride == value;
                if (ImGui.Selectable(value.ToString(), selected) && !selected)
                {
                    material.TransparentTechniqueOverride = value;
                    material.MarkDirty();
                }
            }

            ImGui.EndCombo();
        }
    }

    private static void DrawShaderList(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Shaders", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (material.Shaders.Count == 0)
        {
            ImGui.TextDisabled("No shaders assigned.");
            return;
        }

        for (int i = 0; i < material.Shaders.Count; i++)
        {
            XRShader shader = material.Shaders[i];
            string name = shader?.Name ?? shader?.Source?.FilePath ?? $"Shader {i}";
            string type = shader?.Type.ToString() ?? "Unknown";
            ImGui.BulletText($"{name} ({type})");
        }
    }

    private static void DrawRenderOptions(XRMaterial material, HashSet<object> visitedObjects)
    {
        if (!ImGui.CollapsingHeader("Render Options", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        int renderPass = material.RenderPass;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputInt("Render Pass", ref renderPass))
        {
            material.RenderPass = renderPass;
            material.MarkDirty();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Render pass bucket used by the active pipeline.");

        EditorImGuiUI.DrawRuntimeObjectInspector("Render Option Settings", material.RenderOptions, visitedObjects, defaultOpen: true);
    }

    private static void DrawUniforms(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Uniforms", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled("Edit local material values directly. Use Copy Anim Path to drive a parameter from an owning scene object.");
        ImGui.Separator();

        var parameterLookup = BuildParameterLookup(material);
        bool anyUniforms = false;

        for (int shaderIndex = 0; shaderIndex < material.Shaders.Count; shaderIndex++)
        {
            XRShader shader = material.Shaders[shaderIndex];
            if (shader is null)
                continue;

            string text = shader.GetResolvedSource();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var parsed = ParseShaderSource(text);
            if (parsed.Uniforms.Count == 0)
                continue;

            anyUniforms = true;
            string header = FormatShaderHeader(shader, shaderIndex, "Uniforms");
            if (!ImGui.TreeNodeEx(header, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            DrawUniformTable(parsed.Uniforms, parameterLookup, material);
            ImGui.TreePop();
        }

        if (!anyUniforms)
            ImGui.TextDisabled("No uniform declarations found in assigned shaders.");
    }

    private static void DrawUniformBlocks(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Uniform Blocks", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled("Create local parameters for block members when you need explicit overrides or animation targets.");
        ImGui.Separator();

        var parameterLookup = BuildParameterLookup(material);
        bool anyBlocks = false;

        for (int shaderIndex = 0; shaderIndex < material.Shaders.Count; shaderIndex++)
        {
            XRShader shader = material.Shaders[shaderIndex];
            if (shader is null)
                continue;

            string text = shader.GetResolvedSource();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var parsed = ParseShaderSource(text);
            if (parsed.Blocks.Count == 0)
                continue;

            anyBlocks = true;
            string header = FormatShaderHeader(shader, shaderIndex, "Blocks");
            if (!ImGui.TreeNodeEx(header, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            foreach (var block in parsed.Blocks)
            {
                string blockLabel = string.IsNullOrWhiteSpace(block.InstanceName)
                    ? block.BlockName
                    : $"{block.BlockName} ({block.InstanceName})";

                if (!ImGui.TreeNodeEx(blockLabel, ImGuiTreeNodeFlags.DefaultOpen))
                    continue;

                DrawUniformBlockMembers(block, parameterLookup, material);
                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        if (!anyBlocks)
            ImGui.TextDisabled("No uniform blocks found in assigned shaders.");
    }

    private static void DrawSamplerList(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Texture Samplers", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled("Click a texture field to inspect it inline. Replacing a texture also updates its sampler binding name to match the shader slot.");
        ImGui.Separator();

        var samplers = CollectSamplerDefinitions(material);
        var samplerBindings = ResolveSamplerBindings(material, samplers);
        if (samplers.Count == 0)
        {
            ImGui.TextDisabled("No sampler uniforms found in assigned shaders.");
            return;
        }

        if (ImGui.BeginTable("SamplerTable", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 50.0f);
            ImGui.TableSetupColumn("Sampler", ImGuiTableColumnFlags.WidthStretch, 0.22f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120.0f);
            ImGui.TableSetupColumn("Texture", ImGuiTableColumnFlags.WidthStretch, 0.32f);
            ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthFixed, 140.0f);
            ImGui.TableSetupColumn("Drive", ImGuiTableColumnFlags.WidthStretch, 0.18f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < samplers.Count; i++)
            {
                var binding = samplerBindings[i];
                var sampler = binding.Sampler;

                if (_hideEngineDrivenUniforms && binding.EngineBinding is not null)
                    continue;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(i.ToString(CultureInfo.InvariantCulture));

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(sampler.Name);
                if (binding.EngineBinding is not null)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(EngineTagColor, "(engine)");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(GetEngineSamplerTooltip(material, binding.EngineBinding));
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(sampler.TypeLabel);

                ImGui.TableSetColumnIndex(3);
                ImGui.PushID($"SamplerTexture_{i}");
                DrawSamplerTextureField(material, binding);
                ImGui.PopID();

                ImGui.TableSetColumnIndex(4);
                DrawTexturePreviewCell(binding.PreviewTexture, TexturePreviewMaxEdge);

                ImGui.TableSetColumnIndex(5);
                DrawSamplerDriveCell(material, binding);
            }

            ImGui.EndTable();
        }
    }

    private static void DrawDefaultInspector(XRMaterial material, HashSet<object> visitedObjects)
    {
        if (!ImGui.CollapsingHeader("Advanced Properties"))
            return;

        EditorImGuiUI.DrawDefaultAssetInspector(material, visitedObjects);
    }

    private static string DescribeRenderPass(int renderPass)
    {
        if (Enum.IsDefined(typeof(EDefaultRenderPass), renderPass))
            return ((EDefaultRenderPass)renderPass).ToString();
        return renderPass.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, ShaderVar> BuildParameterLookup(XRMaterial material)
    {
        var lookup = new Dictionary<string, ShaderVar>(StringComparer.Ordinal);
        foreach (var param in material.Parameters)
        {
            if (param?.Name is { Length: > 0 } name)
                lookup[name] = param;
        }

        return lookup;
    }

    private static void DrawUniformTable(IReadOnlyList<UniformEntry> uniforms, Dictionary<string, ShaderVar> parameters, XRMaterial material)
    {
        if (!ImGui.BeginTable("UniformTable", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.36f);
        ImGui.TableSetupColumn("Drive", ImGuiTableColumnFlags.WidthStretch, 0.24f);
        ImGui.TableHeadersRow();

        foreach (var uniform in uniforms)
        {
            bool isEngine = IsEngineUniform(uniform.Name, out var requiredFlags);
            bool isRuntimeDriven = RuntimeDrivenUniformProviders.TryGetValue(uniform.Name, out string? runtimeProvider);
            bool flagsActive = isEngine && (isRuntimeDriven || HasAnyRequiredFlag(material, requiredFlags));

            if (_hideEngineDrivenUniforms && isEngine)
                continue;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(uniform.Name);
            if (isEngine)
            {
                ImGui.SameLine();
                ImGui.TextColored(EngineTagColor, "(engine)");
                if (ImGui.IsItemHovered())
                {
                    if (isRuntimeDriven)
                        ImGui.SetTooltip($"Driven by engine pipeline: {runtimeProvider}");
                    else
                    {
                        string provider = FormatFlagNames(requiredFlags);
                        ImGui.SetTooltip(flagsActive
                            ? $"Driven by engine via RequiredEngineUniforms: {provider}"
                            : $"Enable {provider} in Render Options > RequiredEngineUniforms to drive this uniform");
                    }
                }
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(uniform.TypeLabel);

            ImGui.TableSetColumnIndex(2);
            ShaderVar? param = FindParameter(parameters, uniform.Name);
            if (isEngine && flagsActive && param is null)
            {
                ImGui.TextColored(EngineActiveColor, "Driven by engine");
            }
            else if (isEngine && !flagsActive && param is null)
            {
                string provider = FormatFlagNames(requiredFlags);
                ImGui.TextColored(EngineMissingFlagColor, $"Enable {provider}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Set RequiredEngineUniforms |= {provider} in Render Options for the engine to provide this value.");
            }
            else if (param is null)
            {
                string preferredName = ResolveParameterName(uniform.Name);
                if (ImGui.SmallButton($"Create##Uniform_{uniform.Name}"))
                {
                    if (TryCreateMaterialParameter(material, uniform.Type, preferredName))
                        parameters[preferredName] = material.Parameters[^1];
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Create a local material parameter named '{preferredName}' for this uniform.");
            }
            else
            {
                ImGui.PushID(param.GetHashCode());
                DrawShaderParameterControl(material, param);
                if (isEngine && flagsActive && ImGui.IsItemHovered())
                    ImGui.SetTooltip("This value is overwritten at render time by the engine.");
                ImGui.PopID();
            }

            ImGui.TableSetColumnIndex(3);
            if (isEngine && flagsActive && param is null)
            {
                ImGui.TextColored(EngineActiveColor, "Engine");
            }
            else if (isEngine && !flagsActive && param is null)
            {
                ImGui.TextColored(EngineMissingFlagColor, "Needs engine flags");
            }
            else
            {
                DrawParameterDriveCell(material, param, ResolveParameterName(uniform.Name), uniform.TypeLabel);
            }
        }

        ImGui.EndTable();
    }

    private static void DrawUniformBlockMembers(UniformBlockEntry block, Dictionary<string, ShaderVar> parameters, XRMaterial material)
    {
        if (!ImGui.BeginTable($"Block_{block.BlockName}", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Member", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.36f);
        ImGui.TableSetupColumn("Drive", ImGuiTableColumnFlags.WidthStretch, 0.24f);
        ImGui.TableHeadersRow();

        foreach (var member in block.Members)
        {
            bool isEngine = IsEngineUniform(member.Name, out var requiredFlags);
            bool isRuntimeDriven = RuntimeDrivenUniformProviders.TryGetValue(member.Name, out string? runtimeProvider);
            bool flagsActive = isEngine && (isRuntimeDriven || HasAnyRequiredFlag(material, requiredFlags));

            if (_hideEngineDrivenUniforms && isEngine)
                continue;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(member.Name);
            if (isEngine)
            {
                ImGui.SameLine();
                ImGui.TextColored(EngineTagColor, "(engine)");
                if (ImGui.IsItemHovered())
                {
                    if (isRuntimeDriven)
                        ImGui.SetTooltip($"Driven by engine pipeline: {runtimeProvider}");
                    else
                    {
                        string provider = FormatFlagNames(requiredFlags);
                        ImGui.SetTooltip(flagsActive
                            ? $"Driven by engine via RequiredEngineUniforms: {provider}"
                            : $"Enable {provider} in Render Options > RequiredEngineUniforms to drive this uniform");
                    }
                }
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(member.TypeLabel);

            ImGui.TableSetColumnIndex(2);
            ShaderVar? param = FindParameter(parameters, member.Name, block.BlockName, block.InstanceName);
            if (isEngine && flagsActive && param is null)
            {
                ImGui.TextColored(EngineActiveColor, "Driven by engine");
            }
            else if (isEngine && !flagsActive && param is null)
            {
                string provider = FormatFlagNames(requiredFlags);
                ImGui.TextColored(EngineMissingFlagColor, $"Enable {provider}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Set RequiredEngineUniforms |= {provider} in Render Options for the engine to provide this value.");
            }
            else if (param is null)
            {
                string preferredName = ResolveParameterName(member.Name, block.BlockName, block.InstanceName);
                if (ImGui.SmallButton($"Create##Block_{preferredName}"))
                {
                    if (TryCreateMaterialParameter(material, member.Type, preferredName))
                        parameters[preferredName] = material.Parameters[^1];
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Create a local material parameter named '{preferredName}' for this uniform-block member.");
            }
            else
            {
                ImGui.PushID(param.GetHashCode());
                DrawShaderParameterControl(material, param);
                if (isEngine && flagsActive && ImGui.IsItemHovered())
                    ImGui.SetTooltip("This value is overwritten at render time by the engine.");
                ImGui.PopID();
            }

            ImGui.TableSetColumnIndex(3);
            if (isEngine && flagsActive && param is null)
            {
                ImGui.TextColored(EngineActiveColor, "Engine");
            }
            else if (isEngine && !flagsActive && param is null)
            {
                ImGui.TextColored(EngineMissingFlagColor, "Needs engine flags");
            }
            else
            {
                DrawParameterDriveCell(material, param, ResolveParameterName(member.Name, block.BlockName, block.InstanceName), member.TypeLabel);
            }
        }

        ImGui.EndTable();
    }

    private static ShaderVar? FindParameter(Dictionary<string, ShaderVar> parameters, string memberName, string? blockName = null, string? instanceName = null)
    {
        if (parameters.TryGetValue(memberName, out var param))
            return param;

        if (!string.IsNullOrWhiteSpace(blockName) && parameters.TryGetValue($"{blockName}.{memberName}", out param))
            return param;

        if (!string.IsNullOrWhiteSpace(instanceName) && parameters.TryGetValue($"{instanceName}.{memberName}", out param))
            return param;

        return null;
    }

    private static void DrawShaderParameterControl(XRMaterial material, ShaderVar param)
    {
        switch (param)
        {
            case ShaderFloat f:
                {
                    float value = f.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat("##Float", ref value, 0.01f))
                    {
                        f.SetValue(value);
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderArrayBase a:
                ImGui.TextDisabled($"Array ({a.Length} elements)");
                break;
            case ShaderInt i:
                {
                    int value = i.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragInt("##Int", ref value))
                    {
                        i.SetValue(value);
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderUInt ui:
                {
                    uint value = ui.Value;
                    int intValue = (int)value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragInt("##UInt", ref intValue, 1.0f, 0, int.MaxValue))
                    {
                        ui.SetValue((uint)intValue);
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderBool b:
                {
                    bool value = b.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.Checkbox("##Bool", ref value))
                    {
                        b.SetValue(value);
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderVector2 v2:
                {
                    Vector2 value = v2.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2("##Vec2", ref value, 0.01f))
                    {
                        v2.SetValue(value);
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderVector3 v3:
                {
                    Vector3 value = v3.Value;
                    ImGui.SetNextItemWidth(-1f);
                    bool changed = LooksLikeColorParameter(param.Name, param)
                        ? ImGui.ColorEdit3("##Vec3", ref value)
                        : ImGui.DragFloat3("##Vec3", ref value, 0.01f);
                    if (changed)
                    {
                        v3.SetValue(value);
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderVector4 v4:
                {
                    Vector4 value = v4.Value;
                    ImGui.SetNextItemWidth(-1f);
                    bool changed = LooksLikeColorParameter(param.Name, param)
                        ? ImGui.ColorEdit4("##Vec4", ref value)
                        : ImGui.DragFloat4("##Vec4", ref value, 0.01f);
                    if (changed)
                    {
                        v4.SetValue(value);
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderMat4:
                ImGui.TextDisabled("Matrix4x4 (edit not implemented)");
                break;
            default:
                ImGui.TextDisabled(param.GetType().Name);
                break;
        }
    }

    private static void DrawSamplerTextureField(XRMaterial material, SamplerBindingEntry binding)
    {
        if (binding.EngineBinding is { } engineBinding)
        {
            if (engineBinding.IsDriven(material))
            {
                ImGui.TextColored(EngineActiveColor, "Driven by engine");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(GetEngineSamplerTooltip(material, engineBinding));
            }
            else
            {
                string provider = FormatFlagNames(engineBinding.RequiredFlags);
                ImGui.TextColored(EngineMissingFlagColor, $"Enable {provider}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(GetEngineSamplerTooltip(material, engineBinding));
            }
            return;
        }

        XRTexture? currentTexture = binding.AssignedTexture;
        SamplerEntry sampler = binding.Sampler;

        switch (sampler.Kind)
        {
            case SamplerKind.Texture1D:
                ImGuiAssetUtilities.DrawAssetField("Texture1D", currentTexture as XRTexture1D, asset => AssignTextureToBinding(material, binding, asset));
                break;
            case SamplerKind.Texture1DArray:
                ImGuiAssetUtilities.DrawAssetField("Texture1DArray", currentTexture as XRTexture1DArray, asset => AssignTextureToBinding(material, binding, asset));
                break;
            case SamplerKind.Texture2D:
                ImGuiAssetUtilities.DrawAssetField("Texture2D", currentTexture as XRTexture2D, asset => AssignTextureToBinding(material, binding, asset));
                break;
            case SamplerKind.Texture2DArray:
                ImGuiAssetUtilities.DrawAssetField("Texture2DArray", currentTexture as XRTexture2DArray, asset => AssignTextureToBinding(material, binding, asset));
                break;
            case SamplerKind.Texture3D:
                ImGuiAssetUtilities.DrawAssetField("Texture3D", currentTexture as XRTexture3D, asset => AssignTextureToBinding(material, binding, asset));
                break;
            case SamplerKind.TextureCube:
                ImGuiAssetUtilities.DrawAssetField("TextureCube", currentTexture as XRTextureCube, asset => AssignTextureToBinding(material, binding, asset));
                break;
            case SamplerKind.TextureCubeArray:
                ImGuiAssetUtilities.DrawAssetField("TextureCubeArray", currentTexture as XRTextureCubeArray, asset => AssignTextureToBinding(material, binding, asset));
                break;
            case SamplerKind.TextureRectangle:
                ImGuiAssetUtilities.DrawAssetField("TextureRectangle", currentTexture as XRTextureRectangle, asset => AssignTextureToBinding(material, binding, asset));
                break;
            case SamplerKind.TextureBuffer:
                ImGuiAssetUtilities.DrawAssetField("TextureBuffer", currentTexture as XRTextureBuffer, asset => AssignTextureToBinding(material, binding, asset));
                break;
            default:
                ImGui.TextDisabled("Unsupported sampler type");
                break;
        }
    }

    private static void EnsureTextureSlots(XRMaterial material, int count)
    {
        while (material.Textures.Count < count)
            material.Textures.Add(null);
    }

    private static List<SamplerBindingEntry> ResolveSamplerBindings(XRMaterial material, IReadOnlyList<SamplerEntry> samplers)
    {
        var bindings = new List<SamplerBindingEntry>(samplers.Count);
        for (int i = 0; i < samplers.Count; i++)
        {
            SamplerEntry sampler = samplers[i];
            string baseSamplerName = GetBaseSamplerName(sampler.Name);
            EngineSamplerBindingInfo? engineBinding = TryGetEngineSamplerBinding(baseSamplerName);
            int? materialTextureSlot = TryGetTextureSlotForSampler(material, sampler.Name, baseSamplerName);
            XRTexture? assignedTexture = materialTextureSlot is int slot
                && slot >= 0
                && slot < material.Textures.Count
                    ? material.Textures[slot]
                    : null;

            XRTexture? previewTexture = assignedTexture;
            if (previewTexture is null && engineBinding?.IsDriven(material) == true)
                previewTexture = TryResolveEngineSamplerPreview(baseSamplerName);

            bindings.Add(new SamplerBindingEntry(
                sampler,
                assignedTexture,
                previewTexture,
                materialTextureSlot,
                i,
                engineBinding));
        }

        return bindings;
    }

    private static int? TryGetTextureSlotForSampler(XRMaterial material, string samplerName, string baseSamplerName)
    {
        if (TryFindNamedTextureSlot(material, samplerName, out int namedSlot))
            return namedSlot;

        if (!samplerName.Equals(baseSamplerName, StringComparison.Ordinal)
            && TryFindNamedTextureSlot(material, baseSamplerName, out namedSlot))
        {
            return namedSlot;
        }

        if (TryParseDefaultTextureSlot(baseSamplerName, out int defaultSlot))
        {
            if (defaultSlot >= material.Textures.Count)
                return defaultSlot;

            XRTexture? existingTexture = material.Textures[defaultSlot];
            if (existingTexture is null
                || string.IsNullOrWhiteSpace(existingTexture.SamplerName)
                || existingTexture.SamplerName.Equals(baseSamplerName, StringComparison.Ordinal))
            {
                return defaultSlot;
            }
        }

        return null;
    }

    private static bool TryFindNamedTextureSlot(XRMaterial material, string samplerName, out int slotIndex)
    {
        for (int i = 0; i < material.Textures.Count; i++)
        {
            XRTexture? texture = material.Textures[i];
            if (texture?.SamplerName?.Equals(samplerName, StringComparison.Ordinal) == true)
            {
                slotIndex = i;
                return true;
            }
        }

        slotIndex = -1;
        return false;
    }

    private static bool TryParseDefaultTextureSlot(string samplerName, out int slotIndex)
    {
        slotIndex = -1;
        if (!samplerName.StartsWith("Texture", StringComparison.Ordinal))
            return false;

        string suffix = samplerName["Texture".Length..];
        return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out slotIndex);
    }

    private static string GetBaseSamplerName(string samplerName)
    {
        int bracketIndex = samplerName.IndexOf('[');
        return bracketIndex >= 0 ? samplerName[..bracketIndex] : samplerName;
    }

    private static EngineSamplerBindingInfo? TryGetEngineSamplerBinding(string samplerName)
    {
        EUniformRequirements requiredFlags = UniformRequirementsDetection.GetAllProviders(samplerName);
        if (requiredFlags != EUniformRequirements.None)
            return new EngineSamplerBindingInfo($"RequiredEngineUniforms: {FormatFlagNames(requiredFlags)}", requiredFlags);

        if (RuntimeDrivenSamplerProviders.TryGetValue(samplerName, out string? provider))
            return new EngineSamplerBindingInfo(provider, EUniformRequirements.None);

        return null;
    }

    private static string GetEngineSamplerTooltip(XRMaterial material, EngineSamplerBindingInfo binding)
    {
        if (binding.RequiredFlags == EUniformRequirements.None)
            return $"Driven by engine via {binding.ProviderDescription}";

        return binding.IsDriven(material)
            ? $"Driven by engine via {binding.ProviderDescription}"
            : $"Enable {FormatFlagNames(binding.RequiredFlags)} in Render Options > RequiredEngineUniforms to drive this sampler";
    }

    private static XRTexture? TryResolveEngineSamplerPreview(string samplerName)
    {
        XRRenderPipelineInstance? pipelineInstance = Engine.Rendering.State.CurrentRenderingPipeline;
        if (pipelineInstance?.Pipeline is null)
            return null;

        switch (samplerName)
        {
            case EngineShaderBindingNames.Samplers.BRDF:
                return RenderPipeline.TryGetTexture(EngineShaderBindingNames.Samplers.BRDF, out XRTexture? brdfTexture) ? brdfTexture : null;

            case EngineShaderBindingNames.Samplers.AmbientOcclusionTexture:
                return RenderPipeline.TryGetTexture(DefaultRenderPipeline.AmbientOcclusionIntensityTextureName, out XRTexture? aoTexture) ? aoTexture : null;

            case EngineShaderBindingNames.Samplers.IrradianceArray:
                if (pipelineInstance.Pipeline is DefaultRenderPipeline defaultPipeline)
                    return defaultPipeline.ProbeIrradianceArray;
                if (pipelineInstance.Pipeline is DefaultRenderPipeline2 defaultPipeline2)
                    return defaultPipeline2.ProbeIrradianceArray;
                return null;

            case EngineShaderBindingNames.Samplers.PrefilterArray:
                if (pipelineInstance.Pipeline is DefaultRenderPipeline defaultPipeline3)
                    return defaultPipeline3.ProbePrefilterArray;
                if (pipelineInstance.Pipeline is DefaultRenderPipeline2 defaultPipeline4)
                    return defaultPipeline4.ProbePrefilterArray;
                return null;

            default:
                return null;
        }
    }

    private static void DrawTexturePreviewCell(XRTexture? texture, float maxSize)
    {
        if (texture is null)
        {
            ImGui.TextDisabled("<none>");
            return;
        }

        if (!TryGetTexturePreviewData(texture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? reason))
        {
            ImGui.TextDisabled(reason ?? "Preview unavailable");
            return;
        }

        float scale = MathF.Min(maxSize / displaySize.X, maxSize / displaySize.Y);
        if (scale < 1f)
            displaySize *= scale;

        ImGui.Image(handle, displaySize);
        if (pixelSize.X > 0 && pixelSize.Y > 0)
            ImGui.TextDisabled($"{(int)pixelSize.X} x {(int)pixelSize.Y}");
    }

    private static bool TryGetTexturePreviewData(
        XRTexture texture,
        out nint handle,
        out Vector2 displaySize,
        out Vector2 pixelSize,
        out string? failureReason)
    {
        pixelSize = GetTexturePixelSize(texture);
        displaySize = GetPreviewSize(pixelSize);
        handle = nint.Zero;
        failureReason = null;

        if (!Engine.IsRenderThread)
        {
            failureReason = "Preview unavailable off render thread";
            return false;
        }

        if (AbstractRenderer.Current is VulkanRenderer vkRenderer)
        {
            IntPtr textureId = vkRenderer.RegisterImGuiTexture(texture);
            if (textureId == IntPtr.Zero)
            {
                failureReason = "Texture not uploaded";
                return false;
            }

            handle = (nint)textureId;
            return true;
        }

        if (AbstractRenderer.Current is OpenGLRenderer renderer)
        {
            if (texture is not XRTexture2D tex2D)
            {
                failureReason = $"{texture.GetType().Name} preview not supported";
                return false;
            }

            var apiTexture = renderer.GenericToAPI<GLTexture2D>(tex2D);
            if (apiTexture is null)
            {
                failureReason = "Texture not uploaded";
                return false;
            }

            uint binding = apiTexture.BindingId;
            if (binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || binding == 0)
            {
                failureReason = "Texture not ready";
                return false;
            }

            handle = (nint)binding;
            return true;
        }

        failureReason = "Preview requires OpenGL or Vulkan renderer";
        return false;
    }

    private static Vector2 GetTexturePixelSize(XRTexture texture)
    {
        return texture switch
        {
            XRTexture2D tex2D => new Vector2(tex2D.Width, tex2D.Height),
            _ => new Vector2(texture.WidthHeightDepth.X, texture.WidthHeightDepth.Y),
        };
    }

    private static Vector2 GetPreviewSize(Vector2 pixelSize)
    {
        float width = pixelSize.X;
        float height = pixelSize.Y;

        if (width <= 0f || height <= 0f)
            return new Vector2(TexturePreviewFallbackEdge, TexturePreviewFallbackEdge);

        float maxDimension = MathF.Max(width, height);
        if (maxDimension <= TexturePreviewMaxEdge)
            return new Vector2(width, height);

        float scale = TexturePreviewMaxEdge / maxDimension;
        return new Vector2(width * scale, height * scale);
    }

    private static ParsedShaderData ParseShaderSource(string source)
    {
        string cleaned = StripComments(source);
        var blocks = ParseUniformBlocks(cleaned);
        string sourceWithoutBlocks = UniformBlockRegex().Replace(cleaned, string.Empty);
        var uniforms = ParseUniforms(sourceWithoutBlocks);

        return new ParsedShaderData(uniforms, blocks);
    }

    private static IReadOnlyList<UniformEntry> ParseUniforms(string source)
    {
        var results = new List<UniformEntry>();
        foreach (Match match in UniformRegex().Matches(source))
        {
            string type = match.Groups["type"].Value;
            if (IsSamplerType(type))
                continue;

            string name = match.Groups["name"].Value;
            int arraySize = ParseArraySize(match.Groups["array"].Value);
            results.Add(new UniformEntry(name, type, arraySize));
        }

        return results;
    }

    private static IReadOnlyList<UniformBlockEntry> ParseUniformBlocks(string source)
    {
        var results = new List<UniformBlockEntry>();
        foreach (Match match in UniformBlockRegex().Matches(source))
        {
            string blockName = match.Groups["block"].Value;
            string body = match.Groups["body"].Value;
            string instanceName = match.Groups["instance"].Value;

            var members = new List<UniformEntry>();
            foreach (Match memberMatch in BlockMemberRegex().Matches(body))
            {
                string type = memberMatch.Groups["type"].Value;
                string name = memberMatch.Groups["name"].Value;
                int arraySize = ParseArraySize(memberMatch.Groups["array"].Value);
                members.Add(new UniformEntry(name, type, arraySize));
            }

            results.Add(new UniformBlockEntry(blockName, instanceName, members));
        }

        return results;
    }

    private static List<SamplerEntry> CollectSamplerDefinitions(XRMaterial material)
    {
        var samplers = new List<SamplerEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int shaderIndex = 0; shaderIndex < material.Shaders.Count; shaderIndex++)
        {
            XRShader shader = material.Shaders[shaderIndex];
            if (shader is null)
                continue;

            string text = shader.GetResolvedSource();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            string cleaned = StripComments(text);
            foreach (Match match in UniformRegex().Matches(cleaned))
            {
                string type = match.Groups["type"].Value;
                if (!IsSamplerType(type))
                    continue;

                string name = match.Groups["name"].Value;
                int arraySize = ParseArraySize(match.Groups["array"].Value);

                if (!seen.Add(name))
                    continue;

                var kind = MapSamplerKind(type);
                if (arraySize > 0)
                {
                    for (int i = 0; i < arraySize; i++)
                    {
                        string indexedName = $"{name}[{i}]";
                        string typeLabel = $"{type}[{arraySize}]";
                        samplers.Add(new SamplerEntry(indexedName, typeLabel, kind, arraySize));
                    }
                }
                else
                {
                    string typeLabel = type;
                    samplers.Add(new SamplerEntry(name, typeLabel, kind, arraySize));
                }
            }
        }

        return samplers;
    }

    private static bool IsSamplerType(string type)
        => type.StartsWith("sampler", StringComparison.OrdinalIgnoreCase);

    private static SamplerKind MapSamplerKind(string samplerType)
    {
        return samplerType switch
        {
            "sampler1D" => SamplerKind.Texture1D,
            "sampler1DArray" => SamplerKind.Texture1DArray,
            "sampler2D" => SamplerKind.Texture2D,
            "sampler2DShadow" => SamplerKind.Texture2D,
            "sampler2DArray" => SamplerKind.Texture2DArray,
            "sampler2DArrayShadow" => SamplerKind.Texture2DArray,
            "sampler3D" => SamplerKind.Texture3D,
            "samplerCube" => SamplerKind.TextureCube,
            "samplerCubeShadow" => SamplerKind.TextureCube,
            "samplerCubeArray" => SamplerKind.TextureCubeArray,
            "sampler2DRect" => SamplerKind.TextureRectangle,
            "samplerBuffer" => SamplerKind.TextureBuffer,
            _ => SamplerKind.Unknown,
        };
    }

    private static int ParseArraySize(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    private static string StripComments(string source)
    {
        string withoutBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string FormatShaderHeader(XRShader shader, int index, string suffix)
    {
        string name = shader?.Name ?? shader?.Source?.FilePath ?? $"Shader {index}";
        return $"{name}##{suffix}_{index}";
    }

    private sealed record ParsedShaderData(
        IReadOnlyList<UniformEntry> Uniforms,
        IReadOnlyList<UniformBlockEntry> Blocks);

    private sealed record UniformEntry(string Name, string Type, int ArraySize)
    {
        public string TypeLabel => ArraySize > 0 ? $"{Type}[{ArraySize}]" : Type;
    }

    private sealed record UniformBlockEntry(string BlockName, string InstanceName, List<UniformEntry> Members);

    private sealed record SamplerEntry(string Name, string TypeLabel, SamplerKind Kind, int ArraySize);

    private sealed record SamplerBindingEntry(
        SamplerEntry Sampler,
        XRTexture? AssignedTexture,
        XRTexture? PreviewTexture,
        int? MaterialTextureSlot,
        int FallbackTextureSlot,
        EngineSamplerBindingInfo? EngineBinding);

    private sealed record EngineSamplerBindingInfo(string ProviderDescription, EUniformRequirements RequiredFlags)
    {
        public bool IsDriven(XRMaterial material)
            => RequiredFlags == EUniformRequirements.None || HasAnyRequiredFlag(material, RequiredFlags);
    }

    private enum SamplerKind
    {
        Unknown,
        Texture1D,
        Texture1DArray,
        Texture2D,
        Texture2DArray,
        Texture3D,
        TextureCube,
        TextureCubeArray,
        TextureRectangle,
        TextureBuffer,
    }

    [GeneratedRegex(@"(?:layout\s*\([^\)]*\)\s*)?uniform\s+(?<block>\w+)\s*\{(?<body>.*?)\}\s*(?<instance>\w+)?\s*;", RegexOptions.Singleline)]
    private static partial Regex UniformBlockRegex();

    [GeneratedRegex(@"(?<type>\w+)\s+(?<name>\w+)\s*(?:\[(?<array>\d+)\])?\s*;")]
    private static partial Regex BlockMemberRegex();

    [GeneratedRegex(@"(?:layout\s*\([^\)]*\)\s*)?uniform\s+(?<type>\w+)\s+(?<name>\w+)\s*(?:\[(?<array>\d+)\])?\s*;", RegexOptions.Singleline)]
    private static partial Regex UniformRegex();
}

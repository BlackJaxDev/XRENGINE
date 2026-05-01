using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using XREngine.Editor.ComponentEditors;
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
    private const float MaterialPreviewMaxEdge = 176.0f;
    private static readonly string[] MaterialInspectorTabLabels = ["Properties", "Surface", "Shaders", "Advanced"];

    private static readonly string[] PromotedSamplerHints =
    [
        "maintex",
        "maintexture",
        "basemap",
        "basecolor",
        "basecolormap",
        "albedo",
        "normal",
        "normalmap",
        "bump",
        "bumpmap",
        "orm",
        "roughness",
        "metallic",
        "occlusion",
        "mask",
        "emission",
        "emissionmap"
    ];
    private static readonly ConditionalWeakTable<XRMaterial, MaterialInspectorUiState> MaterialUiStates = new();

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
        [nameof(EEngineUniform.ModelMatrix)] = "Per-draw mesh transform",
        [nameof(EEngineUniform.PrevModelMatrix)] = "Per-draw mesh transform history",
        [nameof(EEngineUniform.BillboardMode)] = "Per-draw billboard state",
        [nameof(EEngineUniform.VRMode)] = "Per-draw stereo state",
        [EngineShaderBindingNames.Uniforms.PpllMaxNodes] = "Per-pixel linked list transparency",
        [EngineShaderBindingNames.Uniforms.DepthPeelLayerIndex] = "Exact transparency depth peeling",
        [EngineShaderBindingNames.Uniforms.DepthPeelEpsilon] = "Exact transparency depth peeling"
    };

    private static readonly Vector4 EngineTagColor = new(0.40f, 0.75f, 0.95f, 1.0f);
    private static readonly Vector4 EngineActiveColor = new(0.30f, 0.85f, 0.55f, 1.0f);
    private static readonly Vector4 EngineMissingFlagColor = new(0.95f, 0.75f, 0.25f, 1.0f);
    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.65f, 0.2f, 1.0f);
    private static readonly Vector4 MaterialPreviewBorderColor = new(0.82f, 0.82f, 0.88f, 0.95f);

    private static bool _hideEngineDrivenUniforms;

    private sealed class MaterialInspectorUiState
    {
        public string AdvancedFilter = string.Empty;
        public bool ShowDisabledUberFeatures;
    }

    private enum MaterialPreviewSurface
    {
        Sphere,
        Cube,
        Plane,
    }

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

        MaterialInspectorUiState uiState = MaterialUiStates.GetValue(material, static _ => new MaterialInspectorUiState());
        HandleMaterialInspectorHotkeys(material);
        DrawHeader(material);

        int selectedTab = DrawInspectorTabSelector("MaterialInspectorTabs", Engine.EditorPreferences?.MaterialInspectorTabIndex ?? 0, MaterialInspectorTabLabels);
        switch (selectedTab)
        {
            case 0:
                DrawMaterialPreviewSection(material);
                DrawPromotedSamplerPanel(material);
                DrawUberInspector(material);
                if (ImGui.CollapsingHeader("Raw Asset Fields"))
                    DrawDefaultInspector(material, visitedObjects);
                break;
            case 1:
                DrawTransparencySettings(material);
                DrawRenderOptions(material, visitedObjects);
                break;
            case 2:
                DrawShaderStageSection(material);
                break;
            case 3:
                ImGui.SetNextItemWidth(MathF.Min(360.0f, ImGui.GetContentRegionAvail().X));
                ImGui.InputTextWithHint("##MaterialAdvancedFilter", "Filter uniforms, blocks, or samplers...", ref uiState.AdvancedFilter, 128u);
                if (!string.IsNullOrWhiteSpace(uiState.AdvancedFilter))
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Clear##MaterialAdvancedFilter"))
                        uiState.AdvancedFilter = string.Empty;
                }

                ImGui.Checkbox("Hide engine-driven", ref _hideEngineDrivenUniforms);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Hide uniforms and samplers that are automatically driven by the engine.");

                DrawUniforms(material, uiState.AdvancedFilter);
                DrawUniformBlocks(material, uiState.AdvancedFilter);
                DrawSamplerList(material, uiState.AdvancedFilter);
                break;
        }
    }

    private static int DrawInspectorTabSelector(string tableId, int selectedIndex, IReadOnlyList<string> labels)
    {
        if (!ImGui.BeginTable(tableId, labels.Count, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
            return selectedIndex;

        for (int i = 0; i < labels.Count; i++)
        {
            ImGui.TableNextColumn();
            bool selected = selectedIndex == i;
            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, EngineActiveColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.38f, 0.93f, 0.63f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.22f, 0.72f, 0.46f, 1.0f));
            }

            if (ImGui.Button(labels[i], new Vector2(-1.0f, 0.0f)))
                selectedIndex = i;

            if (selected)
                ImGui.PopStyleColor(3);
        }

        ImGui.EndTable();
        ImGui.Spacing();

        if (Engine.EditorPreferences is not null && Engine.EditorPreferences.MaterialInspectorTabIndex != selectedIndex)
            Engine.EditorPreferences.MaterialInspectorTabIndex = selectedIndex;

        return selectedIndex;
    }

    private static void HandleMaterialInspectorHotkeys(XRMaterial material)
    {
        var io = ImGui.GetIO();
        if (!io.KeyCtrl || io.WantTextInput || !ImGui.IsKeyPressed(ImGuiKey.S, false))
            return;

        SaveMaterialAsset(material);
    }

    private static void SaveMaterialAsset(XRMaterial material)
    {
        if (Engine.Assets is null || string.IsNullOrWhiteSpace(material.FilePath))
            return;

        Engine.Assets.Save(material, bypassJobThread: true);
    }

    private static void ReloadMaterialAsset(XRMaterial material)
    {
        if (string.IsNullOrWhiteSpace(material.FilePath))
            return;

        material.Reload();
    }

    private static void DrawHeader(XRMaterial material)
    {
        ImGui.TextUnformatted(material.Name ?? "<unnamed material>");
        ImGui.TextDisabled(material.FilePath ?? "<unsaved asset>");

        bool hasPath = !string.IsNullOrWhiteSpace(material.FilePath);

        if (hasPath)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path##XRMaterial"))
                ImGui.SetClipboardText(material.FilePath);

            ImGui.SameLine();
            using (new ImGuiDisabledScope(Engine.Assets is null))
            {
                if (ImGui.SmallButton("Save##XRMaterial"))
                    SaveMaterialAsset(material);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Save this material to disk. Shortcut: Ctrl+S");

            ImGui.SameLine();
            if (ImGui.SmallButton("Revert##XRMaterial"))
                ReloadMaterialAsset(material);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reload this material from disk and discard unsaved changes.");
        }

        ImGui.SameLine();
        ImGui.TextColored(material.IsDirty ? DirtyBadgeColor : EngineActiveColor, material.IsDirty ? "Modified" : "Saved");

        ImGui.TextDisabled($"Render Pass: {DescribeRenderPass(material.RenderPass)}");

        ETransparencyMode inferred = material.InferTransparencyMode();
        if (inferred != material.TransparencyMode)
            ImGui.TextDisabled($"Inferred: {inferred} | Explicit: {material.TransparencyMode}");

        ImGui.Separator();
    }

    private static void DrawMaterialPreviewSection(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Preview", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        XRTexture? texture = TrySelectMaterialPreviewTexture(material);
        if (texture is null)
        {
            ImGui.TextDisabled("No material texture available to preview.");
            return;
        }

        MaterialPreviewSurface surface = GetSelectedMaterialPreviewSurface();
        if (ImGui.BeginCombo("Preview Surface", surface.ToString()))
        {
            foreach (MaterialPreviewSurface option in Enum.GetValues<MaterialPreviewSurface>())
            {
                bool selected = option == surface;
                if (ImGui.Selectable(option.ToString(), selected) && !selected)
                    SetSelectedMaterialPreviewSurface(option);

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (!TryGetTexturePreviewData(texture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason))
        {
            ImGui.TextDisabled(failureReason ?? "Preview unavailable.");
            return;
        }

        float maxDimension = MathF.Max(displaySize.X, displaySize.Y);
        if (maxDimension > 0.0f && maxDimension < MaterialPreviewMaxEdge)
        {
            float scale = MaterialPreviewMaxEdge / maxDimension;
            displaySize *= scale;
        }

        Vector2 start = ImGui.GetCursorScreenPos();
        ImGui.Image(handle, displaySize);
        DrawMaterialPreviewFrame(start, displaySize, surface);

        bool openLarge = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        if (ImGui.SmallButton("View Larger##MaterialPreview"))
            openLarge = true;

        if (openLarge)
            ComponentEditorLayout.RequestPreviewDialog($"{material.Name ?? "Material"} Preview", handle, pixelSize, flipVertically: false);

        ImGui.TextDisabled($"Preview Source: {texture.SamplerName ?? texture.Name ?? texture.GetType().Name}");
        ImGui.TextDisabled($"{(int)pixelSize.X} x {(int)pixelSize.Y}");
    }

    private static void DrawMaterialPreviewFrame(Vector2 start, Vector2 size, MaterialPreviewSurface surface)
    {
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 end = start + size;
        uint borderColor = ImGui.ColorConvertFloat4ToU32(MaterialPreviewBorderColor);

        switch (surface)
        {
            case MaterialPreviewSurface.Sphere:
                {
                    Vector2 center = (start + end) * 0.5f;
                    float radius = MathF.Min(size.X, size.Y) * 0.5f;
                    drawList.AddCircle(center, radius, borderColor, 48, 2.0f);
                    break;
                }
            case MaterialPreviewSurface.Cube:
                drawList.AddRect(start, end, borderColor, 8.0f, ImDrawFlags.None, 2.0f);
                drawList.AddLine(new Vector2(end.X, start.Y), new Vector2(end.X - 12.0f, start.Y + 12.0f), borderColor, 2.0f);
                drawList.AddLine(new Vector2(end.X, start.Y), new Vector2(start.X + 12.0f, start.Y), borderColor, 2.0f);
                break;
            default:
                drawList.AddRect(start, end, borderColor, 0.0f, ImDrawFlags.None, 2.0f);
                break;
        }
    }

    private static MaterialPreviewSurface GetSelectedMaterialPreviewSurface()
    {
        int raw = Engine.EditorPreferences?.MaterialPreviewSurfaceIndex ?? 0;
        raw = Math.Clamp(raw, 0, Enum.GetValues<MaterialPreviewSurface>().Length - 1);
        return (MaterialPreviewSurface)raw;
    }

    private static void SetSelectedMaterialPreviewSurface(MaterialPreviewSurface surface)
    {
        if (Engine.EditorPreferences is not null)
            Engine.EditorPreferences.MaterialPreviewSurfaceIndex = (int)surface;
    }

    private static void DrawPromotedSamplerPanel(XRMaterial material)
    {
        List<SamplerBindingEntry> promotedBindings = GetPromotedSamplerBindings(material);
        if (promotedBindings.Count == 0)
            return;

        if (!ImGui.CollapsingHeader("Textures", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ShaderUiManifest? manifest = null;
        if (TryGetUberMaterialManifest(material, out _, out _, out ShaderUiManifest? resolvedManifest))
            manifest = resolvedManifest;

        if (!ImGui.BeginTable("PromotedSamplerTable", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Texture", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthFixed, 72.0f);
        ImGui.TableHeadersRow();

        foreach (SamplerBindingEntry binding in promotedBindings)
        {
            string displayName = manifest is not null && manifest.PropertyLookup.TryGetValue(binding.Sampler.Name, out ShaderUiProperty? property)
                ? property.DisplayName
                : binding.Sampler.Name;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(displayName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(binding.Sampler.Name);

            ImGui.TableSetColumnIndex(1);
            ImGui.PushID($"PromotedSampler_{binding.Sampler.Name}");
            DrawSamplerTextureField(material, binding);
            ImGui.PopID();

            ImGui.TableSetColumnIndex(2);
            DrawTexturePreviewCell(binding.PreviewTexture, 56.0f);
        }

        ImGui.EndTable();
    }

    private static List<SamplerBindingEntry> GetPromotedSamplerBindings(XRMaterial material)
    {
        List<SamplerBindingEntry> allBindings = ResolveSamplerBindings(material, CollectSamplerDefinitions(material));
        List<SamplerBindingEntry> promoted = [];

        foreach (SamplerBindingEntry binding in allBindings)
        {
            if (binding.EngineBinding is not null)
                continue;

            string normalized = NormalizeSamplerName(binding.Sampler.Name);
            if (Array.Exists(PromotedSamplerHints, hint => normalized.Contains(hint, StringComparison.Ordinal)))
                promoted.Add(binding);
        }

        if (promoted.Count > 0)
            return promoted;

        foreach (SamplerBindingEntry binding in allBindings)
        {
            if (binding.EngineBinding is null)
                promoted.Add(binding);

            if (promoted.Count >= 4)
                break;
        }

        return promoted;
    }

    private static XRTexture? TrySelectMaterialPreviewTexture(XRMaterial material)
    {
        foreach (SamplerBindingEntry binding in GetPromotedSamplerBindings(material))
        {
            if (binding.PreviewTexture is not null)
                return binding.PreviewTexture;
        }

        foreach (XRTexture? texture in material.Textures)
        {
            if (texture is not null)
                return texture;
        }

        return null;
    }

    private static string NormalizeSamplerName(string name)
        => name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

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

    private static void DrawUniforms(XRMaterial material, string? filter)
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
            UniformEntry[] filteredUniforms = parsed.Uniforms
                .Where(uniform => MatchesMaterialAdvancedFilter(filter, uniform.Name, uniform.TypeLabel))
                .ToArray();

            if (filteredUniforms.Length == 0)
                continue;

            anyUniforms = true;
            string header = FormatShaderHeader(shader, shaderIndex, "Uniforms");
            if (!ImGui.TreeNodeEx(header, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            DrawUniformTable(filteredUniforms, parameterLookup, material);
            ImGui.TreePop();
        }

        if (!anyUniforms)
            ImGui.TextDisabled("No uniform declarations found in assigned shaders.");
    }

    private static void DrawUniformBlocks(XRMaterial material, string? filter)
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
            List<UniformBlockEntry> filteredBlocks = [];
            foreach (UniformBlockEntry block in parsed.Blocks)
            {
                List<UniformEntry> filteredMembers = block.Members
                    .Where(member => MatchesMaterialAdvancedFilter(filter, block.BlockName, block.InstanceName, member.Name, member.TypeLabel))
                    .ToList();
                if (filteredMembers.Count > 0)
                    filteredBlocks.Add(block with { Members = filteredMembers });
            }

            if (filteredBlocks.Count == 0)
                continue;

            anyBlocks = true;
            string header = FormatShaderHeader(shader, shaderIndex, "Blocks");
            if (!ImGui.TreeNodeEx(header, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            foreach (var block in filteredBlocks)
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

    private static void DrawSamplerList(XRMaterial material, string? filter)
    {
        if (!ImGui.CollapsingHeader("Texture Samplers", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled("Click a texture field to inspect it inline. Replacing a texture also updates its sampler binding name to match the shader slot.");
        ImGui.Separator();

        List<SamplerEntry> samplers = CollectSamplerDefinitions(material)
            .Where(sampler => MatchesMaterialAdvancedFilter(filter, sampler.Name, sampler.TypeLabel))
            .ToList();
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

        DrawShaderParameterContextMenu(material, param);
    }

    private static void DrawShaderParameterContextMenu(XRMaterial material, ShaderVar param)
    {
        bool canCopy = TrySerializeShaderParameterValue(param, out string serializedValue);
        string? clipboardText = GetClipboardTextSafe();
        bool canPaste = CanApplyShaderParameterClipboard(param, clipboardText);

        if (!ImGui.BeginPopupContextItem($"ShaderParamContext_{param.GetHashCode()}"))
            return;

        if (ImGui.MenuItem("Copy Value", null, false, canCopy) && canCopy)
            ImGui.SetClipboardText(serializedValue);

        if (ImGui.MenuItem("Paste Value", null, false, canPaste))
            TryApplyShaderParameterClipboard(material, param, clipboardText);

        ImGui.EndPopup();
    }

    private static string? GetClipboardTextSafe()
    {
        try
        {
            return ImGui.GetClipboardText();
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private static bool TrySerializeShaderParameterValue(ShaderVar param, out string serialized)
    {
        serialized = param switch
        {
            ShaderFloat value => $"xreparam:float|{value.Value.ToString(CultureInfo.InvariantCulture)}",
            ShaderInt value => $"xreparam:int|{value.Value.ToString(CultureInfo.InvariantCulture)}",
            ShaderUInt value => $"xreparam:uint|{value.Value.ToString(CultureInfo.InvariantCulture)}",
            ShaderBool value => $"xreparam:bool|{(value.Value ? "1" : "0")}",
            ShaderVector2 value => $"xreparam:vec2|{value.Value.X.ToString(CultureInfo.InvariantCulture)},{value.Value.Y.ToString(CultureInfo.InvariantCulture)}",
            ShaderVector3 value => $"xreparam:vec3|{value.Value.X.ToString(CultureInfo.InvariantCulture)},{value.Value.Y.ToString(CultureInfo.InvariantCulture)},{value.Value.Z.ToString(CultureInfo.InvariantCulture)}",
            ShaderVector4 value => $"xreparam:vec4|{value.Value.X.ToString(CultureInfo.InvariantCulture)},{value.Value.Y.ToString(CultureInfo.InvariantCulture)},{value.Value.Z.ToString(CultureInfo.InvariantCulture)},{value.Value.W.ToString(CultureInfo.InvariantCulture)}",
            _ => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(serialized);
    }

    private static bool CanApplyShaderParameterClipboard(ShaderVar param, string? clipboard)
        => TryParseShaderParameterClipboard(param, clipboard, out _);

    private static bool TryApplyShaderParameterClipboard(XRMaterial material, ShaderVar param, string? clipboard)
    {
        if (!TryParseShaderParameterClipboard(param, clipboard, out object? parsedValue))
            return false;

        switch (param)
        {
            case ShaderFloat value when parsedValue is float floatValue:
                value.SetValue(floatValue);
                break;
            case ShaderInt value when parsedValue is int intValue:
                value.SetValue(intValue);
                break;
            case ShaderUInt value when parsedValue is uint uintValue:
                value.SetValue(uintValue);
                break;
            case ShaderBool value when parsedValue is bool boolValue:
                value.SetValue(boolValue);
                break;
            case ShaderVector2 value when parsedValue is Vector2 vec2Value:
                value.SetValue(vec2Value);
                break;
            case ShaderVector3 value when parsedValue is Vector3 vec3Value:
                value.SetValue(vec3Value);
                break;
            case ShaderVector4 value when parsedValue is Vector4 vec4Value:
                value.SetValue(vec4Value);
                break;
            default:
                return false;
        }

        material.MarkDirty();
        return true;
    }

    private static bool TryParseShaderParameterClipboard(ShaderVar param, string? clipboard, out object? parsedValue)
    {
        parsedValue = null;
        if (string.IsNullOrWhiteSpace(clipboard) || !clipboard.StartsWith("xreparam:", StringComparison.Ordinal))
            return false;

        int separatorIndex = clipboard.IndexOf('|');
        if (separatorIndex < 0)
            return false;

        string kind = clipboard[9..separatorIndex];
        string payload = clipboard[(separatorIndex + 1)..];

        switch (param)
        {
            case ShaderFloat when string.Equals(kind, "float", StringComparison.Ordinal) &&
                                  float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue):
                parsedValue = floatValue;
                return true;
            case ShaderInt when string.Equals(kind, "int", StringComparison.Ordinal) &&
                                int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue):
                parsedValue = intValue;
                return true;
            case ShaderUInt when string.Equals(kind, "uint", StringComparison.Ordinal) &&
                                 uint.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uintValue):
                parsedValue = uintValue;
                return true;
            case ShaderBool when string.Equals(kind, "bool", StringComparison.Ordinal):
                parsedValue = payload == "1" || payload.Equals("true", StringComparison.OrdinalIgnoreCase);
                return true;
            case ShaderVector2 when string.Equals(kind, "vec2", StringComparison.Ordinal) && TryParseVectorPayload(payload, 2, out float[]? vec2) && vec2 is not null:
                parsedValue = new Vector2(vec2[0], vec2[1]);
                return true;
            case ShaderVector3 when string.Equals(kind, "vec3", StringComparison.Ordinal) && TryParseVectorPayload(payload, 3, out float[]? vec3) && vec3 is not null:
                parsedValue = new Vector3(vec3[0], vec3[1], vec3[2]);
                return true;
            case ShaderVector4 when string.Equals(kind, "vec4", StringComparison.Ordinal) && TryParseVectorPayload(payload, 4, out float[]? vec4) && vec4 is not null:
                parsedValue = new Vector4(vec4[0], vec4[1], vec4[2], vec4[3]);
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseVectorPayload(string payload, int componentCount, out float[]? values)
    {
        string[] parts = payload.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != componentCount)
        {
            values = null;
            return false;
        }

        values = new float[componentCount];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                values = null;
                return false;
            }
        }

        return true;
    }

    private static bool MatchesMaterialAdvancedFilter(string? filter, params string?[] candidates)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        string needle = filter.Trim();
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool RemoveMaterialParameter(XRMaterial material, string parameterName)
    {
        ShaderVar[] parameters = material.Parameters ?? [];
        int index = Array.FindIndex(parameters, x => string.Equals(x?.Name, parameterName, StringComparison.Ordinal));
        if (index < 0)
            return false;

        ShaderVar[] next = [.. parameters];
        Array.Copy(next, index + 1, next, index, next.Length - index - 1);
        Array.Resize(ref next, next.Length - 1);
        material.Parameters = next;
        material.MarkDirty();
        return true;
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

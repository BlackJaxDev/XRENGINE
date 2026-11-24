using ImGuiNET;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector
{
    private const string CustomUberRenderPresetLabel = "Custom";

    /// <summary>
    /// Draws the render preset selector of an Uber material. A preset sets the transparency mode, blend state,
    /// depth write, alpha cutoff, and shader blend mode in one undo step.
    /// </summary>
    private static void DrawUberRenderPreset(XRMaterial material)
    {
        bool detected = UberRenderPresets.TryDetect(material, out EUberRenderPreset current);
        string preview = detected
            ? UberRenderPresets.Get(current).DisplayName
            : CustomUberRenderPresetLabel;

        ImGui.SetNextItemWidth(-1.0f);
        if (!ImGui.BeginCombo("##UberRenderPreset", $"Render Preset: {preview}"))
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Sets the blend state, depth write, render pass, and alpha cutoff together.");
            return;
        }

        foreach (ref readonly UberRenderPresetDefinition definition in UberRenderPresets.All)
        {
            bool selected = detected && definition.Preset == current;
            if (ImGui.Selectable(definition.DisplayName, selected) && !selected)
                ApplyUberRenderPreset(material, definition.Preset);
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void ApplyUberRenderPreset(XRMaterial material, EUberRenderPreset preset)
    {
        // Keep the previous state for undo. The preset replaces the blend mode objects, so the previous
        // references stay unchanged.
        ETransparencyMode transparencyMode = material.TransparencyMode;
        float alphaCutoff = material.AlphaCutoff;
        int renderPass = material.RenderPass;
        RenderingParameters options = material.RenderOptions ??= new RenderingParameters();
        BlendMode? blend = options.BlendModeAllDrawBuffers;
        Dictionary<uint, BlendMode>? blendPerDrawBuffer = options.BlendModesPerDrawBuffer;
        ERenderParamUsage alphaToCoverage = options.AlphaToCoverage;
        bool updateDepth = options.DepthTest.UpdateDepth;
        int shaderMode = material.Parameter<ShaderInt>("_Mode")?.Value ?? 0;
        float premultiply = material.Parameter<ShaderFloat>("_AlphaPremultiply")?.Value ?? 0.0f;
        float forceOpaque = material.Parameter<ShaderFloat>("_AlphaForceOpaque")?.Value ?? 0.0f;

        void Restore()
        {
            // Restore the transparency mode first, because it resets the blend and depth state.
            material.TransparencyMode = transparencyMode;
            material.AlphaCutoff = alphaCutoff;
            RenderingParameters target = material.RenderOptions ??= new RenderingParameters();
            target.BlendModeAllDrawBuffers = blend;
            target.BlendModesPerDrawBuffer = blendPerDrawBuffer;
            target.AlphaToCoverage = alphaToCoverage;
            target.DepthTest.UpdateDepth = updateDepth;
            material.RenderPass = renderPass;
            material.Parameter<ShaderInt>("_Mode")?.SetValue(shaderMode);
            material.Parameter<ShaderFloat>("_AlphaPremultiply")?.SetValue(premultiply);
            material.Parameter<ShaderFloat>("_AlphaForceOpaque")?.SetValue(forceOpaque);
        }

        string name = UberRenderPresets.Get(preset).DisplayName;
        MaterialAuthoringTransaction transaction = new($"Set Render Preset: {name}");
        transaction.AddStructural(
            material,
            $"Set Render Preset: {name}",
            () => UberRenderPresets.Apply(material, preset),
            Restore,
            invalidatesVariant: true);
        if (!transaction.TryExecute(out MaterialAuthoringTransactionReport report))
            Debug.LogWarning($"The render preset was not applied: {string.Join("; ", report.Diagnostics)}");
    }
}

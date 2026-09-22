using ImGuiNET;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Controls explicitly shared by the Default and Advanced pipeline providers.
/// The generic camera inspector never assumes these rendering features exist.
/// </summary>
internal static class StandardPipelineEditorControls
{
    private static readonly EAntiAliasingMode[] AntiAliasingModes =
        [EAntiAliasingMode.None, EAntiAliasingMode.Msaa, EAntiAliasingMode.Fxaa, EAntiAliasingMode.Smaa,
         EAntiAliasingMode.Taa, EAntiAliasingMode.Tsr, EAntiAliasingMode.Dlaa];
    private static readonly string[] AntiAliasingModeNames = ["None", "MSAA", "FXAA", "SMAA", "TAA", "TSR", "DLAA"];
    private static readonly string[] HdrOutputModeNames = ["Use Global Setting", "SDR", "HDR"];
    private static readonly string[] DirectionalShadowModeNames = ["Non-Cascaded", "Cascaded"];

    public static void DrawCameraSettings(PipelineEditorContext context)
    {
        ImGui.TextWrapped("Camera-wide overrides: shared by every supporting pipeline rendering this camera.");
        if (!context.IsActivePipeline)
            ImGui.TextWrapped("Select the active pipeline to edit camera-wide overrides. This target's pipeline and effect settings can be edited separately.");

        ImGui.BeginDisabled(!context.IsActivePipeline);
        if (ImGui.CollapsingHeader("Image Quality", ImGuiTreeNodeFlags.DefaultOpen))
            DrawAntiAliasingOverride(context.Camera);
        if (ImGui.CollapsingHeader("Color Output"))
            DrawOutputHDROverride(context.Camera);
        if (context.Component is { } component && ImGui.CollapsingHeader("Directional Shadows"))
        {
            CameraSettingLabel("Shadow Rendering");
            int mode = (int)component.DirectionalShadowRenderingMode;
            if (ImGui.Combo("##DirectionalShadowRendering", ref mode, DirectionalShadowModeNames, DirectionalShadowModeNames.Length))
                component.DirectionalShadowRenderingMode = (EDirectionalShadowRenderingMode)mode;
        }
        ImGui.EndDisabled();

        // These hints belong to the pipelines implementing this feature contract.
        if (context.SelectedPipeline is IForwardDepthNormalPrePassSettings settings)
        {
            if (!settings.ForwardDepthPrePassEnabled)
                ImGui.TextWrapped("Forward pre-pass is disabled; share and resolution settings are inactive.");
            else if (settings.ForwardPrePassSharesGBufferTargets && settings.ForwardDepthNormalPrePassResolution != EDepthNormalPrePassResolution.Full)
                ImGui.TextWrapped("Shared GBuffer writes stay full resolution; this resolution affects the dedicated/contact pre-pass targets.");
        }
    }

    public static void DrawForwardPlusDebug(PipelineEditorContext context)
    {
        ImGui.TextWrapped("Forward+ overlay settings belong to this camera.");
        if (!context.IsActivePipeline)
            ImGui.TextWrapped("Select the active pipeline to edit the camera's Forward+ overlay.");
        ImGui.BeginDisabled(!context.IsActivePipeline);
        DrawForwardPlusDebugSection(context.Camera, context.Component);
        ImGui.EndDisabled();
    }

    private static void CameraSettingLabel(string label)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-float.Epsilon);
    }

    private static void DrawAntiAliasingOverride(XRCamera camera)
    {
        EAntiAliasingMode globalMode = RuntimeEngine.EffectiveSettings.AntiAliasingMode;
        string preview = camera.AntiAliasingModeOverride?.ToString() ?? $"Use Global ({globalMode})";
        CameraSettingLabel("Anti-Aliasing");
        if (ImGui.BeginCombo("##AAOverrideMode", preview))
        {
            if (ImGui.Selectable($"Use Global ({globalMode})", !camera.AntiAliasingModeOverride.HasValue))
                camera.AntiAliasingModeOverride = null;
            for (int i = 0; i < AntiAliasingModes.Length; i++)
            {
                if (ImGui.Selectable(AntiAliasingModeNames[i], camera.AntiAliasingModeOverride == AntiAliasingModes[i]))
                    camera.AntiAliasingModeOverride = AntiAliasingModes[i];
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use the global anti-aliasing mode or choose an override for this camera.");

        EAntiAliasingMode effectiveMode = camera.AntiAliasingModeOverride ?? globalMode;
        if (effectiveMode == EAntiAliasingMode.Msaa)
        {
            bool hasOverride = camera.MsaaSampleCountOverride.HasValue;
            if (ImGui.Checkbox("Override MSAA Samples", ref hasOverride))
                camera.MsaaSampleCountOverride = hasOverride ? RuntimeEngine.EffectiveSettings.MsaaSampleCount : null;
            if (camera.MsaaSampleCountOverride.HasValue)
            {
                int samples = (int)camera.MsaaSampleCountOverride.Value;
                CameraSettingLabel("MSAA Samples");
                if (ImGui.SliderInt("##MsaaSamples", ref samples, 1, 8))
                    camera.MsaaSampleCountOverride = (uint)Math.Clamp(samples, 1, 8);
            }
        }

        if (effectiveMode == EAntiAliasingMode.Tsr)
            DrawTsrScaleOverride(camera);
    }

    private static void DrawTsrScaleOverride(XRCamera camera)
    {
        bool hasOverride = camera.TsrRenderScaleOverride.HasValue;
        if (ImGui.Checkbox("Override TSR Scale", ref hasOverride))
            camera.TsrRenderScaleOverride = hasOverride ? RuntimeEngine.Rendering.Settings.TsrRenderScale : null;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Override the global TSR internal render scale for this camera.");

        if (camera.TsrRenderScaleOverride.HasValue)
        {
            float scale = camera.TsrRenderScaleOverride.Value;
            CameraSettingLabel("TSR Scale");
            if (ImGui.SliderFloat("##TsrScaleOverride", ref scale, 0.5f, 1f, "%.2fx"))
                camera.TsrRenderScaleOverride = scale;
        }
        else
            ImGui.TextDisabled($"Global TSR Scale: {RuntimeEngine.Rendering.Settings.TsrRenderScale:0.00}x");
    }

    private static void DrawOutputHDROverride(XRCamera camera)
    {
        CameraSettingLabel("Color Output");
        int mode = camera.OutputHDROverride.HasValue ? (camera.OutputHDROverride.Value ? 2 : 1) : 0;
        if (ImGui.Combo("##HDROutput", ref mode, HdrOutputModeNames, HdrOutputModeNames.Length))
            camera.OutputHDROverride = mode == 0 ? null : mode == 2;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use global output settings or override this camera. HDR output skips tonemapping.");
        bool effectiveHdr = camera.OutputHDROverride ?? RuntimeEngine.Rendering.Settings.OutputHDR;
        ImGui.TextDisabled($"Effective: {(effectiveHdr ? "HDR" : "SDR")}");
    }

    private static readonly string[] ForwardPlusDebugModeNames =
    [
        "None",
        "Heatmap",
        "Heatmap + Grid",
        "Overflow Only",
        "Grid Only",
    ];

    private static void DrawForwardPlusDebugSection(XRCamera cam, CameraComponent? component)
    {
        if (!ImGui.CollapsingHeader("Forward+ Debug Visualization"))
            return;

        int totalLights = RuntimeEngine.Rendering.State.ForwardPlusLocalLightCount;
        int tilesX = RuntimeEngine.Rendering.State.ForwardPlusTileCountX;
        int tilesY = RuntimeEngine.Rendering.State.ForwardPlusTileCountY;
        int maxPerTile = RuntimeEngine.Rendering.State.ForwardPlusMaxLightsPerTile;
        int tileSize = RuntimeEngine.Rendering.State.ForwardPlusTileSize;

        ImGui.TextWrapped($"Last rendered view: {tilesX} x {tilesY} tiles ({tileSize}px), max {maxPerTile} lights/tile.");
        ImGui.TextWrapped("These global counters may describe another viewport, not this camera.");
        if (totalLights <= 0)
        {
            ImGui.TextWrapped("No point/spot lights submitted to Forward+. Heatmap will be empty.");
            ImGui.TextWrapped("Forward+ only culls dynamic point and spot lights; directional lights are unaffected.");
        }
        else
        {
            ImGui.TextDisabled($"Local lights submitted this frame: {totalLights}");
        }
        ImGui.Separator();

        int mode = (int)cam.ForwardPlusDebugMode;
        CameraSettingLabel("Mode");
        if (ImGui.Combo("##ForwardPlusDebugMode", ref mode, ForwardPlusDebugModeNames, ForwardPlusDebugModeNames.Length))
        {
            if (component is not null)
                component.ForwardPlusDebugMode = (XRCamera.EForwardPlusDebugMode)mode;
            else
                cam.ForwardPlusDebugMode = (XRCamera.EForwardPlusDebugMode)mode;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Selects the Forward+ tile-light-culling overlay style.");

        float opacity = cam.ForwardPlusDebugOpacity;
        CameraSettingLabel("Opacity");
        if (ImGui.SliderFloat("##ForwardPlusDebugOpacity", ref opacity, 0.0f, 1.0f))
        {
            if (component is not null)
                component.ForwardPlusDebugOpacity = opacity;
            else
                cam.ForwardPlusDebugOpacity = opacity;
        }

        int maxCount = cam.ForwardPlusDebugMaxCount;
        CameraSettingLabel("Heatmap Max Count");
        if (ImGui.SliderInt("##ForwardPlusDebugMaxCount", ref maxCount, 1, 64))
        {
            if (component is not null)
                component.ForwardPlusDebugMaxCount = maxCount;
            else
                cam.ForwardPlusDebugMaxCount = maxCount;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Per-tile visible-light count that saturates the heatmap. Lower = more sensitive.");

        if (ImGui.SmallButton("Auto##ForwardPlusDebug"))
        {
            if (component is not null)
                component.ForwardPlusDebugMaxCount = Math.Max(1, Math.Min(maxPerTile > 0 ? maxPerTile : 32, Math.Max(4, totalLights)));
            else
                cam.ForwardPlusDebugMaxCount = Math.Max(1, Math.Min(maxPerTile > 0 ? maxPerTile : 32, Math.Max(4, totalLights)));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Estimate Max Count from the global last-rendered view, which may be another camera.");

        bool showEmpty = cam.ForwardPlusDebugShowEmptyTiles;
        if (ImGui.Checkbox("Show Empty Tiles##ForwardPlusDebug", ref showEmpty))
        {
            if (component is not null)
                component.ForwardPlusDebugShowEmptyTiles = showEmpty;
            else
                cam.ForwardPlusDebugShowEmptyTiles = showEmpty;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("If on, paints zero-count tiles faintly so the entire culling grid is visible. If off, only populated tiles draw.");

        bool showBar = cam.ForwardPlusDebugShowCountBar;
        if (ImGui.Checkbox("Per-Tile Count Bar##ForwardPlusDebug", ref showBar))
        {
            if (component is not null)
                component.ForwardPlusDebugShowCountBar = showBar;
            else
                cam.ForwardPlusDebugShowCountBar = showBar;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draw a small horizontal bar in each populated tile whose length equals (count / Max Count) for a quantitative readout.");
    }
}

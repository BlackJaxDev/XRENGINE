using ImGuiNET;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene;

namespace XREngine.Editor.ComponentEditors;

public sealed partial class CameraComponentEditor
{
    private static readonly string[] InternalResolutionModeNames = ["Full Resolution", "Scale", "Manual"];

    /// <summary>
    /// Shows pipeline-independent camera controls, followed by the selected pipeline's opt-in UI.
    /// Runtime cameras use the same contract without requiring a scene component.
    /// </summary>
    private static void DrawRuntimeCameraSettings(PipelineEditorContext context)
    {
        using var profilerScope = Engine.Profiler.Start("UI.ComponentEditor.CameraComponent.Settings");
        XRCamera camera = context.Camera;
        CameraComponent? component = context.Component;
        RenderPipeline selectedPipeline = context.SelectedPipeline;
        ImGui.PushID("CameraSettingsPanel");

        if (component is not null)
        {
            bool active = component.IsActivelyRendering;
            ImGui.TextColored(active ? new Vector4(0.2f, 0.8f, 0.2f, 1f) : new Vector4(1f, 0.7f, 0.2f, 1f),
                active ? "Rendering" : "Not rendering");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(component.GetUsageDescription());
        }

        ImGui.SeparatorText("Camera");
        if (ImGui.CollapsingHeader("Visibility", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (component is not null)
            {
                bool cullWithFrustum = component.CullWithFrustum;
                if (ImGui.Checkbox("Frustum Culling", ref cullWithFrustum))
                    component.CullWithFrustum = cullWithFrustum;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Skip objects outside this camera's view.");
            }
            DrawCullingMask(camera);
            ImGui.Spacing();
        }

        if (component is not null && ImGui.CollapsingHeader("Resolution"))
            DrawInternalResolutionSettings(component);

        if (component is not null && ImGui.CollapsingHeader("Output"))
        {
            ImGui.TextUnformatted("Render Target");
            ImGuiAssetUtilities.DrawAssetField<XRFrameBuffer>("CameraDefaultRenderTarget", component.DefaultRenderTarget, asset =>
            {
                component.DefaultRenderTarget = asset;
            });

            var overlay = component.GetUserInterfaceOverlay();
            string uiOverlay = (overlay as XRComponent)?.SceneNode?.Name ?? overlay?.GetType().Name ?? "None";
            ImGui.TextWrapped($"UI Overlay: {uiOverlay}");
        }

        if (ImGui.CollapsingHeader("Pipeline Assignment"))
        {
            ImGui.TextWrapped("Changes which pipeline renders this camera, independently of the editing target above.");
            ImGuiAssetUtilities.DrawAssetField<RenderPipeline>("CameraRenderPipeline", camera.RenderPipeline, asset =>
            {
                camera.ReplaceRenderPipelineAsset(asset ?? RuntimeEngine.Rendering.NewRenderPipeline());
            }, allowClear: false, allowCreateOrReplace: true, allowInlineInspector: false);
        }

        ImGui.SeparatorText("Selected Pipeline");
        ImGui.BeginDisabled(IsPipelineTypePreview(selectedPipeline));
        selectedPipeline.EditorUIProvider?.DrawCameraSettings(context);
        bool hasProperties = DrawRenderPipelineCameraSettings(selectedPipeline);
        ImGui.EndDisabled();
        if (!hasProperties && selectedPipeline.EditorUIProvider is null)
            ImGui.TextDisabled("This pipeline exposes no additional camera settings.");
        ImGui.PopID();
    }

    /// <summary>
    /// Uses a bounded popup so all 32 layers remain editable without expanding the inspector.
    /// </summary>
    private static void DrawCullingMask(XRCamera camera)
    {
        int mask = camera.CullingMask.Value;
        string summary = mask switch
        {
            -1 => "All Layers",
            0 => "No Layers",
            _ => $"{BitOperations.PopCount(unchecked((uint)mask))} of 32 Layers"
        };

        CameraSettingLabel("Culling Mask");
        bool open = ImGui.BeginCombo("##CullingMask", summary, ImGuiComboFlags.HeightLarge);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Layers rendered by this camera. Mask: 0x{mask:X8}");
        if (!open)
            return;

        int originalMask = mask;
        if (ImGui.SmallButton("All"))
            mask = -1;
        ImGui.SameLine();
        if (ImGui.SmallButton("None"))
            mask = 0;
        ImGui.SameLine();
        if (ImGui.SmallButton("Invert"))
            mask = ~mask;
        ImGui.TextDisabled($"Mask: 0x{mask:X8}");
        ImGui.Separator();

        if (ImGui.BeginChild("CullingLayers", new Vector2(0f, ImGui.GetFrameHeightWithSpacing() * 9f)))
        {
            var layerNames = Engine.GameSettings.LayerNames;
            for (int layer = 0; layer < 32; layer++)
            {
                // Layer indices keep IDs unique even when names are duplicated or renamed.
                ImGui.PushID(layer);
                bool enabled = (mask & (1 << layer)) != 0;
                if (ImGui.Checkbox("##Enabled", ref enabled))
                    mask = enabled ? mask | (1 << layer) : mask & ~(1 << layer);
                ImGui.SameLine();
                string name = layerNames.TryGetValue(layer, out var layerName) && !string.IsNullOrWhiteSpace(layerName)
                    ? layerName
                    : "Unnamed";
                ImGui.TextUnformatted($"{layer}: {name}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Layer {layer}: {name}");
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.EndCombo();

        if (mask != originalMask)
            camera.CullingMask = new LayerMask(mask);
    }

    /// <summary>
    /// Labels above full-width controls avoid clipping long labels in narrow docked panels.
    /// </summary>
    private static void CameraSettingLabel(string label)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-float.Epsilon);
    }


    private static void DrawInternalResolutionSettings(CameraComponent component)
    {
        CameraSettingLabel("Internal Resolution");
        int mode = (int)component.InternalResolutionMode;
        if (ImGui.Combo("##InternalResMode", ref mode, InternalResolutionModeNames, InternalResolutionModeNames.Length))
            component.InternalResolutionMode = (EInternalResolutionMode)mode;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lower internal resolutions improve performance at the cost of image detail.");

        switch (component.InternalResolutionMode)
        {
            case EInternalResolutionMode.Scale:
                float scale = component.InternalResolutionScale;
                CameraSettingLabel("Resolution Scale");
                if (ImGui.SliderFloat("##InternalResScale", ref scale, 0.1f, 2f, "%.2fx"))
                    component.InternalResolutionScale = scale;
                if (ImGui.SmallButton("0.5x"))
                    component.InternalResolutionScale = 0.5f;
                ImGui.SameLine();
                if (ImGui.SmallButton("1x"))
                    component.InternalResolutionScale = 1f;
                ImGui.SameLine();
                if (ImGui.SmallButton("1.5x"))
                    component.InternalResolutionScale = 1.5f;
                break;

            case EInternalResolutionMode.Manual:
                int width = component.ManualInternalWidth;
                int height = component.ManualInternalHeight;
                CameraSettingLabel("Width");
                if (ImGui.InputInt("##InternalResWidth", ref width, 1, 100))
                    component.ManualInternalWidth = Math.Max(1, width);
                CameraSettingLabel("Height");
                if (ImGui.InputInt("##InternalResHeight", ref height, 1, 100))
                    component.ManualInternalHeight = Math.Max(1, height);

                if (ImGui.SmallButton("720p"))
                    SetCameraResolution(component, 1280, 720);
                ImGui.SameLine();
                if (ImGui.SmallButton("1080p"))
                    SetCameraResolution(component, 1920, 1080);
                ImGui.SameLine();
                if (ImGui.SmallButton("1440p"))
                    SetCameraResolution(component, 2560, 1440);
                ImGui.SameLine();
                if (ImGui.SmallButton("4K"))
                    SetCameraResolution(component, 3840, 2160);
                break;
        }

        if (component.Camera.Viewports.Count > 0)
        {
            var viewport = component.Camera.Viewports[0];
            ImGui.TextWrapped($"Effective: {viewport.InternalWidth} x {viewport.InternalHeight} -> {viewport.Width} x {viewport.Height}");
        }
    }

    private static void SetCameraResolution(CameraComponent component, int width, int height)
    {
        component.ManualInternalWidth = width;
        component.ManualInternalHeight = height;
    }
}

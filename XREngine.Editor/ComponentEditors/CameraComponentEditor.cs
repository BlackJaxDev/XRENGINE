using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Resources;

namespace XREngine.Editor.ComponentEditors;

public sealed class CameraComponentEditor : IXRComponentEditor
{
    private const float PreviewMaxEdge = 256.0f;
    private const float PreviewMinEdge = 96.0f;

    private readonly struct ParameterOption
    {
        public ParameterOption(string label, Type type)
        {
            Label = label;
            ParameterType = type;
        }

        public string Label { get; }
        public Type ParameterType { get; }
    }

    private static readonly ParameterOption[] ParameterOptions =
    [
        new("Perspective", typeof(XRPerspectiveCameraParameters)),
        new("Orthographic", typeof(XROrthographicCameraParameters)),
        new("OpenVR Eye", typeof(XROVRCameraParameters))
    ];

    private static readonly string[] PreferredPreviewTextureNames =
    [
        DefaultRenderPipeline.PostProcessOutputTextureName,
        DefaultRenderPipeline.HDRSceneTextureName,
        DefaultRenderPipeline.DiffuseTextureName
    ];

    private static readonly string[] PreferredPreviewFrameBufferNames =
    [
        DefaultRenderPipeline.PostProcessOutputFBOName,
        DefaultRenderPipeline.ForwardPassFBOName,
        DefaultRenderPipeline.LightCombineFBOName
    ];

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not CameraComponent cameraComponent)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(cameraComponent, visited, "Camera Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(cameraComponent.GetHashCode());
        DrawCameraSettings(cameraComponent);
        DrawParameterSection(cameraComponent, visited);
        DrawPostProcessingEditor(cameraComponent, visited);
        DrawPreviewSection(cameraComponent);
        ImGui.PopID();

        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawCameraSettings(CameraComponent component)
    {
        if (!ImGui.CollapsingHeader("Camera Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool cullWithFrustum = component.CullWithFrustum;
        if (ImGui.Checkbox("Cull With Frustum", ref cullWithFrustum))
            component.CullWithFrustum = cullWithFrustum;

        string uiOverlay = component.GetUserInterfaceOverlay()?.SceneNode?.Name
            ?? component.GetUserInterfaceOverlay()?.GetType().Name
            ?? "<none>";
        ImGui.TextDisabled($"UI Overlay: {uiOverlay}");

        string renderTargetLabel = component.DefaultRenderTarget?.Name ?? "<none>";
        ImGui.TextDisabled($"Default Render Target: {renderTargetLabel}");
        if (component.DefaultRenderTarget is null && ImGui.IsItemHovered())
            ImGui.SetTooltip("Camera renders directly to its viewport.");

        int viewportCount = component.Camera.Viewports.Count;
        ImGui.TextDisabled($"Bound Viewports: {viewportCount}");

        ImGui.Separator();
        ImGui.TextDisabled("Render Pipeline Asset");
        var pipeline = component.Camera.RenderPipeline;
        ImGuiAssetUtilities.DrawAssetField<RenderPipeline>("CameraRenderPipeline", pipeline, asset =>
        {
            component.Camera.RenderPipeline = asset ?? Engine.Rendering.NewRenderPipeline();
        });

        ImGui.TextDisabled("Default Render Target Asset");
        ImGuiAssetUtilities.DrawAssetField<XRFrameBuffer>("CameraDefaultRenderTarget", component.DefaultRenderTarget, asset =>
        {
            component.DefaultRenderTarget = asset;
        });
    }

    private static void DrawParameterSection(CameraComponent component, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Projection Parameters", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var camera = component.Camera;
        var parameters = camera.Parameters;

        DrawParameterTypeSwitcher(camera, parameters);
        DrawCommonParameterControls(parameters);

        switch (parameters)
        {
            case XRPerspectiveCameraParameters perspective:
                DrawPerspectiveParameters(perspective);
                break;
            case XROrthographicCameraParameters orthographic:
                DrawOrthographicParameters(orthographic);
                break;
            case XROVRCameraParameters ovr:
                DrawOpenVREyeParameters(ovr);
                break;
            default:
                ImGui.TextDisabled($"No custom editor for {parameters.GetType().Name}.");
                break;
        }

        ImGui.Spacing();
        UnitTestingWorld.UserInterface.DrawRuntimeObjectInspector("Projection Object", parameters, visited, defaultOpen: false);
    }

    private static void DrawParameterTypeSwitcher(XRCamera camera, XRCameraParameters current)
    {
        int currentIndex = Array.FindIndex(ParameterOptions, option => option.ParameterType.IsInstanceOfType(current));
        if (currentIndex < 0)
            currentIndex = 0;

        string currentLabel = ParameterOptions[currentIndex].Label;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("Projection Type", currentLabel))
        {
            for (int i = 0; i < ParameterOptions.Length; i++)
            {
                bool selected = i == currentIndex;
                if (ImGui.Selectable(ParameterOptions[i].Label, selected) && !selected)
                {
                    XRCameraParameters replacement = CreateParameterInstance(ParameterOptions[i].ParameterType, current);
                    camera.Parameters = replacement;
                    current = replacement;
                    currentIndex = i;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
    }

    private static XRCameraParameters CreateParameterInstance(Type targetType, XRCameraParameters previous)
    {
        XRCameraParameters instance = targetType switch
        {
            Type t when t == typeof(XRPerspectiveCameraParameters) =>
                ClonePerspective(previous),
            Type t when t == typeof(XROrthographicCameraParameters) =>
                CloneOrthographic(previous),
            Type t when t == typeof(XROVRCameraParameters) =>
                new XROVRCameraParameters(true, previous.NearZ, previous.FarZ),
            _ => (Activator.CreateInstance(targetType) as XRCameraParameters)
                 ?? new XRPerspectiveCameraParameters(previous.NearZ, previous.FarZ)
        };

        instance.NearZ = previous.NearZ;
        instance.FarZ = previous.FarZ;
        return instance;

        static XRPerspectiveCameraParameters ClonePerspective(XRCameraParameters previous)
        {
            if (previous is XRPerspectiveCameraParameters prior)
            {
                return new XRPerspectiveCameraParameters(prior.VerticalFieldOfView, prior.InheritAspectRatio ? null : prior.AspectRatio, prior.NearZ, prior.FarZ)
                {
                    InheritAspectRatio = prior.InheritAspectRatio
                };
            }

            return new XRPerspectiveCameraParameters(previous.NearZ, previous.FarZ);
        }

        static XROrthographicCameraParameters CloneOrthographic(XRCameraParameters previous)
        {
            if (previous is XROrthographicCameraParameters ortho)
                return new XROrthographicCameraParameters(ortho.Width, ortho.Height, ortho.NearZ, ortho.FarZ);

            Vector2 frustum = previous.GetFrustumSizeAtDistance(MathF.Max(previous.NearZ + 1.0f, 1.0f));
            return new XROrthographicCameraParameters(MathF.Max(1.0f, frustum.X), MathF.Max(1.0f, frustum.Y), previous.NearZ, previous.FarZ);
        }
    }

    private static void DrawCommonParameterControls(XRCameraParameters parameters)
    {
        float near = parameters.NearZ;
        if (ImGui.DragFloat("Near Plane", ref near, 0.01f, 0.0001f, parameters.FarZ - 0.001f, "%.4f"))
            parameters.NearZ = Clamp(near, 0.0001f, parameters.FarZ - 0.001f);

        float far = parameters.FarZ;
        if (ImGui.DragFloat("Far Plane", ref far, 1.0f, parameters.NearZ + 0.001f, float.MaxValue, "%.2f"))
            parameters.FarZ = MathF.Max(parameters.NearZ + 0.001f, far);
    }

    private static void DrawPerspectiveParameters(XRPerspectiveCameraParameters parameters)
    {
        float fov = parameters.VerticalFieldOfView;
            if (ImGui.SliderFloat("Vertical FOV", ref fov, 1.0f, 170.0f, "%.1f deg"))
                parameters.VerticalFieldOfView = Clamp(fov, 1.0f, 170.0f);

        bool inheritAspect = parameters.InheritAspectRatio;
        if (ImGui.Checkbox("Inherit Aspect Ratio", ref inheritAspect))
            parameters.InheritAspectRatio = inheritAspect;

        using (new ImGuiDisabledScope(parameters.InheritAspectRatio))
        {
            float aspect = parameters.AspectRatio;
            if (ImGui.DragFloat("Manual Aspect", ref aspect, 0.01f, 0.01f, 32.0f, "%.3f"))
                parameters.AspectRatio = MathF.Max(0.01f, aspect);
        }

            ImGui.TextDisabled($"Horizontal FOV: {parameters.HorizontalFieldOfView:F1} deg");
    }

    private static void DrawOrthographicParameters(XROrthographicCameraParameters parameters)
    {
        float width = parameters.Width;
        if (ImGui.DragFloat("Width", ref width, 0.1f, 0.01f, 100000f, "%.2f"))
            parameters.Width = MathF.Max(0.01f, width);

        float height = parameters.Height;
        if (ImGui.DragFloat("Height", ref height, 0.1f, 0.01f, 100000f, "%.2f"))
            parameters.Height = MathF.Max(0.01f, height);

        if (ImGui.BeginCombo("Origin Preset", "Select"))
        {
            if (ImGui.Selectable("Centered"))
                parameters.SetOriginCentered();
            if (ImGui.Selectable("Bottom Left"))
                parameters.SetOriginBottomLeft();
            if (ImGui.Selectable("Top Left"))
                parameters.SetOriginTopLeft();
            if (ImGui.Selectable("Bottom Right"))
                parameters.SetOriginBottomRight();
            if (ImGui.Selectable("Top Right"))
                parameters.SetOriginTopRight();
            ImGui.EndCombo();
        }

        Vector2 origin = parameters.Origin;
        ImGui.TextDisabled($"Origin Offset: ({origin.X:F2}, {origin.Y:F2})");
    }

    private static void DrawOpenVREyeParameters(XROVRCameraParameters parameters)
    {
        bool leftEye = parameters.LeftEye;
        if (ImGui.Checkbox("Render Left Eye", ref leftEye))
            parameters.LeftEye = leftEye;
    }

    private static void DrawPostProcessingEditor(CameraComponent component, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Post Processing", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var postProcessing = component.Camera.PostProcessing;
        if (postProcessing is null)
        {
            ImGui.TextDisabled("Post processing is disabled for this camera.");
            return;
        }
        ImGui.PushID("PostProcessingPanel");

        DrawTonemappingControls(postProcessing);
        DrawBloomSection(postProcessing.Bloom);
        DrawAmbientOcclusionSection(postProcessing.AmbientOcclusion);
        DrawMotionBlurSection(postProcessing.MotionBlur);
        DrawColorGradingSection(postProcessing.ColorGrading);
        DrawVignetteSection(postProcessing.Vignette);
        DrawLensDistortionSection(postProcessing.LensDistortion);
        DrawChromaticAberrationSection(postProcessing.ChromaticAberration);
        DrawFogSection(postProcessing.Fog);

        DrawAdvancedPostProcessingInspector(postProcessing, visited);

        ImGui.PopID();
    }

    private static void DrawPreviewSection(CameraComponent component)
    {
        if (!ImGui.CollapsingHeader("Camera Preview", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!Engine.IsRenderThread)
        {
            ImGui.TextDisabled("Preview is available only on the render thread.");
            return;
        }

        if (!TryResolvePreviewTexture(component, out XRTexture? texture, out Vector2 pixelSize, out string? failure))
        {
            ImGui.TextDisabled(failure ?? "Preview unavailable.");
            return;
        }

        XRTexture resolvedTexture = texture!;

        if (!TryGetTextureHandle(resolvedTexture, out nint handle, out string? handleFailure))
        {
            ImGui.TextDisabled(handleFailure ?? "Failed to acquire GPU handle.");
            return;
        }

        Vector2 displaySize = CalculatePreviewSize(pixelSize);
        Vector2 uv0 = new(0.0f, 1.0f);
        Vector2 uv1 = new(1.0f, 0.0f);
        bool openDialog = false;

        ImGui.Image(handle, displaySize, uv0, uv1);
        if (ImGui.IsItemHovered())
        {
            string formatLabel = resolvedTexture is XRTexture2D tex2D
                ? tex2D.SizedInternalFormat.ToString()
                : resolvedTexture.GetType().Name;
            ImGui.SetTooltip($"{pixelSize.X:0} x {pixelSize.Y:0} | {formatLabel}");
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                openDialog = true;
        }

        ImGui.TextDisabled($"{pixelSize.X:0} x {pixelSize.Y:0} ({resolvedTexture.GetType().Name})");
        if (ImGui.Button("Open Preview Window"))
            openDialog = true;

        if (openDialog)
            ComponentEditorLayout.RequestPreviewDialog(component.SceneNode?.Name ?? "Camera Preview", handle, pixelSize, flipVertically: true);
    }

    private static bool TryResolvePreviewTexture(CameraComponent component, out XRTexture? texture, out Vector2 pixelSize, out string? failure)
    {
        if (TryExtractTextureFromFbo(component.DefaultRenderTarget, out texture, out pixelSize))
        {
            failure = null;
            return true;
        }

        foreach (var viewport in component.Camera.Viewports)
        {
            if (viewport is null || viewport.CameraComponent != component && viewport.Camera != component.Camera)
                continue;
            
            if (!TryResolvePipelineTexture(viewport.RenderPipelineInstance, out texture))
                continue;
            
            pixelSize = GetPixelSize(texture);
            failure = null;
            return true;
        }

        texture = null;
        pixelSize = Vector2.Zero;
        failure = "No render target or viewport texture is available.";
        return false;
    }

    private static bool TryExtractTextureFromFbo(XRFrameBuffer? fbo, out XRTexture? texture, out Vector2 pixelSize)
    {
        texture = null;
        pixelSize = Vector2.Zero;
        if (fbo?.Targets is null)
            return false;

        foreach (var (target, attachment, _, _) in fbo.Targets)
        {
            if (!IsColorAttachment(attachment) || target is not XRTexture tex)
                continue;

            texture = tex;
            pixelSize = GetPixelSize(tex);
            return true;
        }

        return false;
    }

    private static bool TryResolvePipelineTexture(XRRenderPipelineInstance pipeline, [NotNullWhen(true)] out XRTexture? texture)
    {
        texture = null;
        RenderResourceRegistry resources = pipeline.Resources;

        foreach (string name in PreferredPreviewTextureNames)
        {
            if (resources.TryGetTexture(name, out XRTexture? candidate) && candidate is XRTexture2D)
            {
                texture = candidate;
                return true;
            }
        }

        foreach (string fboName in PreferredPreviewFrameBufferNames)
        {
            if (resources.TryGetFrameBuffer(fboName, out XRFrameBuffer? fbo) && TryExtractTextureFromFbo(fbo, out XRTexture? candidate, out _))
            {
                texture = candidate;
                return true;
            }
        }

        foreach (XRFrameBuffer fbo in resources.EnumerateFrameBufferInstances())
        {
            if (TryExtractTextureFromFbo(fbo, out XRTexture? candidate, out _))
            {
                texture = candidate;
                return true;
            }
        }

        texture = resources.EnumerateTextureInstances()
            .OfType<XRTexture2D>()
            .OrderByDescending(GetPixelArea)
            .FirstOrDefault();

        return texture is not null;
    }

    private static float GetPixelArea(XRTexture texture)
    {
        Vector3 dims = texture.WidthHeightDepth;
        return MathF.Max(1.0f, dims.X) * MathF.Max(1.0f, dims.Y);
    }

    private static bool TryGetTextureHandle(XRTexture texture, out nint handle, out string? failure)
    {
        handle = nint.Zero;
        failure = null;

        if (texture is not XRTexture2D texture2D)
        {
            failure = "Preview currently supports 2D textures only.";
            return false;
        }

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            failure = "Preview requires the OpenGL renderer.";
            return false;
        }

        var glTexture = renderer.GenericToAPI<GLTexture2D>(texture2D);
        if (glTexture is null)
        {
            failure = "Texture has not been uploaded to the GPU yet.";
            return false;
        }

        if (glTexture.BindingId == 0 || glTexture.BindingId == OpenGLRenderer.GLObjectBase.InvalidBindingId)
        {
            failure = "Texture binding identifier is invalid.";
            return false;
        }

        handle = (nint)glTexture.BindingId;
        return true;
    }

    private static Vector2 CalculatePreviewSize(Vector2 pixelSize)
    {
        float width = MathF.Max(1.0f, pixelSize.X);
        float height = MathF.Max(1.0f, pixelSize.Y);

        float scale = MathF.Min(PreviewMaxEdge / width, PreviewMaxEdge / height);
        scale = MathF.Min(scale, 1.0f);

        width = MathF.Max(PreviewMinEdge, width * scale);
        height = MathF.Max(PreviewMinEdge, height * scale);
        return new Vector2(width, height);
    }

    private static Vector2 GetPixelSize(XRTexture texture)
    {
        Vector3 dims = texture.WidthHeightDepth;
        return new Vector2(MathF.Max(1.0f, dims.X), MathF.Max(1.0f, dims.Y));
    }

    private static void DrawTonemappingControls(PostProcessingSettings settings)
    {
        if (settings is null || !ImGui.CollapsingHeader("Tonemapping", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        int currentIndex = (int)settings.Tonemapping;
        string currentLabel = settings.Tonemapping.ToString();

        ImGui.PushID("Tonemapping");
        if (ImGui.BeginCombo("Operator", currentLabel))
        {
            foreach (ETonemappingType type in Enum.GetValues<ETonemappingType>())
            {
                bool selected = (int)type == currentIndex;
                if (ImGui.Selectable(type.ToString(), selected) && !selected)
                {
                    settings.Tonemapping = type;
                    currentIndex = (int)type;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.PopID();
    }

    private static void DrawBloomSection(BloomSettings? bloom)
    {
        if (bloom is null || !ImGui.CollapsingHeader("Bloom", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushID("Bloom");
        float intensity = bloom.Intensity;
        if (ImGui.SliderFloat("Intensity", ref intensity, 0.0f, 5.0f, "%.2f"))
            bloom.Intensity = intensity;

        float threshold = bloom.Threshold;
        if (ImGui.SliderFloat("Threshold", ref threshold, 0.1f, 5.0f, "%.2f"))
            bloom.Threshold = threshold;

        float softKnee = bloom.SoftKnee;
        if (ImGui.SliderFloat("Soft Knee", ref softKnee, 0.0f, 1.0f, "%.2f"))
            bloom.SoftKnee = softKnee;

        float radius = bloom.Radius;
        if (ImGui.SliderFloat("Blur Radius", ref radius, 0.1f, 8.0f, "%.2f"))
            bloom.Radius = radius;

        ImGui.PopID();
    }

    private static void DrawAmbientOcclusionSection(AmbientOcclusionSettings? ao)
    {
        if (ao is null || !ImGui.CollapsingHeader("Ambient Occlusion", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushID("AmbientOcclusion");
        bool enabled = ao.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
            ao.Enabled = enabled;

        using (new ImGuiDisabledScope(!enabled))
        {
            if (ImGui.BeginCombo("Method", ao.Type.ToString()))
            {
                foreach (AmbientOcclusionSettings.EType type in Enum.GetValues<AmbientOcclusionSettings.EType>())
                {
                    bool selected = ao.Type == type;
                    if (ImGui.Selectable(type.ToString(), selected) && !selected)
                        ao.Type = type;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            float radius = ao.Radius;
            if (ImGui.SliderFloat("Radius", ref radius, 0.1f, 5.0f, "%.2f"))
                ao.Radius = radius;

            float power = ao.Power;
            if (ImGui.SliderFloat("Contrast", ref power, 0.5f, 3.0f, "%.2f"))
                ao.Power = power;

            float bias = ao.Bias;
            if (ImGui.SliderFloat("Bias", ref bias, 0.0f, 0.2f, "%.3f"))
                ao.Bias = bias;

            float intensity = ao.Intensity;
            if (ImGui.SliderFloat("Intensity", ref intensity, 0.0f, 4.0f, "%.2f"))
                ao.Intensity = intensity;

            float resolution = ao.ResolutionScale;
            if (ImGui.SliderFloat("Resolution Scale", ref resolution, 0.25f, 2.0f, "%.2f"))
                ao.ResolutionScale = resolution;

            float spp = ao.SamplesPerPixel;
            if (ImGui.SliderFloat("Samples / Pixel", ref spp, 0.5f, 8.0f, "%.1f"))
                ao.SamplesPerPixel = spp;
        }

        ImGui.PopID();
    }

    private static void DrawMotionBlurSection(MotionBlurSettings? motionBlur)
    {
        if (motionBlur is null || !ImGui.CollapsingHeader("Motion Blur", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushID("MotionBlur");
        bool enabled = motionBlur.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
            motionBlur.Enabled = enabled;

        using (new ImGuiDisabledScope(!enabled))
        {
            float shutter = motionBlur.ShutterScale;
            if (ImGui.SliderFloat("Shutter Scale", ref shutter, 0.0f, 2.0f, "%.2f"))
                motionBlur.ShutterScale = shutter;

            int samples = motionBlur.MaxSamples;
            if (ImGui.SliderInt("Max Samples", ref samples, 4, 64))
                motionBlur.MaxSamples = samples;

            float maxBlur = motionBlur.MaxBlurPixels;
            if (ImGui.SliderFloat("Max Blur (px)", ref maxBlur, 1.0f, 64.0f, "%.1f"))
                motionBlur.MaxBlurPixels = maxBlur;

            float velocityThreshold = motionBlur.VelocityThreshold;
            if (ImGui.SliderFloat("Velocity Threshold", ref velocityThreshold, 0.0f, 0.5f, "%.3f"))
                motionBlur.VelocityThreshold = velocityThreshold;

            float depthReject = motionBlur.DepthRejectThreshold;
            if (ImGui.SliderFloat("Depth Reject", ref depthReject, 0.0f, 0.05f, "%.3f"))
                motionBlur.DepthRejectThreshold = depthReject;

            float falloff = motionBlur.SampleFalloff;
            if (ImGui.SliderFloat("Sample Falloff", ref falloff, 0.1f, 8.0f, "%.2f"))
                motionBlur.SampleFalloff = falloff;
        }

        ImGui.PopID();
    }

    private static void DrawColorGradingSection(ColorGradingSettings? colorGrading)
    {
        if (colorGrading is null || !ImGui.CollapsingHeader("Color Grading", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushID("ColorGrading");

        Vector3 tint = colorGrading.Tint;
        if (ImGui.ColorEdit3("Tint", ref tint))
            colorGrading.Tint = tint;

        bool autoExposure = colorGrading.AutoExposure;
        if (ImGui.Checkbox("Auto Exposure", ref autoExposure))
            colorGrading.AutoExposure = autoExposure;

        using (new ImGuiDisabledScope(autoExposure))
        {
            float exposure = colorGrading.Exposure;
            if (ImGui.SliderFloat("Manual Exposure", ref exposure, 0.0001f, 10.0f, "%.4f"))
                colorGrading.Exposure = exposure;
        }

        float bias = colorGrading.AutoExposureBias;
        if (ImGui.SliderFloat("Exposure Bias", ref bias, -10.0f, 10.0f, "%.2f"))
            colorGrading.AutoExposureBias = bias;

        float scale = colorGrading.AutoExposureScale;
        if (ImGui.SliderFloat("Exposure Scale", ref scale, 0.1f, 5.0f, "%.2f"))
            colorGrading.AutoExposureScale = scale;

        float contrast = colorGrading.Contrast;
        if (ImGui.SliderFloat("Contrast", ref contrast, -50.0f, 50.0f, "%.1f"))
            colorGrading.Contrast = contrast;

        float gamma = colorGrading.Gamma;
        if (ImGui.SliderFloat("Gamma", ref gamma, 0.1f, 4.0f, "%.2f"))
            colorGrading.Gamma = gamma;

        float hue = colorGrading.Hue;
        if (ImGui.SliderFloat("Hue", ref hue, 0.0f, 2.0f, "%.2f"))
            colorGrading.Hue = hue;

        float saturation = colorGrading.Saturation;
        if (ImGui.SliderFloat("Saturation", ref saturation, 0.0f, 2.0f, "%.2f"))
            colorGrading.Saturation = saturation;

        float brightness = colorGrading.Brightness;
        if (ImGui.SliderFloat("Brightness", ref brightness, 0.0f, 2.0f, "%.2f"))
            colorGrading.Brightness = brightness;

        ImGui.PopID();
    }

    private static void DrawVignetteSection(VignetteSettings? vignette)
    {
        if (vignette is null || !ImGui.CollapsingHeader("Vignette", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushID("Vignette");
        Vector3 color = vignette.Color;
        if (ImGui.ColorEdit3("Color", ref color))
            vignette.Color = color;

        float intensity = vignette.Intensity;
        if (ImGui.SliderFloat("Intensity", ref intensity, 0.0f, 2.0f, "%.2f"))
            vignette.Intensity = intensity;

        float power = vignette.Power;
        if (ImGui.SliderFloat("Power", ref power, 0.1f, 6.0f, "%.2f"))
            vignette.Power = power;

        ImGui.PopID();
    }

    private static void DrawLensDistortionSection(LensDistortionSettings? lens)
    {
        if (lens is null || !ImGui.CollapsingHeader("Lens Distortion", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushID("LensDistortion");
        float intensity = lens.Intensity;
        if (ImGui.SliderFloat("Intensity", ref intensity, -1.0f, 1.0f, "%.3f"))
            lens.Intensity = intensity;
        ImGui.PopID();
    }

    private static void DrawChromaticAberrationSection(ChromaticAberrationSettings? ca)
    {
        if (ca is null || !ImGui.CollapsingHeader("Chromatic Aberration", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushID("ChromaticAberration");
        float intensity = ca.Intensity;
        if (ImGui.SliderFloat("Intensity", ref intensity, 0.0f, 1.0f, "%.3f"))
            ca.Intensity = intensity;
        ImGui.PopID();
    }

    private static void DrawFogSection(FogSettings? fog)
    {
        if (fog is null || !ImGui.CollapsingHeader("Fog", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushID("Fog");
        float intensity = fog.DepthFogIntensity;
        if (ImGui.SliderFloat("Intensity", ref intensity, 0.0f, 1.0f, "%.2f"))
            fog.DepthFogIntensity = intensity;

        float start = fog.DepthFogStartDistance;
        if (ImGui.DragFloat("Start Distance", ref start, 1.0f, 0.0f, float.MaxValue, "%.1f"))
            fog.DepthFogStartDistance = MathF.Max(0.0f, start);

        float end = fog.DepthFogEndDistance;
        if (ImGui.DragFloat("End Distance", ref end, 1.0f, 0.0f, float.MaxValue, "%.1f"))
            fog.DepthFogEndDistance = MathF.Max(end, fog.DepthFogStartDistance + 1.0f);

        Vector3 color = fog.DepthFogColor;
        if (ImGui.ColorEdit3("Color", ref color))
            fog.DepthFogColor = color;

        ImGui.PopID();
    }

    private static void DrawAdvancedPostProcessingInspector(PostProcessingSettings settings, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Advanced Post Processing"))
            return;

        ImGui.TextDisabled("Full object view (experimental)");
        UnitTestingWorld.UserInterface.DrawRuntimeObjectInspector("Post Processing Settings", settings, visited, defaultOpen: false);
    }

    private static bool IsColorAttachment(EFrameBufferAttachment attachment)
    {
        return attachment >= EFrameBufferAttachment.ColorAttachment0 && attachment <= EFrameBufferAttachment.ColorAttachment31;
    }

    private static float Clamp(float value, float min, float max)
        => MathF.Max(min, MathF.Min(max, value));

    private readonly struct ImGuiDisabledScope : IDisposable
    {
        private readonly bool _disabled;

        public ImGuiDisabledScope(bool disabled)
        {
            _disabled = disabled;
            if (disabled)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (_disabled)
                ImGui.EndDisabled();
        }
    }
}

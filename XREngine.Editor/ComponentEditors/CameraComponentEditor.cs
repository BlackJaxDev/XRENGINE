using ImGuiNET;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Scene;

namespace XREngine.Editor.ComponentEditors;

public sealed class CameraComponentEditor : IXRComponentEditor
{
    private const float PreviewMaxEdge = 256.0f;
    private const float PreviewMinEdge = 96.0f;

    private readonly struct ParameterOption(string label, Type type)
    {
        public string Label { get; } = label;
        public Type ParameterType { get; } = type;
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
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
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
        DrawSchemaBasedPostProcessingEditor(cameraComponent, visited);
        DrawPreviewSection(cameraComponent);
        ImGui.PopID();

        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawUsageStatus(CameraComponent component)
    {
        bool isActive = component.IsActivelyRendering;
        var player = component.GetUsingLocalPlayer();
        var pawn = component.GetUsingPawn();

        // Status indicator with color
        Vector4 statusColor = isActive 
            ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f)  // Green for active
            : new Vector4(0.6f, 0.6f, 0.6f, 1.0f); // Gray for inactive
        
        string statusIcon = isActive ? "[ACTIVE]" : "[INACTIVE]";
        ImGui.TextColored(statusColor, statusIcon);
        ImGui.SameLine();
        ImGui.Text("Rendering Status");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(component.GetUsageDescription());

        ImGui.Indent();

        // Local Player info
        if (player is not null)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), $"Local Player: {(int)player.LocalPlayerIndex + 1}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"This camera is being used by Local Player {(int)player.LocalPlayerIndex + 1}'s viewport.");
        }
        else
        {
            ImGui.TextDisabled("Local Player: None");
        }

        // Pawn info
        if (pawn is not null)
        {
            string pawnName = pawn.SceneNode?.Name ?? pawn.GetType().Name;
            string pawnType = pawn.GetType().Name;
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.6f, 1.0f), $"Pawn: {pawnName}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Type: {pawnType}\nThis camera is provided by this pawn component.");
        }
        else
        {
            ImGui.TextDisabled("Pawn: None");
        }

        // Viewport count
        int viewportCount = component.Camera.Viewports.Count;
        if (viewportCount > 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.4f, 1.0f), $"Viewports: {viewportCount}");
            if (ImGui.IsItemHovered())
            {
                string tooltip = "Bound viewports:\n";
                for (int i = 0; i < component.Camera.Viewports.Count; i++)
                {
                    var vp = component.Camera.Viewports[i];
                    tooltip += $"  [{i}] {vp.Region.Width}x{vp.Region.Height}";
                    if (vp.Window is not null)
                        tooltip += $" (Window: {vp.Window.Window.Title ?? "untitled"})";
                    tooltip += "\n";
                }
                ImGui.SetTooltip(tooltip.TrimEnd());
            }
        }
        else
        {
            ImGui.TextDisabled("Viewports: 0");
        }

        // Warning if not in use
        if (!isActive)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.2f, 1.0f), "⚠ Camera is not rendering");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("This camera is not bound to any viewport or render target.\nIt will not consume rendering resources.");
        }

        ImGui.Unindent();
    }

    private static void DrawCameraSettings(CameraComponent component)
    {
        if (!ImGui.CollapsingHeader("Camera Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Usage Status Section
        DrawUsageStatus(component);
        ImGui.Separator();

        bool cullWithFrustum = component.CullWithFrustum;
        if (ImGui.Checkbox("Cull With Frustum", ref cullWithFrustum))
            component.CullWithFrustum = cullWithFrustum;

        // Culling Mask
        int cullingMask = component.Camera.CullingMask.Value;
        bool cullingChanged = false;

        ImGui.Text("Culling Mask");
        ImGui.SameLine();
        ImGui.TextDisabled($"(0x{cullingMask:X8})");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Bitmask determining which layers this camera renders. -1 = all layers.");

        const int columns = 4;
        if (ImGui.BeginTable("CullingMaskTable", columns, ImGuiTableFlags.BordersInnerV))
        {
            var layerNames = Engine.GameSettings.LayerNames;
            for (int layer = 0; layer < 32; layer++)
            {
                if (layer % columns == 0)
                    ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(layer % columns);

                string label = layerNames.TryGetValue(layer, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : $"Layer {layer}";

                bool enabled = (cullingMask & (1 << layer)) != 0;
                if (ImGui.Checkbox(label, ref enabled))
                {
                    cullingChanged = true;
                    if (enabled)
                        cullingMask |= 1 << layer;
                    else
                        cullingMask &= ~(1 << layer);
                }
            }

            ImGui.EndTable();
        }

        if (cullingChanged)
            component.Camera.CullingMask = new LayerMask(cullingMask);

        string uiOverlay = component.GetUserInterfaceOverlay()?.SceneNode?.Name
            ?? component.GetUserInterfaceOverlay()?.GetType().Name
            ?? "<none>";
        ImGui.TextDisabled($"UI Overlay: {uiOverlay}");

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
        EditorImGuiUI.DrawRuntimeObjectInspector("Projection Object", parameters, visited, defaultOpen: false);
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

    private static void DrawSchemaBasedPostProcessingEditor(CameraComponent component, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Post Processing", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var pipeline = component.Camera.RenderPipeline;
        var schema = pipeline.PostProcessSchema;
        var state = component.Camera.GetActivePostProcessState();

        ImGui.PushID("PostProcessingPanel");

        if (schema.IsEmpty || state is null)
        {
            ImGui.TextDisabled($"Pipeline '{pipeline.DebugName}' exposes no post-processing schema.");
            DrawAdvancedPostProcessingInspector(state, visited);
        }
        else
        {
            DrawSchemaCategories(schema, state);
            DrawAdvancedPostProcessingInspector(state, visited);
        }

        ImGui.PopID();
    }

    private static void DrawSchemaCategories(RenderPipelinePostProcessSchema schema, PipelinePostProcessState state)
    {
        foreach (var category in schema.Categories)
        {
            if (!ImGui.CollapsingHeader(category.DisplayName, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            if (!string.IsNullOrWhiteSpace(category.Description))
                ImGui.TextDisabled(category.Description);

            ImGui.PushID(category.Key);

            foreach (var stageKey in category.StageKeys)
            {
                if (!schema.TryGetStage(stageKey, out var stage) || !state.TryGetStage(stageKey, out var stageState))
                    continue;

                DrawSchemaStage(stage, stageState);
            }

            ImGui.PopID();
        }
    }

    private static void DrawSchemaStage(PostProcessStageDescriptor stage, PostProcessStageState stageState)
    {
        if (stage.Parameters.Count == 0)
            return;

        ImGui.PushID(stage.Key);
        ImGui.TextDisabled($"— {stage.DisplayName} —");

        foreach (var param in stage.Parameters)
            DrawSchemaParameter(param, stageState);

        ImGui.PopID();
    }

    private static void DrawSchemaParameter(PostProcessParameterDescriptor param, PostProcessStageState stageState)
    {
        if (param.VisibilityCondition != null && stageState.BackingInstance != null)
        {
            if (!param.VisibilityCondition(stageState.BackingInstance))
                return;
        }

        ImGui.PushID(param.Name);

        switch (param.Kind)
        {
            case PostProcessParameterKind.Bool:
                DrawBoolParameter(param, stageState);
                break;
            case PostProcessParameterKind.Int:
                DrawIntParameter(param, stageState);
                break;
            case PostProcessParameterKind.Float:
                DrawFloatParameter(param, stageState);
                break;
            case PostProcessParameterKind.Vector2:
                DrawVector2Parameter(param, stageState);
                break;
            case PostProcessParameterKind.Vector3:
                DrawVector3Parameter(param, stageState);
                break;
            case PostProcessParameterKind.Vector4:
                DrawVector4Parameter(param, stageState);
                break;
            default:
                ImGui.TextDisabled($"{param.DisplayName}: (unsupported type)");
                break;
        }

        ImGui.PopID();
    }

    private static void DrawBoolParameter(PostProcessParameterDescriptor param, PostProcessStageState state)
    {
        bool fallback = ExtractDefault(param, false);
        bool value = state.GetValue(param.Name, fallback);
        if (ImGui.Checkbox(param.DisplayName, ref value))
            state.SetValue(param.Name, value);
    }

    private static void DrawIntParameter(PostProcessParameterDescriptor param, PostProcessStageState state)
    {
        if (param.EnumOptions.Count > 0)
        {
            DrawEnumParameter(param, state);
            return;
        }

        int fallback = ExtractDefault(param, 0);
        int value = state.GetValue(param.Name, fallback);

        float min = param.Min ?? int.MinValue;
        float max = param.Max ?? int.MaxValue;

        if (ImGui.SliderInt(param.DisplayName, ref value, (int)min, (int)max))
            state.SetValue(param.Name, value);
    }

    private static void DrawEnumParameter(PostProcessParameterDescriptor param, PostProcessStageState state)
    {
        int fallback = ExtractDefault(param, 0);
        int value = state.GetValue(param.Name, fallback);
        string currentLabel = param.EnumOptions.FirstOrDefault(o => o.Value == value)?.Label ?? value.ToString();

        if (ImGui.BeginCombo(param.DisplayName, currentLabel))
        {
            foreach (var option in param.EnumOptions)
            {
                bool selected = option.Value == value;
                if (ImGui.Selectable(option.Label, selected) && !selected)
                {
                    state.SetValue(param.Name, option.Value);
                    value = option.Value;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
    }

    private static void DrawFloatParameter(PostProcessParameterDescriptor param, PostProcessStageState state)
    {
        float fallback = ExtractDefault(param, 0.0f);
        float value = state.GetValue(param.Name, fallback);
        float min = param.Min ?? 0.0f;
        float max = param.Max ?? 1.0f;
        float step = param.Step ?? 0.01f;

        string format = step < 0.01f ? "%.4f" : step < 0.1f ? "%.3f" : "%.2f";
        bool useSlider = (max - min) <= 1000.0f && max < float.MaxValue / 2;

        if (useSlider)
        {
            if (ImGui.SliderFloat(param.DisplayName, ref value, min, max, format))
                state.SetValue(param.Name, value);
        }
        else
        {
            if (ImGui.DragFloat(param.DisplayName, ref value, step, min, max, format))
                state.SetValue(param.Name, value);
        }
    }

    private static void DrawVector2Parameter(PostProcessParameterDescriptor param, PostProcessStageState state)
    {
        Vector2 fallback = ExtractDefault(param, Vector2.Zero);
        Vector2 value = state.GetValue(param.Name, fallback);

        if (ImGui.DragFloat2(param.DisplayName, ref value, param.Step ?? 0.01f))
            state.SetValue(param.Name, value);
    }

    private static void DrawVector3Parameter(PostProcessParameterDescriptor param, PostProcessStageState state)
    {
        Vector3 fallback = ExtractDefault(param, Vector3.Zero);
        Vector3 value = state.GetValue(param.Name, fallback);

        bool changed = param.IsColor
            ? ImGui.ColorEdit3(param.DisplayName, ref value)
            : ImGui.DragFloat3(param.DisplayName, ref value, param.Step ?? 0.01f);

        if (changed)
            state.SetValue(param.Name, value);
    }

    private static void DrawVector4Parameter(PostProcessParameterDescriptor param, PostProcessStageState state)
    {
        Vector4 fallback = ExtractDefault(param, Vector4.Zero);
        Vector4 value = state.GetValue(param.Name, fallback);

        bool changed = param.IsColor
            ? ImGui.ColorEdit4(param.DisplayName, ref value)
            : ImGui.DragFloat4(param.DisplayName, ref value, param.Step ?? 0.01f);

        if (changed)
            state.SetValue(param.Name, value);
    }

    private static T ExtractDefault<T>(PostProcessParameterDescriptor descriptor, T fallback)
    {
        if (descriptor.DefaultValue is T typed)
            return typed;

        if (descriptor.DefaultValue is null)
            return fallback;

        try
        {
            if (typeof(T) == typeof(Vector2))
            {
                if (descriptor.DefaultValue is Vector3 v3)
                    return (T)(object)new Vector2(v3.X, v3.Y);
                if (descriptor.DefaultValue is Vector4 v4)
                    return (T)(object)new Vector2(v4.X, v4.Y);
            }

            if (typeof(T) == typeof(Vector3) && descriptor.DefaultValue is Vector4 v4Value)
                return (T)(object)new Vector3(v4Value.X, v4Value.Y, v4Value.Z);

            if (descriptor.DefaultValue is IConvertible convertible)
                return (T)Convert.ChangeType(convertible, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
        }

        return fallback;
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
                texture = candidate!;
                return true;
            }
        }

        foreach (string fboName in PreferredPreviewFrameBufferNames)
        {
            if (resources.TryGetFrameBuffer(fboName, out XRFrameBuffer? fbo) && TryExtractTextureFromFbo(fbo, out XRTexture? candidate, out _))
            {
                texture = candidate!;
                return true;
            }
        }

        foreach (XRFrameBuffer fbo in resources.EnumerateFrameBufferInstances())
        {
            if (TryExtractTextureFromFbo(fbo, out XRTexture? candidate, out _))
            {
                texture = candidate!;
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

    private static void DrawAdvancedPostProcessingInspector(PipelinePostProcessState? state, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Advanced Post Processing"))
            return;

        if (state is null)
        {
            ImGui.TextDisabled("No post-processing state available.");
            return;
        }

        ImGui.TextDisabled("Full object view (experimental)");
        EditorImGuiUI.DrawRuntimeObjectInspector("Post Processing State", state, visited, defaultOpen: false);
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

    private sealed class DebugViewState
    {
        public int SelectedPipelineIndex;
        public string? SelectedFboName;
        public bool FlipPreview = true;
    }
}

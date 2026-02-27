using ImGuiNET;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using XREngine.Components;
using XREngine.Data.Core;
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

    /// <summary>
    /// Active camera transitions keyed by camera instance.
    /// </summary>
    private static readonly ConcurrentDictionary<XRCamera, CameraProjectionTransition> _activeTransitions = new();

    /// <summary>
    /// Settings for animated camera transitions.
    /// </summary>
    private static bool _enableAnimatedTransitions = true;
    private static float _transitionDuration = 1.0f;
    private static float _focusDistance = 10f;

    /// <summary>
    /// Cached information about a camera parameter type for the editor dropdown.
    /// </summary>
    private readonly struct ParameterOption
    {
        public string Label { get; }
        public Type ParameterType { get; }
        public int SortOrder { get; }
        public string? Category { get; }
        public string? Description { get; }

        public ParameterOption(Type type)
        {
            ParameterType = type;
            
            // Get attribute if present
            var attr = type.GetCustomAttribute<CameraParameterEditorAttribute>();
            
            // Use attribute values or generate defaults
            Label = !string.IsNullOrEmpty(attr?.DisplayName) 
                ? attr.DisplayName 
                : GenerateFriendlyName(type);
            SortOrder = attr?.SortOrder ?? 100;
            Category = attr?.Category;
            Description = attr?.Description;
        }

        private static string GenerateFriendlyName(Type type)
        {
            string name = type.Name;

            // Remove common prefixes and suffixes
            if (name.StartsWith("XR", StringComparison.Ordinal))
                name = name.Substring(2);
            if (name.EndsWith("CameraParameters", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - "CameraParameters".Length);

            // Insert spaces before capital letters (e.g., "OpenXRFov" -> "Open XR Fov")
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                    result.Append(' ');
                result.Append(c);
            }

            return result.Length > 0 ? result.ToString() : type.Name;
        }
    }

    /// <summary>
    /// Lazily-initialized array of all concrete XRCameraParameters types.
    /// Automatically discovers all available camera parameter types via reflection,
    /// respecting the <see cref="CameraParameterEditorAttribute"/> for ordering and display.
    /// </summary>
    private static ParameterOption[]? _parameterOptions;
    private static ParameterOption[] ParameterOptions => _parameterOptions ??= DiscoverParameterTypes();

    /// <summary>
    /// Discovers all concrete types derived from XRCameraParameters across all loaded assemblies.
    /// Types with <see cref="CameraParameterEditorAttribute.Hidden"/> set to true are excluded.
    /// </summary>
    private static ParameterOption[] DiscoverParameterTypes()
    {
        var baseType = typeof(XRCameraParameters);
        
        // Search in the main engine assembly and any assemblies that reference it
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && ReferencesAssembly(a, baseType.Assembly));

        var types = assemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .Where(t => t.IsClass && !t.IsAbstract && baseType.IsAssignableFrom(t))
            .Where(t =>
            {
                var attr = t.GetCustomAttribute<CameraParameterEditorAttribute>();
                return attr?.Hidden != true;
            })
            .Select(t => new ParameterOption(t))
            .OrderBy(o => o.SortOrder)
            .ThenBy(o => o.Category ?? "")
            .ThenBy(o => o.Label)
            .ToArray();

        return types.Length > 0 ? types : [new ParameterOption(typeof(XRPerspectiveCameraParameters))];
    }

    /// <summary>
    /// Checks if an assembly references or is the target assembly.
    /// </summary>
    private static bool ReferencesAssembly(Assembly assembly, Assembly target)
    {
        if (assembly == target)
            return true;
        
        try
        {
            return assembly.GetReferencedAssemblies()
                .Any(r => r.FullName == target.FullName);
        }
        catch
        {
            return false;
        }
    }


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

        // Authoritative viewport bindings: scan live windows.
        var boundViewports = new List<XRViewport>();
        foreach (var vp in Engine.EnumerateActiveViewports())
        {
            if (ReferenceEquals(vp.CameraComponent, component))
                boundViewports.Add(vp);
        }

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
        var cam = component.Camera;
        int viewportCount = boundViewports.Count;
        ImGui.TextDisabled($"(CameraHash: {cam.GetHashCode()})");
        ImGui.TextDisabled($"(XRCamera.Viewports: {cam.Viewports.Count})");
        if (viewportCount > 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.4f, 1.0f), $"Viewports: {viewportCount}");
            if (ImGui.IsItemHovered())
            {
                string tooltip = "Bound viewports:\n";
                for (int i = 0; i < boundViewports.Count; i++)
                {
                    var vp = boundViewports[i];
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

        // Internal Resolution Settings
        DrawInternalResolutionSettings(component);

        ImGui.Separator();
        ImGui.TextDisabled("Render Pipeline Asset");
        var pipeline = component.Camera.RenderPipeline;
        ImGuiAssetUtilities.DrawAssetField<RenderPipeline>("CameraRenderPipeline", pipeline, asset =>
        {
            component.Camera.RenderPipeline = asset ?? Engine.Rendering.NewRenderPipeline();
        }, allowClear: false, allowCreateOrReplace: true);

        ImGui.TextDisabled("Default Render Target Asset");
        ImGuiAssetUtilities.DrawAssetField<XRFrameBuffer>("CameraDefaultRenderTarget", component.DefaultRenderTarget, asset =>
        {
            component.DefaultRenderTarget = asset;
        });
    }

    private static readonly string[] InternalResolutionModeNames = 
    [
        "Full Resolution",
        "Scale",
        "Manual"
    ];

    private static void DrawInternalResolutionSettings(CameraComponent component)
    {
        ImGui.Text("Internal Resolution");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Controls the rendering resolution used by the camera.\nLower resolutions improve performance but reduce quality.");

        // Mode selector
        int modeIndex = (int)component.InternalResolutionMode;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.Combo("Mode##InternalResMode", ref modeIndex, InternalResolutionModeNames, InternalResolutionModeNames.Length))
        {
            component.InternalResolutionMode = (EInternalResolutionMode)modeIndex;
        }

        // Show relevant controls based on mode
        switch (component.InternalResolutionMode)
        {
            case EInternalResolutionMode.FullResolution:
                ImGui.TextDisabled("Renders at viewport's native resolution.");
                break;

            case EInternalResolutionMode.Scale:
                float scale = component.InternalResolutionScale;
                ImGui.SetNextItemWidth(150f);
                if (ImGui.SliderFloat("Scale##InternalResScale", ref scale, 0.1f, 2.0f, "%.2fx"))
                {
                    component.InternalResolutionScale = scale;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("1.0 = native resolution\n0.5 = half resolution (faster)\n2.0 = supersampling (slower, higher quality)");

                // Quick preset buttons
                ImGui.SameLine();
                if (ImGui.SmallButton("0.5x"))
                    component.InternalResolutionScale = 0.5f;
                ImGui.SameLine();
                if (ImGui.SmallButton("1x"))
                    component.InternalResolutionScale = 1.0f;
                ImGui.SameLine();
                if (ImGui.SmallButton("1.5x"))
                    component.InternalResolutionScale = 1.5f;
                break;

            case EInternalResolutionMode.Manual:
                int width = component.ManualInternalWidth;
                int height = component.ManualInternalHeight;

                ImGui.SetNextItemWidth(100f);
                if (ImGui.InputInt("Width##InternalResWidth", ref width, 1, 100))
                {
                    component.ManualInternalWidth = Math.Max(1, width);
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100f);
                if (ImGui.InputInt("Height##InternalResHeight", ref height, 1, 100))
                {
                    component.ManualInternalHeight = Math.Max(1, height);
                }

                // Common resolution presets
                ImGui.Text("Presets:");
                ImGui.SameLine();
                if (ImGui.SmallButton("720p"))
                {
                    component.ManualInternalWidth = 1280;
                    component.ManualInternalHeight = 720;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("1080p"))
                {
                    component.ManualInternalWidth = 1920;
                    component.ManualInternalHeight = 1080;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("1440p"))
                {
                    component.ManualInternalWidth = 2560;
                    component.ManualInternalHeight = 1440;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("4K"))
                {
                    component.ManualInternalWidth = 3840;
                    component.ManualInternalHeight = 2160;
                }
                break;
        }

        // Show current effective resolution if camera has viewports
        if (component.Camera.Viewports.Count > 0)
        {
            var viewport = component.Camera.Viewports[0];
            ImGui.TextDisabled($"Effective: {viewport.InternalWidth}x{viewport.InternalHeight} → {viewport.Width}x{viewport.Height}");
        }
    }

    private static void DrawParameterSection(CameraComponent component, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Projection Parameters", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var camera = component.Camera;
        var parameters = camera.Parameters;

        DrawParameterTypeSwitcher(camera, parameters);
        DrawCommonParameterControls(parameters);

        // Check for custom editor from attribute first
        var customEditor = CameraParameterEditorRegistry.GetEditor(parameters.GetType());
        if (customEditor is not null)
        {
            customEditor.DrawEditor(parameters);
        }
        else
        {
            // Fall back to built-in editors for known types
            switch (parameters)
            {
                case XRPerspectiveCameraParameters perspective:
                    DrawPerspectiveParameters(perspective);
                    break;
                case XROrthographicCameraParameters orthographic:
                    DrawOrthographicParameters(orthographic);
                    break;
                case XRPhysicalCameraParameters physical:
                    DrawPhysicalCameraParameters(physical);
                    break;
                case XROpenXRFovCameraParameters openXrFov:
                    DrawOpenXRFovParameters(openXrFov);
                    break;
                case XROVRCameraParameters ovr:
                    DrawOpenVREyeParameters(ovr);
                    break;
                default:
                    ImGui.TextDisabled($"No custom editor for {parameters.GetType().Name}.");
                    ImGui.TextDisabled("Add [CameraParameterEditor] attribute with CustomEditorType to provide a custom UI.");
                    break;
            }
        }

        ImGui.Spacing();
        EditorImGuiUI.DrawRuntimeObjectInspector("Projection Object", parameters, visited, defaultOpen: false);
    }

    private static void DrawParameterTypeSwitcher(XRCamera camera, XRCameraParameters current)
    {
        int currentIndex = Array.FindIndex(ParameterOptions, option => option.ParameterType == current.GetType());
        
        // Handle unknown types that aren't in the discovered list
        string currentLabel = currentIndex >= 0 
            ? ParameterOptions[currentIndex].Label 
            : new ParameterOption(current.GetType()).Label;

        // Show category/description for current type
        var currentOption = currentIndex >= 0 ? ParameterOptions[currentIndex] : new ParameterOption(current.GetType());
        
        // Check if a transition is in progress
        bool isTransitioning = _activeTransitions.TryGetValue(camera, out var transition) && transition.IsTransitioning;
        
        // Show transition progress if active
        if (isTransitioning && transition is not null)
        {
            ImGui.ProgressBar(transition.Progress, new Vector2(-1, 0), "Transitioning...");
            ImGui.BeginDisabled();
        }
        
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("Projection Type", currentLabel))
        {
            string? lastCategory = null;
            
            for (int i = 0; i < ParameterOptions.Length; i++)
            {
                var option = ParameterOptions[i];
                
                // Draw category separator if category changes
                if (option.Category != lastCategory && !string.IsNullOrEmpty(option.Category))
                {
                    if (i > 0)
                        ImGui.Separator();
                    ImGui.TextDisabled(option.Category);
                    lastCategory = option.Category;
                }
                else if (option.Category != lastCategory)
                {
                    lastCategory = option.Category;
                }

                bool selected = i == currentIndex;
                if (ImGui.Selectable(option.Label, selected) && !selected && !isTransitioning)
                {
                    SwitchCameraType(camera, current, option.ParameterType);
                }

                // Show tooltip with description if available
                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(option.Description))
                {
                    string tooltip = option.Description;
                    if (_enableAnimatedTransitions && CameraProjectionTransition.CanAnimateTransition(current.GetType(), option.ParameterType))
                        tooltip += "\n\n(Animated transition available)";
                    ImGui.SetTooltip(tooltip);
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        
        if (isTransitioning)
            ImGui.EndDisabled();
        
        // Show description below the combo if current type has one
        if (!string.IsNullOrEmpty(currentOption.Description))
            ImGui.TextDisabled(currentOption.Description);
            
        // Transition settings (collapsible)
        DrawTransitionSettings();
    }

    /// <summary>
    /// Draws the transition settings UI.
    /// </summary>
    private static void DrawTransitionSettings()
    {
        if (ImGui.TreeNode("Transition Settings"))
        {
            ImGui.Checkbox("Animated Transitions", ref _enableAnimatedTransitions);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When enabled, switching between Perspective and Orthographic\nwill use a smooth dolly zoom animation.");
                
            if (_enableAnimatedTransitions)
            {
                ImGui.SliderFloat("Duration", ref _transitionDuration, 0.1f, 3.0f, "%.1f sec");
                ImGui.DragFloat("Focus Distance", ref _focusDistance, 0.5f, 0.1f, 1000f, "%.1f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Distance from camera to the focus point.\nObjects at this distance maintain their apparent size during transition.");
            }
            
            ImGui.TreePop();
        }
    }

    /// <summary>
    /// Handles switching camera types, potentially with animation.
    /// </summary>
    private static void SwitchCameraType(XRCamera camera, XRCameraParameters current, Type targetType)
    {
        // Cancel any existing transition
        if (_activeTransitions.TryRemove(camera, out var existingTransition))
            existingTransition.Cancel();

        // Check if we should animate
        if (_enableAnimatedTransitions && CameraProjectionTransition.CanAnimateTransition(current.GetType(), targetType))
        {
            var newTransition = new CameraProjectionTransition(camera, camera.Transform);
            
            // Register completion handler
            newTransition.TransitionCompleted += OnTransitionCompleted;
            
            _activeTransitions[camera] = newTransition;
            
            // Start the appropriate transition
            if (current is XRPerspectiveCameraParameters && targetType == typeof(XROrthographicCameraParameters))
            {
                newTransition.StartPerspectiveToOrthographic(_transitionDuration, _focusDistance);
            }
            else if (current is XROrthographicCameraParameters && targetType == typeof(XRPerspectiveCameraParameters))
            {
                // Get the target FOV from the current perspective settings or default to 60
                float targetFov = 60f;
                newTransition.StartOrthographicToPerspective(_transitionDuration, targetFov, _focusDistance);
            }
        }
        else
        {
            // No animation, just switch directly
            XRCameraParameters replacement = CreateParameterInstance(targetType, current);
            camera.Parameters = replacement;
        }
    }

    /// <summary>
    /// Called when a camera transition completes.
    /// </summary>
    private static void OnTransitionCompleted(CameraProjectionTransition transition)
    {
        // Find and remove the completed transition
        foreach (var kvp in _activeTransitions)
        {
            if (kvp.Value == transition)
            {
                _activeTransitions.TryRemove(kvp.Key, out _);
                break;
            }
        }
    }

    /// <summary>
    /// Creates a new camera parameter instance of the specified type, using <see cref="XRCameraParameters.CreateFromPrevious"/>
    /// to intelligently convert settings from the previous parameter type.
    /// </summary>
    /// <remarks>
    /// This method first attempts to use the parameter type's own <c>CreateFromPrevious</c> method,
    /// which allows each camera type to define its own conversion logic. Custom camera types
    /// can override this method to provide intelligent conversions from other types.
    /// </remarks>
    private static XRCameraParameters CreateParameterInstance(Type targetType, XRCameraParameters previous)
    {
        // Use the CreateFromPrevious pattern which allows each type to define its own conversion logic
        var instance = XRCameraParameters.CreateFromType(targetType, previous);
        
        // Ensure near/far planes are preserved
        instance.NearZ = previous.NearZ;
        instance.FarZ = previous.FarZ;
        
        return instance;
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
        // Aspect Ratio Section
        ImGui.SeparatorText("Aspect Ratio");
        
        bool inheritAspect = parameters.InheritAspectRatio;
        if (ImGui.Checkbox("Inherit Aspect Ratio", ref inheritAspect))
            parameters.InheritAspectRatio = inheritAspect;

        // Width is disabled when inheriting aspect ratio (it's calculated from height)
        using (new ImGuiDisabledScope(parameters.InheritAspectRatio))
        {
            float width = parameters.Width;
            if (ImGui.DragFloat("Width", ref width, 0.1f, 0.01f, 100000f, "%.2f"))
                parameters.Width = MathF.Max(0.01f, width);
        }

        // Height is the primary control - always editable
        float height = parameters.Height;
        if (ImGui.DragFloat("Height", ref height, 0.1f, 0.01f, 100000f, "%.2f"))
            parameters.Height = MathF.Max(0.01f, height);

        ImGui.TextDisabled($"Aspect Ratio: {parameters.AspectRatio:F3}");

        // Origin Section
        ImGui.SeparatorText("Origin");

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

    /// <summary>
    /// Draws the custom editor UI for physical camera parameters.
    /// Shows sensor size, focal length, resolution, and principal point settings.
    /// </summary>
    private static void DrawPhysicalCameraParameters(XRPhysicalCameraParameters parameters)
    {
        // Sensor Size Section
        ImGui.SeparatorText("Sensor / Filmback");

        float sensorW = parameters.SensorWidthMm;
        if (ImGui.DragFloat("Sensor Width (mm)", ref sensorW, 0.1f, 0.1f, 100.0f, "%.2f"))
            parameters.SensorWidthMm = MathF.Max(0.1f, sensorW);

        float sensorH = parameters.SensorHeightMm;
        if (ImGui.DragFloat("Sensor Height (mm)", ref sensorH, 0.1f, 0.1f, 100.0f, "%.2f"))
            parameters.SensorHeightMm = MathF.Max(0.1f, sensorH);

        // Sensor presets
        if (ImGui.BeginCombo("Sensor Preset", "Select"))
        {
            if (ImGui.Selectable("Full Frame (36x24mm)"))
            {
                parameters.SensorWidthMm = 36.0f;
                parameters.SensorHeightMm = 24.0f;
            }
            if (ImGui.Selectable("APS-C (23.5x15.6mm)"))
            {
                parameters.SensorWidthMm = 23.5f;
                parameters.SensorHeightMm = 15.6f;
            }
            if (ImGui.Selectable("Super 35 (24.89x18.66mm)"))
            {
                parameters.SensorWidthMm = 24.89f;
                parameters.SensorHeightMm = 18.66f;
            }
            if (ImGui.Selectable("Micro Four Thirds (17.3x13mm)"))
            {
                parameters.SensorWidthMm = 17.3f;
                parameters.SensorHeightMm = 13.0f;
            }
            if (ImGui.Selectable("IMAX (70x48.5mm)"))
            {
                parameters.SensorWidthMm = 70.0f;
                parameters.SensorHeightMm = 48.5f;
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();

        // Lens Section
        ImGui.SeparatorText("Lens");

        float focalLength = parameters.FocalLengthMm;
        if (ImGui.DragFloat("Focal Length (mm)", ref focalLength, 0.5f, 1.0f, 1200.0f, "%.1f"))
            parameters.FocalLengthMm = MathF.Max(1.0f, focalLength);

        // Common focal length presets
        if (ImGui.BeginCombo("Focal Length Preset", "Select"))
        {
            float[] presets = [14, 24, 35, 50, 85, 100, 135, 200, 300, 400, 600];
            foreach (float preset in presets)
            {
                if (ImGui.Selectable($"{preset}mm"))
                    parameters.FocalLengthMm = preset;
            }
            ImGui.EndCombo();
        }

        ImGui.TextDisabled($"Vertical FOV: {parameters.VerticalFieldOfViewDegrees:F1}°");
        ImGui.TextDisabled($"Horizontal FOV: {parameters.HorizontalFieldOfViewDegrees:F1}°");

        ImGui.Spacing();

        // Resolution Section
        ImGui.SeparatorText("Output Resolution");

        bool inheritRes = parameters.InheritResolution;
        if (ImGui.Checkbox("Inherit from Render Area", ref inheritRes))
            parameters.InheritResolution = inheritRes;

        using (new ImGuiDisabledScope(parameters.InheritResolution))
        {
            int resW = parameters.ResolutionWidthPx;
            if (ImGui.DragInt("Width (px)", ref resW, 1.0f, 1, 16384))
                parameters.ResolutionWidthPx = Math.Max(1, resW);

            int resH = parameters.ResolutionHeightPx;
            if (ImGui.DragInt("Height (px)", ref resH, 1.0f, 1, 16384))
                parameters.ResolutionHeightPx = Math.Max(1, resH);
        }

        ImGui.Spacing();

        // Principal Point Section
        ImGui.SeparatorText("Principal Point");

        bool inheritPP = parameters.InheritPrincipalPoint;
        if (ImGui.Checkbox("Center (Auto)", ref inheritPP))
            parameters.InheritPrincipalPoint = inheritPP;

        using (new ImGuiDisabledScope(parameters.InheritPrincipalPoint))
        {
            Vector2 pp = parameters.PrincipalPointPx;
            if (ImGui.DragFloat2("Principal Point (px)", ref pp, 1.0f))
                parameters.PrincipalPointPx = pp;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The optical center of the lens.\nOrigin is top-left, X right, Y down.\nUsed for off-axis projections.");
    }

    /// <summary>
    /// Draws the custom editor UI for OpenXR asymmetric FOV camera parameters.
    /// Shows the four angle boundaries in both radians and degrees.
    /// </summary>
    private static void DrawOpenXRFovParameters(XROpenXRFovCameraParameters parameters)
    {
        ImGui.SeparatorText("Asymmetric FOV Angles");

        ImGui.TextDisabled("Angles are in radians. Positive = outward from center.");
        ImGui.TextDisabled("Left/Down are typically negative, Right/Up are positive.");

        ImGui.Spacing();

        // Left angle (typically negative)
        float left = parameters.AngleLeft;
        float leftDeg = float.RadiansToDegrees(left);
        if (ImGui.DragFloat("Left Angle", ref left, 0.01f, -MathF.PI / 2.0f, 0.0f, $"{left:F3} rad ({leftDeg:F1}°)"))
            parameters.AngleLeft = left;

        // Right angle (typically positive)
        float right = parameters.AngleRight;
        float rightDeg = float.RadiansToDegrees(right);
        if (ImGui.DragFloat("Right Angle", ref right, 0.01f, 0.0f, MathF.PI / 2.0f, $"{right:F3} rad ({rightDeg:F1}°)"))
            parameters.AngleRight = right;

        // Up angle (typically positive)
        float up = parameters.AngleUp;
        float upDeg = float.RadiansToDegrees(up);
        if (ImGui.DragFloat("Up Angle", ref up, 0.01f, 0.0f, MathF.PI / 2.0f, $"{up:F3} rad ({upDeg:F1}°)"))
            parameters.AngleUp = up;

        // Down angle (typically negative)
        float down = parameters.AngleDown;
        float downDeg = float.RadiansToDegrees(down);
        if (ImGui.DragFloat("Down Angle", ref down, 0.01f, -MathF.PI / 2.0f, 0.0f, $"{down:F3} rad ({downDeg:F1}°)"))
            parameters.AngleDown = down;

        ImGui.Spacing();

        // Calculated total FOV
        float hFov = float.RadiansToDegrees(right - left);
        float vFov = float.RadiansToDegrees(up - down);
        ImGui.TextDisabled($"Total Horizontal FOV: {hFov:F1}°");
        ImGui.TextDisabled($"Total Vertical FOV: {vFov:F1}°");

        ImGui.Spacing();

        // Symmetric preset button
        if (ImGui.Button("Make Symmetric"))
        {
            float avgH = (MathF.Abs(left) + MathF.Abs(right)) / 2.0f;
            float avgV = (MathF.Abs(up) + MathF.Abs(down)) / 2.0f;
            parameters.SetAngles(-avgH, avgH, avgV, -avgV);
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset to 90° Symmetric"))
        {
            float halfFov = MathF.PI / 4.0f; // 45 degrees
            parameters.SetAngles(-halfFov, halfFov, halfFov, -halfFov);
        }
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
            DrawSchemaCategories(schema, state, component);
            DrawAdvancedPostProcessingInspector(state, visited);
        }

        ImGui.PopID();
    }

    private static void DrawSchemaCategories(RenderPipelinePostProcessSchema schema, PipelinePostProcessState state, CameraComponent component)
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

                DrawSchemaStage(stage, stageState, component);
            }

            ImGui.PopID();
        }
    }

    private static void DrawSchemaStage(PostProcessStageDescriptor stage, PostProcessStageState stageState, CameraComponent component)
    {
        if (stage.Parameters.Count == 0)
            return;

        ImGui.PushID(stage.Key);
        ImGui.TextDisabled($"— {stage.DisplayName} —");

        XRBase? undoTarget = (stageState.BackingInstance as XRBase) ?? component;

        foreach (var param in stage.Parameters)
            DrawSchemaParameter(param, stageState, undoTarget);

        ImGui.PopID();
    }

    private static void DrawSchemaParameter(PostProcessParameterDescriptor param, PostProcessStageState stageState, XRBase? undoTarget)
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
                DrawBoolParameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Int:
                DrawIntParameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Float:
                DrawFloatParameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Vector2:
                DrawVector2Parameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Vector3:
                DrawVector3Parameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Vector4:
                DrawVector4Parameter(param, stageState, undoTarget);
                break;
            default:
                ImGui.TextDisabled($"{param.DisplayName}: (unsupported type)");
                break;
        }

        ImGui.PopID();
    }

    private static void DrawBoolParameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        bool fallback = ExtractDefault(param, false);
        bool value = state.GetValue(param.Name, fallback);
        if (ImGui.Checkbox(param.DisplayName, ref value))
        {
            using var _ = Undo.TrackChange(param.DisplayName, undoTarget);
            state.SetValue(param.Name, value);
        }
    }

    private static void DrawIntParameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        if (param.EnumOptions.Count > 0)
        {
            DrawEnumParameter(param, state, undoTarget);
            return;
        }

        int fallback = ExtractDefault(param, 0);
        int value = state.GetValue(param.Name, fallback);

        float min = param.Min ?? int.MinValue;
        float max = param.Max ?? int.MaxValue;

        if (ImGui.SliderInt(param.DisplayName, ref value, (int)min, (int)max))
            state.SetValue(param.Name, value);
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
    }

    private static void DrawEnumParameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
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
                    using var _ = Undo.TrackChange(param.DisplayName, undoTarget);
                    state.SetValue(param.Name, option.Value);
                    value = option.Value;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
    }

    private static void DrawFloatParameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
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
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
    }

    private static void DrawVector2Parameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        Vector2 fallback = ExtractDefault(param, Vector2.Zero);
        Vector2 value = state.GetValue(param.Name, fallback);

        if (ImGui.DragFloat2(param.DisplayName, ref value, param.Step ?? 0.01f))
            state.SetValue(param.Name, value);
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
    }

    private static void DrawVector3Parameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        Vector3 fallback = ExtractDefault(param, Vector3.Zero);
        Vector3 value = state.GetValue(param.Name, fallback);

        if (param.Name == nameof(ColorGradingSettings.AutoExposureLuminanceWeights))
        {
            bool changed2 = ImGui.DragFloat3(param.DisplayName, ref value, param.Step ?? 0.001f);
            if (changed2)
            {
                value = NormalizeLuminanceWeights(value, Engine.Rendering.Settings.DefaultLuminance);
                state.SetValue(param.Name, value);
            }
            ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);

            ImGui.SameLine();
            if (ImGui.SmallButton("Default"))
            {
                using var _ = Undo.TrackChange("Luminance Weights Default", undoTarget);
                value = NormalizeLuminanceWeights(Engine.Rendering.Settings.DefaultLuminance, Engine.Rendering.Settings.DefaultLuminance);
                state.SetValue(param.Name, value);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Rec.709"))
            {
                using var _ = Undo.TrackChange("Luminance Weights Rec.709", undoTarget);
                value = NormalizeLuminanceWeights(new Vector3(0.2126f, 0.7152f, 0.0722f), Engine.Rendering.Settings.DefaultLuminance);
                state.SetValue(param.Name, value);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Rec.601"))
            {
                using var _ = Undo.TrackChange("Luminance Weights Rec.601", undoTarget);
                value = NormalizeLuminanceWeights(new Vector3(0.299f, 0.587f, 0.114f), Engine.Rendering.Settings.DefaultLuminance);
                state.SetValue(param.Name, value);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Equal"))
            {
                using var _ = Undo.TrackChange("Luminance Weights Equal", undoTarget);
                value = NormalizeLuminanceWeights(new Vector3(1.0f, 1.0f, 1.0f), Engine.Rendering.Settings.DefaultLuminance);
                state.SetValue(param.Name, value);
            }

            float sum = value.X + value.Y + value.Z;
            ImGui.TextDisabled($"Normalized (sum={sum:0.###})");
            return;
        }

        bool changed = param.IsColor
            ? ImGui.ColorEdit3(param.DisplayName, ref value)
            : ImGui.DragFloat3(param.DisplayName, ref value, param.Step ?? 0.01f);

        if (changed)
            state.SetValue(param.Name, value);
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
    }

    private static Vector3 NormalizeLuminanceWeights(Vector3 w, Vector3 fallback)
    {
        static float Sanitize(float v) => float.IsFinite(v) ? MathF.Max(0.0f, v) : 0.0f;

        w = new Vector3(Sanitize(w.X), Sanitize(w.Y), Sanitize(w.Z));
        float sum = w.X + w.Y + w.Z;
        if (!(sum > 0.0f) || float.IsNaN(sum) || float.IsInfinity(sum))
            return NormalizeLuminanceWeights(fallback, fallback);
        return w / sum;
    }

    private static void DrawVector4Parameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        Vector4 fallback = ExtractDefault(param, Vector4.Zero);
        Vector4 value = state.GetValue(param.Name, fallback);

        bool changed = param.IsColor
            ? ImGui.ColorEdit4(param.DisplayName, ref value)
            : ImGui.DragFloat4(param.DisplayName, ref value, param.Step ?? 0.01f);

        if (changed)
            state.SetValue(param.Name, value);
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
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

        // Take a snapshot of the viewports collection to avoid "Collection was modified" exceptions
        // during play mode transitions when viewports may be added/removed concurrently.
        XRViewport[] viewportsSnapshot;
        try
        {
            viewportsSnapshot = [.. component.Camera.Viewports];
        }
        catch (InvalidOperationException)
        {
            // Collection was modified during snapshot - return failure gracefully
            texture = null;
            pixelSize = Vector2.Zero;
            failure = "Viewports collection is being modified.";
            return false;
        }

        foreach (var viewport in viewportsSnapshot)
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

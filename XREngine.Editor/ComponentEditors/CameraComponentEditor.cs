using ImGuiNET;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Editor.ComponentEditors.PostProcessDrawers;
using XREngine.Editor.Services;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.ComponentEditors;

public sealed partial class CameraComponentEditor : IXRComponentEditor
{

    /// <summary>
    /// Active camera transitions keyed by camera instance.
    /// </summary>
    private static readonly ConcurrentDictionary<XRCamera, CameraProjectionTransition> _activeTransitions = new();

    private sealed class PostProcessStageSelectorState
    {
        public string SelectedStageKey = string.Empty;
    }


    private readonly struct PostProcessStageEntry
    {
        public PostProcessStageEntry(PostProcessCategoryDescriptor? category, PostProcessStageDescriptor descriptor, PostProcessStageState state)
        {
            Category = category;
            Descriptor = descriptor;
            State = state;
        }

        public PostProcessCategoryDescriptor? Category { get; }
        public PostProcessStageDescriptor Descriptor { get; }
        public PostProcessStageState State { get; }
    }
    private static readonly ConditionalWeakTable<PipelinePostProcessState, PostProcessStageSelectorState> PostProcessStageSelectorStates = new();
    private static readonly List<XRViewport> BoundViewportScratch = [];

    private sealed class CameraMetadataCacheState(long generation)
    {
        public long Generation { get; } = generation;
        public ConcurrentDictionary<Type, PropertyInfo[]> PipelineSettingProperties { get; } = new();
        public ConcurrentDictionary<Type, RenderPipeline> FallbackPipelines { get; } = new();
        public ParameterOption[]? ParameterOptions;
    }

    private sealed class EditorFallbackPipelineState
    {
        private readonly object _sync = new();
        private readonly ConditionalWeakTable<XRCamera, PerCameraFallbackState> _states = new();
        private readonly List<WeakReference<PerCameraFallbackState>> _ownedStates = [];

        public PipelinePostProcessState GetOrCreate(XRCamera camera, RenderPipeline pipeline)
        {
            lock (_sync)
            {
                if (!_states.TryGetValue(camera, out PerCameraFallbackState? state))
                {
                    state = new PerCameraFallbackState();
                    _states.Add(camera, state);
                    _ownedStates.Add(new WeakReference<PerCameraFallbackState>(state));
                }

                return state.GetOrCreate(pipeline);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                for (int i = 0; i < _ownedStates.Count; i++)
                    if (_ownedStates[i].TryGetTarget(out PerCameraFallbackState? state))
                        state.Dispose();
                _ownedStates.Clear();
            }
        }

        private sealed class PerCameraFallbackState
        {
            private PipelinePostProcessState? _state;
            private RenderPipelinePostProcessSchema? _schema;

            public PipelinePostProcessState GetOrCreate(RenderPipeline pipeline)
            {
                RenderPipelinePostProcessSchema schema = pipeline.PostProcessSchema;
                if (_state is not null && ReferenceEquals(_schema, schema))
                    return _state;

                Dispose();
                _state = new PipelinePostProcessState();
                _state.BindToPipeline(pipeline);
                _schema = schema;
                return _state;
            }

            public void Dispose()
            {
                if (_state is not null)
                    foreach (PostProcessStageState stage in _state.Stages.Values)
                        stage.Dispose();

                _state = null;
                _schema = null;
            }
        }
    }

    private static CameraMetadataCacheState _metadataCacheState =
        new(EditorTypeCatalogGeneration.Current);
    private static readonly ConcurrentQueue<CameraMetadataCacheState> RetiredMetadataStates = new();
    private static readonly ConditionalWeakTable<RenderPipeline, EditorFallbackPipelineState> EditorFallbackPipelineStates = new();
    private static IEnumerator<RenderPipeline>? _retiredPipelineEnumerator;

    static CameraComponentEditor()
        => EditorTypeCatalogGeneration.Changed += ReplaceMetadataCacheState;

    private static void ReplaceMetadataCacheState(long generation)
    {
        CameraMetadataCacheState state = Volatile.Read(ref _metadataCacheState);
        if (state.Generation >= generation)
            return;

        var replacement = new CameraMetadataCacheState(generation);
        if (!ReferenceEquals(
            Interlocked.CompareExchange(ref _metadataCacheState, replacement, state),
            state))
        {
            return;
        }

        RetiredMetadataStates.Enqueue(state);
    }

    /// <summary>
    /// Releases editor-owned fallback pipelines after their type generation has
    /// been detached. This is called by the ImGui owner before drawing a frame.
    /// </summary>
    internal static void ProcessTypeCatalogOwnerWork()
    {
        const int maxRetiredStatesPerFrame = 8;
        const int maxRetiredPipelinesPerFrame = 4;
        int retiredStateCount = 0;
        int retiredPipelineCount = 0;

        while (retiredStateCount < maxRetiredStatesPerFrame
            && retiredPipelineCount < maxRetiredPipelinesPerFrame)
        {
            if (_retiredPipelineEnumerator is null)
            {
                if (!RetiredMetadataStates.TryDequeue(out CameraMetadataCacheState? retired))
                    return;

                _retiredPipelineEnumerator = retired.FallbackPipelines.Values.GetEnumerator();
                retiredStateCount++;
            }

            if (!_retiredPipelineEnumerator.MoveNext())
            {
                _retiredPipelineEnumerator.Dispose();
                _retiredPipelineEnumerator = null;
                continue;
            }

            RenderPipeline pipeline = _retiredPipelineEnumerator.Current;
            if (EditorFallbackPipelineStates.TryGetValue(pipeline, out EditorFallbackPipelineState? state))
            {
                state.Dispose();
                EditorFallbackPipelineStates.Remove(pipeline);
            }

            pipeline.Destroy();
            retiredPipelineCount++;
        }
    }

    private static CameraMetadataCacheState GetCurrentMetadataCacheState()
    {
        CameraMetadataCacheState state = Volatile.Read(ref _metadataCacheState);
        long generation = EditorTypeCatalogGeneration.Current;
        if (state.Generation == generation)
            return state;

        var replacement = new CameraMetadataCacheState(generation);
        CameraMetadataCacheState observed = Interlocked.CompareExchange(
            ref _metadataCacheState,
            replacement,
            state);
        if (ReferenceEquals(observed, state))
        {
            RetiredMetadataStates.Enqueue(state);
            return replacement;
        }

        return observed;
    }

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
    private static ParameterOption[] GetParameterOptions(out long generation)
    {
        const int maxGenerationAttempts = 2;
        for (int attempt = 0; attempt < maxGenerationAttempts; attempt++)
        {
            CameraMetadataCacheState state = GetCurrentMetadataCacheState();
            if (state.Generation != EditorTypeCatalogGeneration.Current)
                continue;

            ParameterOption[]? cached = Volatile.Read(ref state.ParameterOptions);
            if (cached is not null)
            {
                generation = state.Generation;
                return cached;
            }

            ParameterOption[] discovered = DiscoverParameterTypes();
            if (state.Generation != EditorTypeCatalogGeneration.Current)
                continue;

            cached = Interlocked.CompareExchange(ref state.ParameterOptions, discovered, null) ?? discovered;
            if (state.Generation != EditorTypeCatalogGeneration.Current)
                continue;

            generation = state.Generation;
            return cached;
        }

        generation = EditorTypeCatalogGeneration.Current;
        return [];
    }

    /// <summary>
    /// Discovers all concrete types derived from XRCameraParameters across all loaded assemblies.
    /// Types with <see cref="CameraParameterEditorAttribute.Hidden"/> set to true are excluded.
    /// </summary>
    private static ParameterOption[] DiscoverParameterTypes()
    {
        var baseType = typeof(XRCameraParameters);
        
        // Search in the main engine assembly and any assemblies that reference it
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => EditorTypeCatalogGeneration.IsAssemblyDiscoverable(a)
                && ReferencesAssembly(a, baseType.Assembly));

        var types = assemblies
            .SelectMany(static assembly => XREngine.Core.XRLoadableTypeCatalog.GetTypes(assembly))
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

    public void DrawInspector(XRComponent component, HashSet<object> visited)
        => DrawInspector(component, visited, null);

    internal void DrawInspector(XRComponent component, HashSet<object> visited, RenderPipeline? defaultPipeline,
        XRViewport? viewport = null, XRRenderPipelineInstance? pipelineInstance = null)
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

        DrawRuntimeCameraEditor(cameraComponent.Camera, defaultPipeline, cameraComponent, visited, viewport, pipelineInstance);
    }



    private static bool DrawRenderPipelineCameraSettings(RenderPipeline pipeline)
    {
        PropertyInfo[] properties = GetPipelineCameraSettingProperties(pipeline.GetType());

        if (properties.Length == 0)
            return false;

        ImGui.SeparatorText("Pipeline Settings");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Settings exposed by the selected pipeline asset; all render instances using that asset share them.");

        ImGui.TextWrapped($"Pipeline: {pipeline.DebugName}");
        ImGui.PushID("RenderPipelineCameraSettings");

        string? currentCategory = null;
        foreach (PropertyInfo property in properties)
        {
            string category = property.GetCustomAttribute<CategoryAttribute>(inherit: true)?.Category ?? "Pipeline";
            if (!string.Equals(category, currentCategory, StringComparison.Ordinal))
            {
                if (currentCategory is not null)
                    ImGui.Spacing();

                ImGui.SeparatorText(category);
                currentCategory = category;
            }

            DrawPipelineCameraSettingProperty(pipeline, property);
        }



        ImGui.PopID();
        return true;
    }

    private static PropertyInfo[] GetPipelineCameraSettingProperties(Type pipelineType)
    {
        if (!EditorTypeCatalogGeneration.IsTypeDiscoverable(pipelineType))
            return [];

        const int maxGenerationAttempts = 2;
        for (int attempt = 0; attempt < maxGenerationAttempts; attempt++)
        {
            CameraMetadataCacheState state = GetCurrentMetadataCacheState();
            if (state.Generation != EditorTypeCatalogGeneration.Current)
                continue;

            PropertyInfo[] properties = state.PipelineSettingProperties.GetOrAdd(
                pipelineType,
                static type => type
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.CanRead
                        && property.CanWrite
                        && property.GetIndexParameters().Length == 0
                        && property.GetCustomAttribute<RenderPipelineCameraSettingAttribute>(inherit: true) is not null)
                    .OrderBy(property => property.GetCustomAttribute<RenderPipelineCameraSettingAttribute>(inherit: true)?.Order ?? 0)
                    .ThenBy(property => property.GetCustomAttribute<CategoryAttribute>(inherit: true)?.Category ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(GetPipelineSettingDisplayName, StringComparer.Ordinal)
                    .ToArray());

            if (state.Generation == EditorTypeCatalogGeneration.Current)
                return properties;
        }

        return [];
    }

    private static void DrawPipelineCameraSettingProperty(RenderPipeline pipeline, PropertyInfo property)
    {
        Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        string displayName = GetPipelineSettingDisplayName(property);

        ImGui.PushID(property.Name);
        try
        {
            if (propertyType == typeof(bool))
            {
                bool value = property.GetValue(pipeline) is bool current && current;
                if (ImGui.Checkbox(displayName, ref value))
                {
                    using var _ = Undo.TrackChange(displayName, pipeline);
                    property.SetValue(pipeline, value);
                }
                DrawPipelineSettingTooltip(property);
            }
            else if (propertyType.IsEnum)
            {
                DrawPipelineEnumSetting(pipeline, property, propertyType, displayName);
            }
            else
            {
                ImGui.TextDisabled($"{displayName}: unsupported {propertyType.Name}");
                DrawPipelineSettingTooltip(property);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private static void DrawPipelineEnumSetting(RenderPipeline pipeline, PropertyInfo property, Type enumType, string displayName)
    {
        object? current = property.GetValue(pipeline);
        Array values = Enum.GetValues(enumType);
        string currentLabel = current is not null ? GetEnumDisplayName(enumType, current) : "<unset>";

        CameraSettingLabel(displayName);
        if (ImGui.BeginCombo("##Value", currentLabel))
        {
            foreach (object option in values)
            {
                bool selected = Equals(current, option);
                string optionLabel = GetEnumDisplayName(enumType, option);
                if (ImGui.Selectable(optionLabel, selected) && !selected)
                {
                    using var _ = Undo.TrackChange(displayName, pipeline);
                    property.SetValue(pipeline, option);
                    current = option;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        DrawPipelineSettingTooltip(property);
    }

    private static void DrawPipelineSettingTooltip(PropertyInfo property)
    {
        string? description = property.GetCustomAttribute<DescriptionAttribute>(inherit: true)?.Description;
        if (!string.IsNullOrWhiteSpace(description) && ImGui.IsItemHovered())
            ImGui.SetTooltip(description);
    }

    private static string GetPipelineSettingDisplayName(PropertyInfo property)
        => property.GetCustomAttribute<DisplayNameAttribute>(inherit: true)?.DisplayName ?? property.Name;

    private static string GetEnumDisplayName(Type enumType, object value)
    {
        string? name = Enum.GetName(enumType, value);
        if (string.IsNullOrWhiteSpace(name))
            return value.ToString() ?? string.Empty;

        FieldInfo? field = enumType.GetField(name);
        return field?.GetCustomAttribute<DisplayNameAttribute>(inherit: false)?.DisplayName
            ?? field?.GetCustomAttribute<DescriptionAttribute>(inherit: false)?.Description
            ?? name;
    }

    private static readonly Vector4 WarningTextColor = new(1.0f, 0.7f, 0.2f, 1.0f);


    internal static void DrawRuntimeCameraProjection(XRCamera camera, HashSet<object> visited)
    {
        using var profilerScope = Engine.Profiler.Start("UI.ComponentEditor.CameraComponent.Projection");

        if (!ImGui.CollapsingHeader("Projection"))
            return;

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
        ParameterOption[] parameterOptions = GetParameterOptions(out long optionsGeneration);
        int currentIndex = Array.FindIndex(parameterOptions, option => option.ParameterType == current.GetType());
        
        // Handle unknown types that aren't in the discovered list
        string currentLabel = currentIndex >= 0 
            ? parameterOptions[currentIndex].Label
            : new ParameterOption(current.GetType()).Label;

        // Show category/description for current type
        var currentOption = currentIndex >= 0 ? parameterOptions[currentIndex] : new ParameterOption(current.GetType());
        
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
            
            for (int i = 0; i < parameterOptions.Length; i++)
            {
                var option = parameterOptions[i];
                
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
                if (ImGui.Selectable(option.Label, selected)
                    && !selected
                    && !isTransitioning
                    && optionsGeneration == EditorTypeCatalogGeneration.Current)
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

    private static readonly ConditionalWeakTable<XRCamera, CameraPostProcessEditorState> PostProcessEditorStates = new();

    private sealed class CameraPostProcessEditorState
    {
        public string? SelectedPipelineId { get; set; }
        public RenderPipeline? AssetTarget { get; private set; }
        public Action<RenderPipeline?> SelectAsset { get; }

        public CameraPostProcessEditorState()
            => SelectAsset = SetAssetTarget;

        private void SetAssetTarget(RenderPipeline? pipeline)
        {
            AssetTarget = pipeline;
            SelectedPipelineId = pipeline?.ID.ToString();
        }
    }

    private static List<(string Label, RenderPipeline Pipeline)> GetCandidatePostProcessingPipelines(XRCamera camera, RenderPipeline activePipeline)
    {
        const int maxGenerationAttempts = 2;
        for (int attempt = 0; attempt < maxGenerationAttempts; attempt++)
        {
            List<(string Label, RenderPipeline Pipeline)> list = GetKnownPostProcessingPipelines(
                camera,
                activePipeline,
                out HashSet<Guid> seen);

            // 5. Discover loadable RenderPipeline types from one exact catalog generation.
            IReadOnlyList<Type> pipelineTypes = GetAvailableRenderPipelineTypes(out long generation);
            foreach (Type pipelineType in pipelineTypes)
            {
                if (list.Any(p => p.Pipeline.GetType() == pipelineType))
                    continue;

                if (TryGetOrCreatePipelineTypeFallback(pipelineType, generation, out RenderPipeline? pipelineInstance)
                    && seen.Add(pipelineInstance.ID))
                {
                    list.Add(($"Type Preview ({pipelineInstance.DebugName}, read-only)", pipelineInstance));
                }
            }

            if (generation == EditorTypeCatalogGeneration.Current)
                return list;
        }

        // Continuous type-catalog churn must not spin the ImGui owner. Keep the
        // already-owned pipelines visible and retry discovered fallbacks next frame.
        return GetKnownPostProcessingPipelines(camera, activePipeline, out _);
    }

    private static List<(string Label, RenderPipeline Pipeline)> GetKnownPostProcessingPipelines(
        XRCamera camera,
        RenderPipeline activePipeline,
        out HashSet<Guid> seen)
    {
        List<(string Label, RenderPipeline Pipeline)> list = [];
        seen = [];

        list.Add(($"Active Viewport ({activePipeline.DebugName})", activePipeline));
        seen.Add(activePipeline.ID);

        if (PostProcessEditorStates.GetOrCreateValue(camera).AssetTarget is { } assetTarget && seen.Add(assetTarget.ID))
            list.Add(($"Selected Asset ({assetTarget.DebugName})", assetTarget));

        if (camera.RenderPipeline is { } camPipeline && seen.Add(camPipeline.ID))
            list.Add(($"Camera Assigned ({camPipeline.DebugName})", camPipeline));

        foreach (var viewport in camera.Viewports)
        {
            if (viewport.RenderPipeline is { } cvpPipeline && seen.Add(cvpPipeline.ID))
                list.Add(($"Bound Viewport ({cvpPipeline.DebugName})", cvpPipeline));
        }

        foreach (var viewport in RuntimeEngine.EnumerateActiveViewports())
        {
            if (viewport.RenderPipeline is { } vpPipeline && seen.Add(vpPipeline.ID))
                list.Add(($"Viewport #{viewport.FrameOutputIdentity} ({vpPipeline.DebugName})", vpPipeline));
        }

        return list;
    }

    private static IReadOnlyList<Type> GetAvailableRenderPipelineTypes(out long generation)
    {
        const int maxGenerationAttempts = 2;
        for (int attempt = 0; attempt < maxGenerationAttempts; attempt++)
        {
            generation = EditorTypeCatalogGeneration.Current;
            Type[] types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(EditorTypeCatalogGeneration.IsAssemblyDiscoverable)
                .SelectMany(static assembly => XREngine.Core.XRLoadableTypeCatalog.GetTypes(assembly))
                .Where(t => t.IsClass && !t.IsAbstract && typeof(RenderPipeline).IsAssignableFrom(t))
                .ToArray();
            if (generation == EditorTypeCatalogGeneration.Current)
                return types;
        }

        generation = EditorTypeCatalogGeneration.Current;
        return [];
    }

    private static bool TryGetOrCreatePipelineTypeFallback(
        Type pipelineType,
        long expectedGeneration,
        [NotNullWhen(true)] out RenderPipeline? pipeline)
    {
        if (!EditorTypeCatalogGeneration.IsTypeDiscoverable(pipelineType))
        {
            pipeline = null;
            return false;
        }

        CameraMetadataCacheState state = GetCurrentMetadataCacheState();
        if (state.Generation != expectedGeneration
            || expectedGeneration != EditorTypeCatalogGeneration.Current)
        {
            pipeline = null;
            return false;
        }

        if (state.FallbackPipelines.TryGetValue(pipelineType, out pipeline))
        {
            EditorFallbackPipelineStates.GetValue(pipeline, static _ => new EditorFallbackPipelineState());
            return expectedGeneration == EditorTypeCatalogGeneration.Current;
        }

        RenderPipeline? created;
        try
        {
            created = Activator.CreateInstance(pipelineType) as RenderPipeline;
        }
        catch
        {
            try
            {
                created = Activator.CreateInstance(pipelineType, false) as RenderPipeline;
            }
            catch
            {
                pipeline = null;
                return false;
            }
        }

        if (created is null)
        {
            pipeline = null;
            return false;
        }

        if (state.Generation != expectedGeneration
            || expectedGeneration != EditorTypeCatalogGeneration.Current)
        {
            created.Destroy();
            pipeline = null;
            return false;
        }

        pipeline = state.FallbackPipelines.GetOrAdd(pipelineType, created);
        if (!ReferenceEquals(pipeline, created))
            created.Destroy();
        EditorFallbackPipelineStates.GetValue(pipeline, static _ => new EditorFallbackPipelineState());

        if (state.Generation == expectedGeneration
            && expectedGeneration == EditorTypeCatalogGeneration.Current)
        {
            return true;
        }

        pipeline = null;
        return false;
    }

    private static bool IsPipelineTypePreview(RenderPipeline pipeline)
        => EditorFallbackPipelineStates.TryGetValue(pipeline, out _);

    private static PipelinePostProcessState? GetEditorPostProcessState(
        XRCamera camera,
        RenderPipeline pipeline)
        => EditorFallbackPipelineStates.TryGetValue(pipeline, out EditorFallbackPipelineState? fallbackState)
            ? fallbackState.GetOrCreate(camera, pipeline)
            : camera.GetPostProcessState(pipeline);

    private static void DrawRuntimeCameraPostProcessing(PipelineEditorContext context)
    {
        using var profilerScope = Engine.Profiler.Start("UI.ComponentEditor.CameraComponent.PostProcessing");

        XRCamera camera = context.Camera;
        CameraComponent? component = context.Component;
        RenderPipeline selectedPipeline = context.SelectedPipeline;
        ImGui.PushID("PostProcessingPanel");
        var schema = context.Schema;
        var state = context.State;

        ImGui.BeginDisabled(IsPipelineTypePreview(selectedPipeline));
        selectedPipeline.EditorUIProvider?.DrawPostProcessingHeader(context);
        if (schema.IsEmpty || state is null)
        {
            ImGui.Separator();
            ImGui.TextDisabled($"Pipeline '{selectedPipeline.DebugName}' exposes no post-processing schema.");
        }
        else
        {

            ImGui.Separator();
            DrawSchemaStageSelector(schema, state, camera, component, context.IsActivePipeline);
            ImGui.Spacing();
        }

        selectedPipeline.EditorUIProvider?.DrawPostProcessingFooter(context);
        ImGui.EndDisabled();
        ImGui.PopID();
    }

    private static RenderPipeline DrawCameraPipelineSelector(XRCamera camera, RenderPipeline activePipeline)
    {
        List<(string Label, RenderPipeline Pipeline)> candidatePipelines = GetCandidatePostProcessingPipelines(camera, activePipeline);

        var editorState = PostProcessEditorStates.GetOrCreateValue(camera);
        RenderPipeline selectedPipeline = activePipeline;
        int selectedIndex = 0;

        if (editorState.SelectedPipelineId is not null)
        {
            int idx = candidatePipelines.FindIndex(p => p.Pipeline.ID.ToString() == editorState.SelectedPipelineId || p.Pipeline.DebugName == editorState.SelectedPipelineId);
            if (idx >= 0)
            {
                selectedPipeline = candidatePipelines[idx].Pipeline;
                selectedIndex = idx;
            }
        }

        CameraSettingLabel("Target Pipeline");
        if (ImGui.BeginCombo("##TargetPipeline", candidatePipelines[selectedIndex].Label))
        {
            for (int i = 0; i < candidatePipelines.Count; i++)
            {
                bool isSelected = i == selectedIndex;
                if (ImGui.Selectable(candidatePipelines[i].Label, isSelected))
                {
                    selectedIndex = i;
                    selectedPipeline = candidatePipelines[i].Pipeline;
                    editorState.SelectedPipelineId = selectedPipeline.ID.ToString();
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.TextWrapped(ReferenceEquals(selectedPipeline, activePipeline)
            ? "Editing the active pipeline."
            : "Editing a separate pipeline; the camera's rendering pipeline is unchanged.");
        if (IsPipelineTypePreview(selectedPipeline))
            ImGui.TextWrapped("Read-only type preview. Select a saved pipeline asset below to edit it.");
        else if (!ReferenceEquals(selectedPipeline, activePipeline))
            ImGui.TextWrapped("Live applicability checks are applied only when this camera uses the target pipeline.");
        if (ImGui.TreeNode("Edit Another Pipeline Asset"))
        {
            ImGui.TextWrapped("Choose an existing asset without assigning it to this camera. Pipeline properties affect all views using that asset; effect settings remain on this camera for that asset.");
            ImGuiAssetUtilities.DrawAssetField<RenderPipeline>("EditingPipelineAsset", editorState.AssetTarget,
                editorState.SelectAsset, allowCreateOrReplace: false, allowInlineInspector: false);
            ImGui.TreePop();
        }
        return selectedPipeline;
    }

    private static RenderPipeline ResolveCameraEditorPipeline(XRCamera camera)
        => camera.Viewports
            .Select(viewport => viewport.RenderPipeline)
            .FirstOrDefault(pipeline => pipeline is not null)
            ?? camera.RenderPipeline;

    private static void DrawSchemaStageSelector(RenderPipelinePostProcessSchema schema, PipelinePostProcessState state, XRCamera camera, CameraComponent? component, bool evaluateLiveState)
    {
        List<PostProcessStageEntry> stages = BuildOrderedStageEntries(schema, state);
        if (stages.Count == 0)
        {
            ImGui.TextDisabled("No schema-backed post-processing stages are available for this camera.");
            return;
        }

        var selectorState = PostProcessStageSelectorStates.GetValue(state, _ => new PostProcessStageSelectorState());
        int selectedIndex = FindSelectedStageIndex(stages, selectorState.SelectedStageKey);
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
            selectorState.SelectedStageKey = stages[0].Descriptor.Key;
        }

        ImGui.SetNextItemWidth(-1.0f);
        if (ImGui.BeginCombo("Effect", GetStageSelectorLabel(stages[selectedIndex])))
        {
            for (int index = 0; index < stages.Count; index++)
            {
                var stage = stages[index];
                bool selected = index == selectedIndex;
                string label = $"{GetStageSelectorLabel(stage)}##{stage.Descriptor.Key}";

                if (ImGui.Selectable(label, selected) && !selected)
                {
                    selectorState.SelectedStageKey = stage.Descriptor.Key;
                    selectedIndex = index;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
        DrawSchemaStageSelection(stages[selectedIndex], camera, component, evaluateLiveState: evaluateLiveState);
    }

    private static int FindSelectedStageIndex(List<PostProcessStageEntry> stages, string selectedStageKey)
    {
        if (string.IsNullOrWhiteSpace(selectedStageKey))
            return -1;

        for (int index = 0; index < stages.Count; index++)
        {
            if (stages[index].Descriptor.Key.Equals(selectedStageKey, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private static string GetStageSelectorLabel(PostProcessStageEntry stage)
        => stage.Category is { } category
            ? $"{category.DisplayName} / {stage.Descriptor.DisplayName}"
            : stage.Descriptor.DisplayName;

    private static List<PostProcessStageEntry> BuildOrderedStageEntries(RenderPipelinePostProcessSchema schema, PipelinePostProcessState state, PipelineEditorSection section = PipelineEditorSection.PostProcessing)
    {
        List<PostProcessStageEntry> stages = new(schema.StagesByKey.Count);
        HashSet<string> addedStageKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (var category in schema.Categories)
        {
            foreach (var stageKey in category.StageKeys)
            {

                if (!TryCreateStageEntry(schema, state, stageKey, category, out PostProcessStageEntry stage))
                    continue;

                if (!StageHasEditorSection(stage.Descriptor, section) || !addedStageKeys.Add(stage.Descriptor.Key))
                    continue;

                stages.Add(stage);
            }
        }

        foreach (var stage in schema.StagesByKey.Values)
        {
            if (!StageHasEditorSection(stage, section))
                continue;

            if (!addedStageKeys.Add(stage.Key))
                continue;

            if (!state.TryGetStage(stage.Key, out PostProcessStageState? stageState) || stageState is null)
                continue;

            stages.Add(new PostProcessStageEntry(null, stage, stageState));
        }

        return stages;
    }

    private static bool StageHasEditorSection(PostProcessStageDescriptor stage, PipelineEditorSection section)
    {
        if (stage.EditorSection == section && PostProcessCustomDrawerRegistry.GetDrawer(stage) is not null)
            return true;
        foreach (var parameter in stage.Parameters)
            if ((parameter.EditorSection ?? stage.EditorSection) == section)
                return true;
        return false;
    }

    private static bool TryCreateStageEntry(RenderPipelinePostProcessSchema schema, PipelinePostProcessState state, string stageKey, PostProcessCategoryDescriptor? category, out PostProcessStageEntry stageEntry)
    {
        if (!schema.TryGetStage(stageKey, out PostProcessStageDescriptor? stage) ||
            stage is null ||
            !state.TryGetStage(stageKey, out PostProcessStageState? stageState) ||
            stageState is null)
        {
            stageEntry = default;
            return false;
        }

        stageEntry = new PostProcessStageEntry(category, stage, stageState);
        return true;
    }

    private static void DrawSchemaStageSelection(PostProcessStageEntry stage, XRCamera camera, CameraComponent? component, PipelineEditorSection section = PipelineEditorSection.PostProcessing, bool evaluateLiveState = true)
    {
        var (stageDisabled, disableReason) = evaluateLiveState ? stage.Descriptor.EvaluateState(camera) : (false, null);

        ImGui.PushID(stage.Descriptor.Key);

        if (stage.Category is { } category)
            ImGui.TextDisabled($"Category: {category.DisplayName}");

        XRBase? undoTarget = (stage.State.BackingInstance as XRBase) ?? component;
        if (ImGui.SmallButton("Reset To Defaults"))
            ResetSchemaStageToDefaults(stage.Descriptor, stage.State, undoTarget, section);

        if (!string.IsNullOrWhiteSpace(stage.Category?.Description))
            ImGui.TextDisabled(stage.Category.Description);

        if (stageDisabled && !string.IsNullOrWhiteSpace(disableReason))
            ImGui.TextColored(WarningTextColor, disableReason);

        IPostProcessStageCustomDrawer? customDrawer = PostProcessCustomDrawerRegistry.GetDrawer(stage.Descriptor);

        using (new ImGuiDisabledScope(stageDisabled))
            DrawSchemaStage(stage.Descriptor, stage.State, undoTarget, component, customDrawer, drawStageHeader: false, camera: camera, section: section);

        ImGui.PopID();
    }

    private static void ResetSchemaStageToDefaults(PostProcessStageDescriptor stage, PostProcessStageState stageState, XRBase? undoTarget, PipelineEditorSection section)
    {
        using var _ = Undo.TrackChange($"Reset {stage.DisplayName}", undoTarget);

        foreach (var param in stage.Parameters)
            if ((param.EditorSection ?? stage.EditorSection) == section)
                ResetSchemaParameterToDefault(param, stageState);
    }

    private static void ResetSchemaParameterToDefault(PostProcessParameterDescriptor param, PostProcessStageState stageState)
    {
        switch (param.Kind)
        {
            case PostProcessParameterKind.Bool:
                stageState.SetValue(param.Name, ExtractDefault(param, false));
                break;
            case PostProcessParameterKind.Int:
                stageState.SetValue(param.Name, ExtractDefault(param, 0));
                break;
            case PostProcessParameterKind.Float:
                stageState.SetValue(param.Name, ExtractDefault(param, 0.0f));
                break;
            case PostProcessParameterKind.Vector2:
                stageState.SetValue(param.Name, ExtractDefault(param, Vector2.Zero));
                break;
            case PostProcessParameterKind.Vector3:
                stageState.SetValue(param.Name, ExtractDefault(param, Vector3.Zero));
                break;
            case PostProcessParameterKind.Vector4:
                stageState.SetValue(param.Name, ExtractDefault(param, Vector4.Zero));
                break;
        }
    }

    private static void DrawSchemaStage(
        PostProcessStageDescriptor stage,
        PostProcessStageState stageState,
        XRBase? undoTarget,
        CameraComponent? component,
        IPostProcessStageCustomDrawer? customDrawer,
        bool drawStageHeader = true,
        XRCamera? camera = null,
        PipelineEditorSection section = PipelineEditorSection.PostProcessing)
    {
        if (stage.Parameters.Count == 0 && customDrawer is null)
            return;

        ImGui.PushID(stage.Key);

        if (drawStageHeader)
            ImGui.SeparatorText(stage.DisplayName);

        undoTarget = (stageState.BackingInstance as XRBase) ?? undoTarget;

        PostProcessStageCustomDrawerContext? stageContext = null;
        if (stage.EditorSection == section && customDrawer is not null && camera is not null)
        {
            stageContext = new PostProcessStageCustomDrawerContext(camera, component, stage, stageState, undoTarget);
            customDrawer.DrawStageHeader(stageContext);
        }

        foreach (var param in stage.Parameters)
            if ((param.EditorSection ?? stage.EditorSection) == section)
                DrawSchemaParameter(param, stage, stageState, undoTarget, camera, component, customDrawer);

        if (stageContext is not null)
        {
            customDrawer?.DrawStageFooter(stageContext);
        }

        ImGui.PopID();
    }

    private static void DrawSchemaParameter(
        PostProcessParameterDescriptor param,
        PostProcessStageDescriptor stage,
        PostProcessStageState stageState,
        XRBase? undoTarget,
        XRCamera? camera,
        CameraComponent? component,
        IPostProcessStageCustomDrawer? customDrawer)
    {
        if (param.VisibilityCondition != null && stageState.BackingInstance != null)
        {
            if (!param.VisibilityCondition(stageState.BackingInstance))
                return;
        }

        ImGui.PushID(param.Name);

        if (customDrawer is not null && camera is not null)
        {
            var paramContext = new PostProcessParameterCustomDrawerContext(
                camera,
                component,
                stage,
                param,
                stageState,
                undoTarget);

            if (customDrawer.TryDrawParameter(paramContext))
            {
                ImGui.PopID();
                return;
            }
        }

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

        bool changed = param.IsColor
            ? ImGui.ColorEdit3(param.DisplayName, ref value)
            : ImGui.DragFloat3(param.DisplayName, ref value, param.Step ?? 0.01f);

        if (changed)
            state.SetValue(param.Name, value);
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
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
        public int SelectedPipelineIndex = 0;
        public string? SelectedFboName = null;
        public bool FlipPreview = true;
    }
}

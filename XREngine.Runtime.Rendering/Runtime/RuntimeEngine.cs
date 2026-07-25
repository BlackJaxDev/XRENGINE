using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Data.Core;
using XREngine.Scene.Physics;

namespace XREngine;

public static partial class RuntimeEngine
{
    private static readonly EventList<XRWindow> ActiveWindows = [];
    private static int _renderThreadId;
    private static int _windowThreadId;

    public enum EViewportEnumerationMode
    {
        ExcludeVrEyeViewports,
        IncludeVrEyeViewports,
    }

    public static float Delta => Time.Timer.Update.Delta;
    public static float SmoothedDelta => (float)RuntimeRenderingHostServices.FrameTiming.SmoothedUpdateDeltaSeconds;
    public static long ElapsedTicks => RuntimeRenderingHostServices.FrameTiming.ElapsedTicks;
    public static float ElapsedTime => RuntimeRenderingHostServices.FrameTiming.ElapsedTime;
    public static bool IsEditor => false;
    public static bool IsRenderThread
        => Environment.CurrentManagedThreadId == RenderThreadId;
    public static int RenderThreadId
        => Volatile.Read(ref _renderThreadId);
    public static bool IsWindowThread
        => Environment.CurrentManagedThreadId == WindowThreadId;
    public static int WindowThreadId
        => Volatile.Read(ref _windowThreadId);
    public static bool StartingUp => RuntimeRenderingHostServices.FrameTiming.IsStartingUp;
    public static bool IsDispatchingRenderFrame { get; set; }
    public static bool StartupPresentationEnabled { get; set; }
    public static ColorF4 StartupPresentationClearColor { get; set; } = ColorF4.Black;

    internal static RuntimeTime Time { get; } = new();
    internal static RuntimePlayMode PlayMode { get; } = new();
    internal static RuntimeGameSettings GameSettings { get; } = new();
    internal static RuntimeEditorPreferences EditorPreferences { get; } = new();
    internal static RuntimeEffectiveSettings EffectiveSettings { get; } = new();
    public static UserSettings UserSettings { get; } = new();
    internal static RuntimeAssetFacade Assets { get; } = new();
    internal static RuntimeProfilerFacade Profiler { get; } = new();
    public static JobManager Jobs { get; } = new();
    public static RuntimeVrState VRState { get; } = new();
    /// <summary>
    /// Active render windows. Runtime.Rendering owns this registry; the application host owns
    /// native creation and destruction and registers each window at the lifecycle boundary.
    /// </summary>
    public static IEventListReadOnly<XRWindow> Windows => ActiveWindows;

    public static void RegisterWindow(XRWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!ActiveWindows.Contains(window))
            ActiveWindows.Add(window);
    }

    public static bool UnregisterWindow(XRWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return ActiveWindows.Remove(window);
    }

    /// <summary>
    /// Serializes the window's target-world hierarchy for application-owned state replication.
    /// </summary>
    public static string? EncodeWindowTargetWorldHierarchyJson(XRWindow window)
        => window.EncodeTargetWorldHierarchyJson();

    public static void AssignRenderThread(int threadId)
        => Volatile.Write(ref _renderThreadId, threadId);

    public static void AssignWindowThread(int threadId)
        => Volatile.Write(ref _windowThreadId, threadId);

    public static ActiveViewportEnumerable EnumerateActiveViewports(
        EViewportEnumerationMode mode = EViewportEnumerationMode.ExcludeVrEyeViewports)
        => new(ActiveWindows, mode);

    public static ActiveViewportEnumerable EnumerateActiveViewports(
        XRWindow? window,
        EViewportEnumerationMode mode = EViewportEnumerationMode.ExcludeVrEyeViewports)
        => new(window, mode);

    public static IReadOnlyList<XRViewport> EnumerateActiveViewportsOnRenderThread(
        EViewportEnumerationMode mode = EViewportEnumerationMode.ExcludeVrEyeViewports)
    {
        if (IsRenderThread)
            return [.. EnumerateActiveViewports(mode)];

        var completion = new TaskCompletionSource<IReadOnlyList<XRViewport>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueRenderThreadTask(() =>
        {
            try
            {
                completion.TrySetResult([.. EnumerateActiveViewports(mode)]);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, "RuntimeEngine.EnumerateActiveViewportsOnRenderThread");

        return completion.Task.GetAwaiter().GetResult();
    }

    public static ActiveWindowViewportEnumerable EnumerateActiveWindowViewports(
        EViewportEnumerationMode mode = EViewportEnumerationMode.ExcludeVrEyeViewports)
        => new(ActiveWindows, mode);

    public static void ProcessMainThreadTasks()
        => RuntimeRenderingHostServices.Scheduling.ProcessRenderThreadTasks();

    public static void EnqueueMainThreadTask(
        Action action,
        string? name = null,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
        => RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(
            action,
            name ?? "main-thread facade task",
            renderThreadKind);

    public static void EnqueueMainThreadTask(Action action, RenderThreadJobKind renderThreadKind)
        => EnqueueMainThreadTask(action, name: null, renderThreadKind);

    public static void EnqueueRenderThreadTask(
        Action action,
        string? name = null,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
        => RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(
            action,
            name ?? "render-thread facade task",
            renderThreadKind);

    public static void EnqueueRenderThreadTask(Action action, RenderThreadJobKind renderThreadKind)
        => EnqueueRenderThreadTask(action, name: null, renderThreadKind);

    public static void EnqueueAppThreadTask(Action action, string? name = null)
        => RuntimeRenderingHostServices.Scheduling.EnqueueAppThreadTask(action, name ?? "app-thread facade task");

    public static bool InvokeOnAppThread(
        Action action,
        string? name = null,
        bool executeNowIfAlreadyAppThread = false)
    {
        if (executeNowIfAlreadyAppThread && RuntimeRenderingHostServices.FrameTiming.IsAppThread)
        {
            action();
            return false;
        }

        EnqueueAppThreadTask(action, name);
        return true;
    }

    public static bool InvokeOnMainThread(Action action, string? name = null, bool forceSynchronous = false, bool executeNowIfAlreadyMainThread = false)
    {
        if (IsRenderThread)
        {
            if (executeNowIfAlreadyMainThread)
                action();
            return false;
        }

        if (forceSynchronous)
        {
            action();
            return false;
        }

        EnqueueMainThreadTask(action, name);
        return true;
    }

    public static void AddMainThreadCoroutine(
        Func<bool> step,
        string? name = null,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
        => RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(
            step,
            name ?? "main-thread facade coroutine",
            renderThreadKind);

    public static void AddMainThreadCoroutine(Func<bool> step, RenderThreadJobKind renderThreadKind)
        => AddMainThreadCoroutine(step, name: null, renderThreadKind);

    public static void AddRenderThreadCoroutine(
        Func<bool> step,
        string? name = null,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
        => RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(
            step,
            name ?? "render-thread facade coroutine",
            renderThreadKind);

    public static void AddRenderThreadCoroutine(Func<bool> step, RenderThreadJobKind renderThreadKind)
        => AddRenderThreadCoroutine(step, name: null, renderThreadKind);

    public static string GetStackTrace() => Environment.StackTrace;
    public static void LogWarning(string message, EOutputVerbosity verbosity = EOutputVerbosity.Normal, ELogCategory category = ELogCategory.General)
        => Debug.Out(message);

    internal static RuntimeEngineState State { get; } = new();

    public static partial class Rendering
    {
        [ThreadStatic]
        private static Stack<XRRenderPipelineInstance>? t_pipelineStack;
        [ThreadStatic]
        private static Stack<XRRenderPipelineInstance?>? t_pipelineOverrideStack;

        private static Stack<XRRenderPipelineInstance> PipelineStack => t_pipelineStack ??= new();
        private static Stack<XRRenderPipelineInstance?> PipelineOverrideStack => t_pipelineOverrideStack ??= new();
        private static readonly Action<object?> PopRenderingPipelineAction = static _ =>
        {
            if (PipelineStack.Count != 0)
                PipelineStack.Pop();
        };

        private static EngineSettings _settings = new();
        private static EngineSettings _globalDefaultSettings = _settings;
        private static EngineSettings? _projectDefaultSettings;
        private static RuntimeRenderingState StateData { get; } = new();
        public static RuntimeBvhStats BvhStats { get; } = new();
        public static event Action? SettingsChanged;
        public static event Action<string?>? SettingChanged;
        public static event Action? AntiAliasingSettingsChanged;

        static Rendering()
            => AttachSettings(_settings);

        /// <summary>
        /// Begins a render frame and returns its monotonically increasing identifier.
        /// </summary>
        public static ulong BeginRenderFrame()
        {
            StateData.BeginRenderFrame();
            return StateData.RenderFrameId;
        }

        /// <summary>
        /// Records the completed render-frame duration and publishes its output snapshot.
        /// </summary>
        public static void CompleteRenderFrame(ulong renderFrameId, long elapsedTicks)
        {
            Stats.FrameOutputs.RecordWholeFrameRenderThread(renderFrameId, elapsedTicks);
            Stats.FrameOutputs.SnapshotAndReset();
        }

        /// <summary>
        /// Active rendering defaults. Runtime.Rendering owns this serialized asset and its
        /// change notifications; application composition owns the resulting side effects.
        /// </summary>
        public static EngineSettings Settings
        {
            get => _settings;
            set
            {
                EngineSettings next = value ?? new EngineSettings();
                if (ReferenceEquals(_settings, next))
                    return;

                DetachSettings(_settings);
                _settings = next;
                AttachSettings(_settings);

                if (_projectDefaultSettings is not null)
                    _projectDefaultSettings = _settings;
                else
                    _globalDefaultSettings = _settings;

                NotifySettingsChanged(null);
            }
        }

        public static EngineSettings GlobalDefaultSettings
        {
            get => _globalDefaultSettings;
            set
            {
                EngineSettings next = value ?? new EngineSettings();
                if (ReferenceEquals(_globalDefaultSettings, next))
                    return;

                _globalDefaultSettings = next;
                if (_projectDefaultSettings is null)
                    Settings = _globalDefaultSettings;
            }
        }

        public static EngineSettings? ProjectDefaultSettings
        {
            get => _projectDefaultSettings;
            set
            {
                if (ReferenceEquals(_projectDefaultSettings, value))
                    return;

                _projectDefaultSettings = value;
                Settings = _projectDefaultSettings ?? _globalDefaultSettings;
            }
        }

        public static EngineSettings DefaultSettings
        {
            get => Settings;
            set => Settings = value;
        }

        public static void NotifyAntiAliasingSettingChanged()
            => AntiAliasingSettingsChanged?.Invoke();

        private static void AttachSettings(EngineSettings settings)
        {
            settings.PropertyChanged += HandleSettingsPropertyChanged;
            settings.PhysicsVisualizeSettings.PropertyChanged += HandlePhysicsVisualizeSettingsChanged;
            settings.PhysicsGpuMemorySettings.PropertyChanged += HandlePhysicsGpuMemorySettingsChanged;
        }

        private static void DetachSettings(EngineSettings settings)
        {
            settings.PropertyChanged -= HandleSettingsPropertyChanged;
            settings.PhysicsVisualizeSettings.PropertyChanged -= HandlePhysicsVisualizeSettingsChanged;
            settings.PhysicsGpuMemorySettings.PropertyChanged -= HandlePhysicsGpuMemorySettingsChanged;
        }

        private static void HandleSettingsPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EngineSettings.PhysicsVisualizeSettings))
            {
                if (e.PreviousValue is PhysicsVisualizeSettings previous)
                    previous.PropertyChanged -= HandlePhysicsVisualizeSettingsChanged;
                if (e.NewValue is PhysicsVisualizeSettings current)
                    current.PropertyChanged += HandlePhysicsVisualizeSettingsChanged;
            }

            if (e.PropertyName == nameof(EngineSettings.PhysicsGpuMemorySettings))
            {
                if (e.PreviousValue is PhysicsGpuMemorySettings previous)
                    previous.PropertyChanged -= HandlePhysicsGpuMemorySettingsChanged;
                if (e.NewValue is PhysicsGpuMemorySettings current)
                    current.PropertyChanged += HandlePhysicsGpuMemorySettingsChanged;
            }

            NotifySettingsChanged(e.PropertyName);
        }

        private static void HandlePhysicsVisualizeSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            // Physics scenes subscribe directly to this asset. Do not rebuild render resources.
        }

        private static void HandlePhysicsGpuMemorySettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
            => NotifySettingsChanged(nameof(EngineSettings.PhysicsGpuMemorySettings));

        private static void NotifySettingsChanged(string? propertyName)
        {
            SettingChanged?.Invoke(propertyName);
            SettingsChanged?.Invoke();
        }

        public static bool VulkanUpscaleBridgeDx12InteropEnabled => false;
        public static bool VulkanUpscaleBridgeImplicitModeEnabled => false;

        public static class Constants
        {
            public const string ShadowExponentBaseUniform = "ShadowBase";
            public const string ShadowExponentUniform = "ShadowMult";
            public const string ShadowBiasMinUniform = "ShadowBiasMin";
            public const string ShadowBiasMaxUniform = "ShadowBiasMax";
            public const string BoneTransformsName = "Transforms";
            public const string MorphWeightsName = "MorphWeights";
            public const string LightsStructName = "LightData";
            public const string EngineFontsCommonFolderName = "Fonts";
            public const string ShadowSamples = "ShadowSamples";
            public const string ShadowBlockerSamples = "ShadowBlockerSamples";
            public const string ShadowFilterSamples = "ShadowFilterSamples";
            public const string ShadowVogelTapCount = "ShadowVogelTapCount";
            public const string ShadowFilterRadius = "ShadowFilterRadius";
            public const string ShadowBlockerSearchRadius = "ShadowBlockerSearchRadius";
            public const string ShadowMinPenumbra = "ShadowMinPenumbra";
            public const string ShadowMaxPenumbra = "ShadowMaxPenumbra";
            public const string SoftShadowMode = "SoftShadowMode";
            public const string LightSourceRadius = "LightSourceRadius";
            public const string EnableCascadedShadows = "EnableCascadedShadows";
            public const string EnableContactShadows = "EnableContactShadows";
            public const string ContactShadowDistance = "ContactShadowDistance";
            public const string ContactShadowSamples = "ContactShadowSamples";
            public const string ContactShadowThickness = "ContactShadowThickness";
            public const string ContactShadowFadeStart = "ContactShadowFadeStart";
            public const string ContactShadowFadeEnd = "ContactShadowFadeEnd";
            public const string ContactShadowNormalOffset = "ContactShadowNormalOffset";
            public const string ContactShadowJitterStrength = "ContactShadowJitterStrength";
        }

        public static RenderPipeline NewRenderPipeline(bool stereo = false)
            => new DefaultRenderPipeline(stereo);

        public static VisualScene3D NewVisualScene()
            => RuntimeRenderingHostServices.Factories.CreateVisualScene();

        public static AbstractPhysicsScene NewPhysicsScene()
            => RuntimeRenderingHostServices.Factories.CreatePhysicsScene();

        public static IDisposable? PushRenderingPipeline(XRRenderPipelineInstance pipeline)
        {
            PipelineStack.Push(pipeline);
            return StateObject.New(PopRenderingPipelineAction, null);
        }

        public readonly struct RenderingPipelineOverrideScope : IDisposable
        {
            private readonly bool _active;

            internal RenderingPipelineOverrideScope(XRRenderPipelineInstance? pipeline)
            {
                PipelineOverrideStack.Push(pipeline);
                _active = true;
            }

            public void Dispose()
            {
                if (_active && PipelineOverrideStack.Count != 0)
                    PipelineOverrideStack.Pop();
            }
        }

        public static RenderingPipelineOverrideScope PushRenderingPipelineOverride(XRRenderPipelineInstance? pipeline)
        {
            return new RenderingPipelineOverrideScope(pipeline);
        }

        public static XRCamera.EDepthMode ResolveSceneCameraDepthModePreference()
            => RuntimeRenderingHostServices.Factories.ResolveSceneCameraDepthModePreference();

        public static ERenderClipDepthRange ResolveEffectiveClipDepthRange(RuntimeGraphicsApiKind backend)
            => Settings.ClipDepthRange;

        public static ERenderClipDepthRange EffectiveClipDepthRange
            => ResolveEffectiveClipDepthRange(RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend);

        public static bool ShouldUseNativeVulkanDepthClipControl
            => Settings.ClipDepthRange == ERenderClipDepthRange.NegativeOneToOne &&
               State.HasVulkanDepthClipControl;

        public static bool ShouldUseVulkanShaderClipDepthRemap
            => Settings.ClipDepthRange == ERenderClipDepthRange.NegativeOneToOne &&
               !State.HasVulkanDepthClipControl;

        public static Func<XRWindow, BoundingRectangle?>? ScenePanelRenderRegionProvider { get; set; }

        public static bool IsVulkanRendererActive()
            => RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan;

        /// <summary>
        /// Strategy the user requested in <c>ForceMeshSubmissionStrategy</c> when the
        /// resolver had to downgrade it (typically a meshlet strategy on a backend that
        /// can't dispatch mesh tasks). Null when no downgrade is active.
        /// </summary>
        public static EMeshSubmissionStrategy? LastMeshletDowngradeRequested { get; private set; }
        /// <summary>Strategy the resolver substituted for the requested meshlet strategy.</summary>
        public static EMeshSubmissionStrategy? LastMeshletDowngradeResolved { get; private set; }
        /// <summary>Human-readable reason for the meshlet downgrade.</summary>
        public static string? LastMeshletDowngradeReason { get; private set; }
        /// <summary>Active render backend snapshotted by the last meshlet/strategy resolve.</summary>
        public static RuntimeGraphicsApiKind LastResolvedRendererBackend { get; private set; }
        /// <summary>Mesh-shader dialect (none/KHR/NV/Vulkan EXT) the active renderer reported.</summary>
        public static EMeshShaderDialect LastResolvedMeshShaderDialect { get; private set; }
        /// <summary>True when the active renderer reported a production meshlet dispatch path.</summary>
        public static bool LastResolvedSupportsMeshletDispatch { get; private set; }

        public static void ApplyGpuRenderDispatchToPipeline(object? pipeline, bool enabled)
        {
        }

        public static void ApplyMeshSubmissionStrategyToPipeline(object? pipeline, EMeshSubmissionStrategy strategy)
        {
        }

        /// <summary>
        /// Applies exact-transparency bindings without exposing the internal binding implementation.
        /// </summary>
        public static void ConfigureExactTransparencyMaterialProgram(
            XRMaterialBase material,
            XRRenderProgram program)
            => ExactTransparencyShaderBindings.ConfigureMaterialProgram(material, program);

        public static bool ResolveGpuRenderDispatchPreference(bool requested)
            => VulkanFeatureProfile.ResolveGpuRenderDispatchPreference(requested);

        /// <summary>
        /// Immutable inputs for the pure mesh-submission strategy resolver.
        /// </summary>
        public readonly record struct MeshSubmissionStrategyResolverInputs(
            bool RequestedGpuDispatch,
            EMeshSubmissionStrategy? ForcedStrategy,
            bool EnableGpuIndirectDebugLogging,
            bool EnableGpuIndirectValidationLogging,
            bool EnableGpuIndirectCpuFallback,
            bool EnableZeroReadbackMaterialScatter,
            bool EnableEditorZeroReadbackMaterialScatter,
            bool VulkanFeatureProfileActive,
            EVulkanGpuDrivenProfile ActiveVulkanProfile,
            bool EnforceStrictNoFallbacks,
            bool GpuRenderDispatchAllowed,
            bool SupportsIndirectCountDraw,
            EMeshShaderDialect MeshShaderDialect,
            bool SupportsDirectMeshTaskDispatch,
            bool SupportsIndirectCountMeshTaskDispatch,
            bool SupportsMeshletDispatch);

        /// <summary>
        /// Resolves a mesh-submission strategy from an explicit capability snapshot.
        /// This overload is deterministic and does not read or mutate runtime state.
        /// </summary>
        public static EMeshSubmissionStrategy ResolveMeshSubmissionStrategy(
            MeshSubmissionStrategyResolverInputs inputs)
        {
            if (inputs.ForcedStrategy is { } forcedStrategy)
            {
                if (!forcedStrategy.IsAnyMeshletStrategy())
                    return forcedStrategy;

                if (inputs.SupportsMeshletDispatch)
                {
                    bool instrumentationAllowed =
                        (inputs.VulkanFeatureProfileActive &&
                         inputs.ActiveVulkanProfile == EVulkanGpuDrivenProfile.Diagnostics) ||
                        inputs.EnableGpuIndirectDebugLogging;

                    if (forcedStrategy == EMeshSubmissionStrategy.GpuMeshletInstrumented &&
                        instrumentationAllowed)
                    {
                        return EMeshSubmissionStrategy.GpuMeshletInstrumented;
                    }

                    return EMeshSubmissionStrategy.GpuMeshletZeroReadback;
                }

                if (inputs.SupportsIndirectCountDraw)
                    return EMeshSubmissionStrategy.GpuIndirectZeroReadback;

                return inputs.EnforceStrictNoFallbacks
                    ? EMeshSubmissionStrategy.CpuDirect
                    : EMeshSubmissionStrategy.GpuIndirectInstrumented;
            }

            if (!inputs.RequestedGpuDispatch)
                return EMeshSubmissionStrategy.CpuDirect;

            if (inputs.VulkanFeatureProfileActive && !inputs.GpuRenderDispatchAllowed)
                return EMeshSubmissionStrategy.CpuDirect;

            bool diagnosticsProfile = inputs.VulkanFeatureProfileActive &&
                inputs.ActiveVulkanProfile == EVulkanGpuDrivenProfile.Diagnostics;
            bool shippingFastProfile = inputs.VulkanFeatureProfileActive &&
                inputs.ActiveVulkanProfile == EVulkanGpuDrivenProfile.ShippingFast;
            bool instrumentationRequested = diagnosticsProfile
                || inputs.EnableGpuIndirectDebugLogging
                || inputs.EnableGpuIndirectValidationLogging
                || inputs.EnableGpuIndirectCpuFallback;
            bool zeroReadbackRequested = shippingFastProfile
                || inputs.EnableZeroReadbackMaterialScatter
                || inputs.EnableEditorZeroReadbackMaterialScatter;

            if (zeroReadbackRequested)
            {
                if (inputs.SupportsIndirectCountDraw)
                    return EMeshSubmissionStrategy.GpuIndirectZeroReadback;

                return inputs.EnforceStrictNoFallbacks
                    ? EMeshSubmissionStrategy.CpuDirect
                    : EMeshSubmissionStrategy.GpuIndirectInstrumented;
            }

            if (instrumentationRequested)
                return EMeshSubmissionStrategy.GpuIndirectInstrumented;

            return inputs.SupportsIndirectCountDraw
                ? EMeshSubmissionStrategy.GpuIndirectInstrumented
                : EMeshSubmissionStrategy.CpuDirect;
        }

        public static EMeshSubmissionStrategy ResolveMeshSubmissionStrategy(bool? requestedGpuDispatch = null)
        {
            AbstractRenderer? renderer = AbstractRenderer.Current;
            bool rendererKnown = renderer is not null;
            bool supportsIndirectCount = rendererKnown ? renderer!.SupportsIndirectCountDraw() : true;
            EMeshShaderDialect meshShaderDialect = renderer?.MeshShaderDialect ?? EMeshShaderDialect.None;
            bool supportsDirectMeshTaskDispatch = renderer?.SupportsDirectMeshTaskDispatch() ?? false;
            bool supportsIndirectCountMeshTaskDispatch = renderer?.SupportsIndirectCountMeshTaskDispatch() ?? false;
            bool supportsMeshletDispatch = renderer?.SupportsMeshletDispatch() ?? false;

            // Snapshot inputs the UI uses to explain meshlet availability without re-deriving them.
            LastResolvedRendererBackend = RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend;
            LastResolvedMeshShaderDialect = meshShaderDialect;
            LastResolvedSupportsMeshletDispatch = supportsMeshletDispatch;

            EMeshSubmissionStrategy? forced = RuntimeEngine.EffectiveSettings.ForceMeshSubmissionStrategy;
            if (forced.HasValue)
            {
                if (forced.Value.IsAnyMeshletStrategy())
                {
                    EMeshSubmissionStrategy resolved = ResolveForcedMeshletSubmissionStrategy(
                        forced.Value,
                        IsMeshletInstrumentationAllowed(),
                        supportsMeshletDispatch,
                        supportsIndirectCount);

                    if (resolved != forced.Value)
                    {
                        string reason = GetMeshletFallbackReason(
                            forced.Value,
                            supportsMeshletDispatch,
                            meshShaderDialect,
                            supportsDirectMeshTaskDispatch,
                            supportsIndirectCountMeshTaskDispatch);

                        LastMeshletDowngradeRequested = forced.Value;
                        LastMeshletDowngradeResolved = resolved;
                        LastMeshletDowngradeReason = reason;

                        XREngine.Debug.RenderingWarningEvery(
                            "RenderDispatch.MeshSubmissionStrategy.UnsupportedGpuMeshlet",
                            TimeSpan.FromSeconds(2),
                            "[RenderDispatch] Mesh submission strategy downgraded from {0} to {1}. Dialect={2}; DirectTaskDispatch={3}; IndirectCountTaskDispatch={4}; FallbackReason={5}.",
                            forced.Value,
                            resolved,
                            meshShaderDialect,
                            supportsDirectMeshTaskDispatch,
                            supportsIndirectCountMeshTaskDispatch,
                            reason);
                    }
                    else
                    {
                        LastMeshletDowngradeRequested = null;
                        LastMeshletDowngradeResolved = null;
                        LastMeshletDowngradeReason = null;
                    }

                    return resolved;
                }

                return forced.Value;
            }

            if (!(requestedGpuDispatch ?? RuntimeEngine.EffectiveSettings.GPURenderDispatch))
                return EMeshSubmissionStrategy.CpuDirect;

            if (VulkanFeatureProfile.IsActive && !VulkanFeatureProfile.ResolveGpuRenderDispatchPreference(true))
                return EMeshSubmissionStrategy.CpuDirect;

            bool diagnosticsProfile = VulkanFeatureProfile.IsActive &&
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics;
            bool shippingFastProfile = VulkanFeatureProfile.IsActive &&
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.ShippingFast;
            bool zeroReadbackRequested = shippingFastProfile
                || RuntimeEngine.EffectiveSettings.EnableZeroReadbackMaterialScatter
                || RuntimeEngine.EditorPreferences.Debug.EnableZeroReadbackMaterialScatter;
            bool instrumentationRequested = diagnosticsProfile
                || RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging
                || RuntimeEngine.EffectiveSettings.EnableGpuIndirectValidationLogging
                || RuntimeEngine.EffectiveSettings.EnableGpuIndirectCpuFallback;

            if (zeroReadbackRequested)
            {
                if (supportsIndirectCount)
                    return EMeshSubmissionStrategy.GpuIndirectZeroReadback;

                return VulkanFeatureProfile.EnforceStrictNoFallbacks
                    ? EMeshSubmissionStrategy.CpuDirect
                    : EMeshSubmissionStrategy.GpuIndirectInstrumented;
            }

            if (instrumentationRequested)
                return EMeshSubmissionStrategy.GpuIndirectInstrumented;

            return supportsIndirectCount
                ? EMeshSubmissionStrategy.GpuIndirectInstrumented
                : EMeshSubmissionStrategy.CpuDirect;
        }

        private static EMeshSubmissionStrategy ResolveForcedMeshletSubmissionStrategy(
            EMeshSubmissionStrategy requestedStrategy,
            bool instrumentationAllowed,
            bool supportsMeshletDispatch,
            bool supportsIndirectCountDraw)
        {
            if (supportsMeshletDispatch)
            {
                if (requestedStrategy == EMeshSubmissionStrategy.GpuMeshletInstrumented && instrumentationAllowed)
                    return EMeshSubmissionStrategy.GpuMeshletInstrumented;

                return EMeshSubmissionStrategy.GpuMeshletZeroReadback;
            }

            if (supportsIndirectCountDraw)
                return EMeshSubmissionStrategy.GpuIndirectZeroReadback;

            return VulkanFeatureProfile.EnforceStrictNoFallbacks
                ? EMeshSubmissionStrategy.CpuDirect
                : EMeshSubmissionStrategy.GpuIndirectInstrumented;
        }

        private static bool IsMeshletInstrumentationAllowed()
            => (VulkanFeatureProfile.IsActive &&
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics) ||
               RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging;

        private static string GetMeshletFallbackReason(
            EMeshSubmissionStrategy requestedStrategy,
            bool supportsMeshletDispatch,
            EMeshShaderDialect meshShaderDialect,
            bool supportsDirectMeshTaskDispatch,
            bool supportsIndirectCountMeshTaskDispatch)
        {
            if (supportsMeshletDispatch)
            {
                if (requestedStrategy == EMeshSubmissionStrategy.GpuMeshletInstrumented &&
                    !IsMeshletInstrumentationAllowed())
                {
                    return "meshlet instrumentation requires the Diagnostics Vulkan profile or EnableGpuIndirectDebugLogging";
                }

                return "production meshlet dispatch is available";
            }

            if (meshShaderDialect == EMeshShaderDialect.None)
                return "no mesh shader dialect is available";

            if (supportsDirectMeshTaskDispatch && !supportsIndirectCountMeshTaskDispatch)
                return "only diagnostic CPU-count mesh task dispatch is available";

            if (!supportsIndirectCountMeshTaskDispatch)
                return "production indirect-count mesh task dispatch is unavailable";

            return "production meshlet dispatch is unavailable";
        }

        internal static void RaiseSettingsChanged() => NotifySettingsChanged(null);
        internal static void RaiseAntiAliasingSettingsChanged() => NotifyAntiAliasingSettingChanged();


        public static class State
        {
            public static ulong RenderFrameId => StateData.RenderFrameId;
            public static IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState => StateData.ActiveRenderCommandExecutionState;
            public static int CurrentRenderGraphPassIndex => StateData.CurrentRenderGraphPassIndex;
            internal static XRRenderPipelineInstance? CurrentRenderGraphPassPipeline => StateData.CurrentRenderGraphPassPipeline;
            public static uint CurrentTransformId => StateData.CurrentTransformId;
            public static XRRenderPipelineInstance? CurrentRenderingPipeline => StateData.CurrentRenderingPipeline;
            public static RenderResourceRegistry? CurrentResourceRegistry => CurrentRenderingPipeline?.Resources;
            public static XRRenderPipelineInstance.RenderingState? RenderingPipelineState => StateData.RenderingPipelineState;
            public static XRViewport? RenderingViewport => StateData.RenderingViewport;
            public static IRuntimeRenderWorld? RenderingWorld => StateData.RenderingWorld;
            public static VisualScene? RenderingScene => RenderingPipelineState?.Scene;
            public static XRCamera? RenderingCamera => StateData.RenderingCamera;
            public static XRCamera? RenderingStereoRightEyeCamera => RenderingPipelineState?.StereoRightEyeCamera;
            public static XRFrameBuffer? RenderingTargetOutputFBO => RenderingPipelineState?.OutputFBO;
            public static XRMaterial? OverrideMaterial => RenderingPipelineState?.OverrideMaterial;
            public static XRCamera? RenderingCameraOverride
            {
                get => StateData.RenderingCameraOverride;
                set => StateData.RenderingCameraOverride = value;
            }
            public static BoundingRectangle RenderArea => StateData.RenderArea;
            public static float DefaultDepthClearValue => StateData.DefaultDepthClearValue;
            public static bool IsShadowPass => StateData.IsShadowPass;
            public static bool IsStereoPass => RenderingPipelineState?.StereoPass ?? false;
            public static bool IsDirectionalCascadeLayeredShadowPass => RenderingPipelineState?.DirectionalCascadeLayeredShadowPass ?? false;
            public static bool IsDirectionalCascadeInstancedLayeredShadowPass => RenderingPipelineState?.DirectionalCascadeInstancedLayeredShadowPass ?? false;
            public static bool IsDirectionalCascadeAtlasGroupedShadowPass => RenderingPipelineState?.DirectionalCascadeAtlasGroupedShadowPass ?? false;
            public static int DirectionalCascadeShadowLayerCount => RenderingPipelineState?.DirectionalCascadeShadowLayerCount ?? 0;
            public static bool IsPointLightLayeredShadowPass => RenderingPipelineState?.PointLightLayeredShadowPass ?? false;
            public static bool IsPointLightInstancedLayeredShadowPass => RenderingPipelineState?.PointLightInstancedLayeredShadowPass ?? false;
            public static bool IsPointLightAtlasGroupedShadowPass => RenderingPipelineState?.PointLightAtlasGroupedShadowPass ?? false;
            public static int PointLightShadowFaceCount => RenderingPipelineState?.PointLightShadowFaceCount ?? 0;
            public static bool IsSceneCapturePass
            {
                get => StateData.IsSceneCapturePass;
                set => StateData.IsSceneCapturePass = value;
            }
            public static bool IsLightProbePass
            {
                get => StateData.IsLightProbePass;
                set => StateData.IsLightProbePass = value;
            }
            public static int MirrorPassIndex => StateData.MirrorPassIndex;
            public static bool IsMirrorPass => StateData.IsMirrorPass;
            public static bool IsReflectedMirrorPass => (MirrorPassIndex & 1) == 1;
            public static bool IsMainPass => !IsMirrorPass && !IsSceneCapturePass && !IsLightProbePass;
            public static bool ReverseWinding
            {
                get => StateData.ReverseWinding;
                internal set => StateData.ReverseWinding = value;
            }
            public static bool ReverseCulling
            {
                get => StateData.ReverseCulling;
                internal set => StateData.ReverseCulling = value;
            }
            public static bool HasOvrMultiViewExtension { get; internal set; }
            public static bool SupportsOpenGLLayeredFramebuffers { get; internal set; }
            public static bool SupportsOpenGLGeometryShaderLayeredRendering { get; internal set; }
            public static bool SupportsOpenGLVertexShaderLayeredRendering { get; internal set; }
            public static bool SupportsOpenGLViewportArray { get; internal set; }
            public static bool SupportsOpenGLViewportScissorArray { get; internal set; }
            public static bool SupportsOpenGLVertexShaderViewportIndex { get; internal set; }
            public static bool SupportsOpenGLGeometryShaderViewportIndex { get; internal set; }
            public static int MaxOpenGLViewports { get; internal set; } = 1;
            public static bool HasVulkanMultiView { get; internal set; }
            public static bool HasAnyMultiViewExtension => HasOvrMultiViewExtension || HasVulkanMultiView;
            public static bool DebugInstanceRenderingAvailable { get; internal set; } = true;
            public static bool IsNVIDIA { get; internal set; }
            public static bool IsIntel { get; internal set; }
            public static bool IsVulkan { get; internal set; }
            public static bool VulkanValidationLayersEnabled { get; internal set; }
            public static bool VulkanSynchronizationValidationEnabled { get; internal set; }
            public static bool HasNvRayTracing { get; internal set; }
            public static bool HasVulkanRayTracing { get; internal set; }
            public static bool HasVulkanMemoryDecompression { get; internal set; }
            public static bool HasVulkanCopyMemoryIndirect { get; internal set; }
            public static bool HasVulkanRtxIo { get; internal set; }
            public static bool HasVulkanDepthClipControl { get; internal set; }
            public static bool HasParallelShaderCompile { get; internal set; }
            public static string OpenGLParallelShaderCompileExtension { get; internal set; } = string.Empty;
            public static bool OpenGLParallelShaderCompileProbePassed { get; internal set; }
            public static string[] OpenGLExtensions { get; internal set; } = [];
            public static string? OpenGLVendor { get; internal set; }
            public static string? OpenGLRendererName { get; internal set; }
            public static string? VulkanDeviceName { get; internal set; }
            public static uint VulkanVendorId { get; internal set; }
            public static uint VulkanDeviceId { get; internal set; }
            public static XRDataBuffer? ForwardPlusLocalLightsBuffer { get; internal set; }
            public static XRDataBuffer? ForwardPlusVisibleIndicesBuffer { get; internal set; }
            public static XRDataBuffer? ForwardPlusTileLightCountsBuffer { get; internal set; }
            public static Vector2 ForwardPlusScreenSize { get; internal set; }
            public static int ForwardPlusTileSize { get; internal set; }
            public static int ForwardPlusTileCountX { get; internal set; }
            public static int ForwardPlusTileCountY { get; internal set; }
            public static int ForwardPlusMaxLightsPerTile { get; internal set; }
            public static int ForwardPlusLocalLightCount { get; internal set; }
            public static bool ForwardPlusEnabled => ForwardPlusLocalLightsBuffer is not null && ForwardPlusVisibleIndicesBuffer is not null && ForwardPlusLocalLightCount > 0;

            public static IDisposable PushRenderGraphPassIndex(int passIndex) => StateData.PushRenderGraphPassIndex(passIndex);
            public static IDisposable PushTransformId(uint transformId) => StateData.PushTransformId(transformId);
            public static IDisposable? PushRenderingPipeline(XRRenderPipelineInstance pipeline) => Rendering.PushRenderingPipeline(pipeline);
            public static RenderingPipelineOverrideScope PushRenderingPipelineOverride(XRRenderPipelineInstance? pipeline)
                => Rendering.PushRenderingPipelineOverride(pipeline);
            public static void PushMirrorPass() => StateData.PushMirrorPass();
            public static void PopMirrorPass() => StateData.PopMirrorPass();
            internal static void BeginRenderFrame() => StateData.BeginRenderFrame();

            public static void ClearColor(ColorF4 color) => AbstractRenderer.Current?.ClearColor(color);
            public static void ClearStencil(int value) => AbstractRenderer.Current?.ClearStencil(value);
            public static void ClearDepth(float value) => AbstractRenderer.Current?.ClearDepth(value);
            public static void Clear(bool color, bool depth, bool stencil) => AbstractRenderer.Current?.Clear(color, depth, stencil);
            public static void ClearByBoundFBO(bool color = true, bool depth = true, bool stencil = true)
            {
                if (depth)
                    ClearDepth(GetDefaultDepthClearValue());

                var boundFBO = XRFrameBuffer.BoundForWriting;
                if (boundFBO is null)
                {
                    Clear(color, depth, stencil);
                    return;
                }

                var textureTypes = boundFBO.TextureTypes;
                Clear(
                    (textureTypes & EFrameBufferTextureTypeFlags.Color) != 0 && color,
                    (textureTypes & EFrameBufferTextureTypeFlags.Depth) != 0 && depth,
                    (textureTypes & EFrameBufferTextureTypeFlags.Stencil) != 0 && stencil);
            }
            public static void UnbindFrameBuffers(EFramebufferTarget target) => AbstractRenderer.Current?.BindFrameBuffer(target, null);
            public static void SetReadBuffer(EReadBufferMode mode) => AbstractRenderer.Current?.SetReadBuffer(mode);
            public static void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode) => AbstractRenderer.Current?.SetReadBuffer(fbo, mode);
            public static float GetDepth(int x, int y) => AbstractRenderer.Current?.GetDepth(x, y) ?? 0.0f;
            public static Task<float> GetDepthAsync(XRFrameBuffer fbo, int x, int y)
            {
                var completion = new TaskCompletionSource<float>();
                AbstractRenderer.Current?.GetDepthAsync(fbo, x, y, completion.SetResult);
                return completion.Task;
            }
            public static byte GetStencilIndex(float x, float y) => AbstractRenderer.Current?.GetStencilIndex(x, y) ?? 0;
            public static void EnableDepthTest(bool enable) => AbstractRenderer.Current?.EnableDepthTest(enable);
            public static void StencilMask(uint mask) => AbstractRenderer.Current?.StencilMask(mask);
            public static void EnableStencilTest(bool enable) => AbstractRenderer.Current?.EnableStencilTest(enable);
            public static void StencilFunc(EComparison function, int reference, uint mask) => AbstractRenderer.Current?.StencilFunc(function, reference, mask);
            public static void StencilOp(EStencilOp sfail, EStencilOp dpfail, EStencilOp dppass) => AbstractRenderer.Current?.StencilOp(sfail, dpfail, dppass);
            public static void EnableBlend(bool enable) => AbstractRenderer.Current?.EnableBlend(enable);
            public static void BlendFunc(EBlendingFactor source, EBlendingFactor destination) => AbstractRenderer.Current?.BlendFunc(source, destination);
            public static void BlendFuncSeparate(EBlendingFactor srcRgb, EBlendingFactor dstRgb, EBlendingFactor srcAlpha, EBlendingFactor dstAlpha) => AbstractRenderer.Current?.BlendFuncSeparate(srcRgb, dstRgb, srcAlpha, dstAlpha);
            public static void BlendEquation(EBlendEquationMode mode) => AbstractRenderer.Current?.BlendEquation(mode);
            public static void BlendEquationSeparate(EBlendEquationMode modeRgb, EBlendEquationMode modeAlpha) => AbstractRenderer.Current?.BlendEquationSeparate(modeRgb, modeAlpha);
            public static void EnableSampleShading(float minValue) => AbstractRenderer.Current?.EnableSampleShading(minValue);
            public static void DisableSampleShading() => AbstractRenderer.Current?.DisableSampleShading();
            public static void AllowDepthWrite(bool allow) => AbstractRenderer.Current?.AllowDepthWrite(allow);
            public static void DepthFunc(EComparison comparison) => AbstractRenderer.Current?.DepthFunc(MapDepthComparison(comparison));
            public static void ColorMask(bool red, bool green, bool blue, bool alpha) => AbstractRenderer.Current?.ColorMask(red, green, blue, alpha);
            public static XRCamera.EDepthMode GetDepthMode() => RenderingCamera?.DepthMode ?? XRCamera.EDepthMode.Normal;
            public static float GetDefaultDepthClearValue() => RenderingCamera?.GetDepthClearValue() ?? 1.0f;
            public static EComparison MapDepthComparison(EComparison comparison)
            {
                if (GetDepthMode() != XRCamera.EDepthMode.Reversed)
                    return comparison;

                return comparison switch
                {
                    EComparison.Less => EComparison.Greater,
                    EComparison.Lequal => EComparison.Gequal,
                    EComparison.Greater => EComparison.Less,
                    EComparison.Gequal => EComparison.Lequal,
                    _ => comparison
                };
            }
            public static void CalculateDotLuminanceAsync(XRTexture2D texture, bool generateMipmapsNow, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, Settings.DefaultLuminance, generateMipmapsNow);
            public static void CalculateDotLuminanceAsync(XRTexture2DArray texture, bool generateMipmapsNow, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, Settings.DefaultLuminance, generateMipmapsNow);
            public static void CalculateDotLuminanceAsync(XRTexture2D texture, bool generateMipmapsNow, Vector3 luminance, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, luminance, generateMipmapsNow);
            public static void CalculateDotLuminanceAsync(XRTexture2DArray texture, bool generateMipmapsNow, Vector3 luminance, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, luminance, generateMipmapsNow);
            public static void CalculateFrontBufferDotLuminanceAsync(BoundingRectangle region, bool withTransparency, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceFrontAsync(region, withTransparency, callback);
            public static void CalculateFrontBufferDotLuminanceAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);
        }

        public sealed class RuntimeRenderingState
        {
            private static readonly Action<object?> PopRenderGraphPassAction = static state =>
            {
                Stack<RenderGraphPassScopeState> stack = (Stack<RenderGraphPassScopeState>)state!;
                if (stack.Count != 0)
                    stack.Pop();
            };
            private static readonly Action<object?> PopTransformIdAction = static state =>
            {
                Stack<uint> stack = (Stack<uint>)state!;
                if (stack.Count != 0)
                    stack.Pop();
            };
            private static readonly Action<object?> PopMirrorPassAction =
                static state => ((RuntimeRenderingState)state!).PopMirrorPassScope();

            [ThreadStatic]
            private static Stack<RenderGraphPassScopeState>? t_renderGraphPasses;
            [ThreadStatic]
            private static Stack<XRCamera?>? t_cameraOverrides;
            [ThreadStatic]
            private static Stack<uint>? t_transformIds;
            [ThreadStatic]
            private static Stack<int>? t_mirrorPasses;
            [ThreadStatic]
            private static Stack<MirrorPassState>? t_mirrorPassStates;
            [ThreadStatic]
            private static bool t_isSceneCapturePass;
            [ThreadStatic]
            private static bool t_isLightProbePass;
            [ThreadStatic]
            private static bool t_reverseWinding;
            [ThreadStatic]
            private static bool t_reverseCulling;

            private static Stack<RenderGraphPassScopeState> RenderGraphPasses => t_renderGraphPasses ??= new();
            private static Stack<XRCamera?> CameraOverrides => t_cameraOverrides ??= new();
            private static Stack<uint> TransformIds => t_transformIds ??= new();
            private static Stack<int> MirrorPasses => t_mirrorPasses ??= new();
            private static Stack<MirrorPassState> MirrorPassStates => t_mirrorPassStates ??= new();

            // Runtime.Rendering owns frame and pipeline state. The installed host
            // exposes it to consumers, so sourcing it back from FrameTiming would
            // re-enter the concrete host adapter and recurse.
            private long _renderFrameId;
            private readonly record struct MirrorPassState(bool IsSceneCapturePass, bool ReverseCulling);

            public ulong RenderFrameId => unchecked((ulong)Volatile.Read(ref _renderFrameId));
            public IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState
                => CurrentRenderingPipeline?.RenderState;
            public XRRenderPipelineInstance? CurrentRenderingPipeline
            {
                get
                {
                    if (t_pipelineOverrideStack is { Count: > 0 } overrideStack)
                        return overrideStack.Peek();

                    if (t_pipelineStack is { Count: > 0 } pipelineStack)
                        return pipelineStack.Peek();

                    return null;
                }
            }
            public XRRenderPipelineInstance.RenderingState? RenderingPipelineState => CurrentRenderingPipeline?.RenderState;
            public XRViewport? RenderingViewport => CurrentRenderingPipeline?.RenderState.WindowViewport ?? CurrentRenderingPipeline?.LastWindowViewport;
            public IRuntimeRenderWorld? RenderingWorld => RenderingViewport?.World;
            public XRCamera? RenderingCamera
            {
                get
                {
                    XRRenderPipelineInstance? pipeline = CurrentRenderingPipeline;
                    IRuntimeRenderCommandExecutionState? commandState = ActiveRenderCommandExecutionState;
                    return RenderingCameraOverride
                        ?? commandState?.RenderingCamera as XRCamera
                        ?? commandState?.SceneCamera as XRCamera
                        ?? pipeline?.RenderState.RenderingCamera
                        ?? pipeline?.RenderState.SceneCamera
                        ?? pipeline?.LastSceneCamera
                        ?? pipeline?.LastRenderingCamera;
                }
            }
            public XRCamera? RenderingCameraOverride
            {
                get => t_cameraOverrides is { Count: > 0 } stack ? stack.Peek() : null;
                set
                {
                    Stack<XRCamera?> stack = CameraOverrides;
                    if (value is null)
                    {
                        if (stack.Count != 0)
                            stack.Pop();
                    }
                    else
                    {
                        stack.Push(value);
                    }
                }
            }
            public BoundingRectangle RenderArea => RenderingPipelineState?.CurrentRenderRegion ?? BoundingRectangle.Empty;
            public float DefaultDepthClearValue => 1.0f;
            public bool IsShadowPass => CurrentRenderingPipeline?.RenderState.ShadowPass ?? false;
            public bool IsSceneCapturePass
            {
                get => t_isSceneCapturePass;
                set => t_isSceneCapturePass = value;
            }
            public bool IsLightProbePass
            {
                get => t_isLightProbePass;
                set => t_isLightProbePass = value;
            }
            public bool IsMirrorPass => MirrorPassIndex > 0;
            public bool IsReflectedMirrorPass => (MirrorPassIndex & 1) == 1;
            public int MirrorPassIndex => t_mirrorPasses is { Count: > 0 } stack ? stack.Peek() : 0;
            public int CurrentRenderGraphPassIndex
                => t_renderGraphPasses is { Count: > 0 } stack
                    ? stack.Peek().PassIndex
                    : int.MinValue;
            internal XRRenderPipelineInstance? CurrentRenderGraphPassPipeline
                => t_renderGraphPasses is { Count: > 0 } stack
                    ? stack.Peek().OwnerPipeline
                    : null;
            public uint CurrentTransformId => t_transformIds is { Count: > 0 } stack ? stack.Peek() : 0u;
            public bool ReverseWinding
            {
                get => t_reverseWinding;
                internal set => t_reverseWinding = value;
            }
            public bool ReverseCulling
            {
                get => t_reverseCulling;
                internal set => t_reverseCulling = value;
            }

            public void BeginRenderFrame()
            {
                Interlocked.Increment(ref _renderFrameId);
            }

            public IDisposable PushRenderGraphPassIndex(int passIndex)
            {
                Stack<RenderGraphPassScopeState> stack = RenderGraphPasses;
                stack.Push(new RenderGraphPassScopeState(passIndex, CurrentRenderingPipeline));
                return StateObject.New(PopRenderGraphPassAction, stack);
            }

            public IDisposable PushTransformId(uint transformId)
            {
                Stack<uint> stack = TransformIds;
                stack.Push(transformId);
                return StateObject.New(PopTransformIdAction, stack);
            }

            public IDisposable PushMirrorPass(int mirrorPassIndex)
            {
                Stack<int> stack = MirrorPasses;
                PushMirrorPassState();
                stack.Push(mirrorPassIndex);
                ApplyActiveMirrorPassState();
                return StateObject.New(PopMirrorPassAction, this);
            }

            private void PopMirrorPassScope()
            {
                if (t_mirrorPasses is { Count: > 0 } stack)
                    stack.Pop();
                RestoreMirrorPassState();
            }

            public void PushMirrorPass()
            {
                PushMirrorPassState();
                MirrorPasses.Push(MirrorPassIndex + 1);
                ApplyActiveMirrorPassState();
            }

            public void PopMirrorPass()
            {
                if (t_mirrorPasses is { Count: > 0 } stack)
                    stack.Pop();
                RestoreMirrorPassState();
            }

            private void PushMirrorPassState()
                => MirrorPassStates.Push(new(IsSceneCapturePass, ReverseCulling));

            private void ApplyActiveMirrorPassState()
            {
                IsSceneCapturePass = true;
                ReverseCulling = IsReflectedMirrorPass;
            }

            private void RestoreMirrorPassState()
            {
                MirrorPassState previous = t_mirrorPassStates is { Count: > 0 } stack
                    ? stack.Pop()
                    : default;

                if (IsMirrorPass)
                {
                    ApplyActiveMirrorPassState();
                    return;
                }

                IsSceneCapturePass = previous.IsSceneCapturePass;
                ReverseCulling = previous.ReverseCulling;
            }
        }
    }
}

using MemoryPack;
using System.ComponentModel;
using XREngine.Components.Scene.Transforms;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine
{
    /// <summary>
    /// Project-level game configuration settings.
    /// Contains startup, networking, and build configuration.
    /// Also includes optional overrides for engine-level settings (Project > Engine cascade).
    /// </summary>
    [MemoryPackable]
    public partial class GameStartupSettings : OverrideableSettingsAssetBase
    {
        public GameStartupSettings()
        {
            AttachSubSettings(_buildSettings, _defaultUserSettings, _rendering);
            TrackOverrideableSettings();
        }

        private BuildSettings _buildSettings = new();
        private ENetworkingType _networkingType = ENetworkingType.Local;
        private List<GameWindowStartupSettings> _startupWindows = [];
        private ETwoPlayerPreference _twoPlayerViewportPreference;
        private EThreePlayerPreference _threePlayerViewportPreference;
        private bool _logOutputToFile = true;
        private UserSettings _defaultUserSettings = new();
        private GameRenderingOverrides _rendering = new();
        private string _texturesFolder = "";
        private float? _targetUpdatesPerSecond = 90.0f;
        private float _fixedFramesPerSecond = 90.0f;
        private float? _targetFramesPerSecond = 90.0f;
        private float? _unfocusedTargetFramesPerSecond = null;
        private bool _runVRInPlace = true;
        private bool _gpuRenderDispatch = false;

        private string _udpMulticastGroupIP = "239.0.0.222";
        private int _udpMulticastPort = 5000;
        private int _udpServerBindPort = 5000;
        //private string _tcpListenerIP = "0.0.0.0";
        //private int _tcpListenerPort = 5001;
        private string _serverIP = "127.0.0.1";
        private RealtimeTransportKind _multiplayerTransport = RealtimeTransportKind.NativeUdp;
        private Guid? _multiplayerSessionId;
        private string? _multiplayerSessionToken;
        private string? _expectedMultiplayerProtocolVersion;
        private WorldAssetIdentity? _expectedMultiplayerWorldAsset;

        [Category("Windows")]
        [Description("List of windows that will be created at startup (resolution, target world, local players, etc.).")]
        public List<GameWindowStartupSettings> StartupWindows
        {
            get => _startupWindows;
            set => SetField(ref _startupWindows, value);
        }

        [Category("Logging")]
        [Description("When enabled, engine output is written to a log file in addition to the console.")]
        public bool LogOutputToFile
        {
            get => _logOutputToFile;
            set => SetField(ref _logOutputToFile, value);
        }
        /// <summary>
        /// Experimental toggle for GPU-driven render dispatch.
        /// When enabled, rendering commands are generated on the GPU for improved efficiency.
        /// </summary>
        [Category("Rendering")]
        [Description("Experimental: when enabled, rendering commands are generated on the GPU for improved efficiency.")]
        public bool GPURenderDispatch
        {
            get => _gpuRenderDispatch;
            set => SetField(ref _gpuRenderDispatch, value);
        }

        [Category("User Defaults")]
        [Description("Default per-user settings to apply when a project does not have saved user settings yet.")]
        public UserSettings DefaultUserSettings
        {
            get => _defaultUserSettings;
            set => SetField(ref _defaultUserSettings, value);
        }

        [Category("Rendering")]
        [Description("Project-owned grouped rendering overrides and backend startup policy.")]
        public GameRenderingOverrides Rendering
        {
            get => _rendering;
            set => SetField(ref _rendering, value ?? new GameRenderingOverrides());
        }

        [Category("Viewports")]
        [Description("Preferred viewport layout for two local players.")]
        public ETwoPlayerPreference TwoPlayerViewportPreference
        {
            get => _twoPlayerViewportPreference;
            set => SetField(ref _twoPlayerViewportPreference, value);
        }

        [Category("Viewports")]
        [Description("Preferred viewport layout for three local players.")]
        public EThreePlayerPreference ThreePlayerViewportPreference
        {
            get => _threePlayerViewportPreference;
            set => SetField(ref _threePlayerViewportPreference, value);
        }

        [Category("Assets")]
        [Description("Project-relative folder (or path) used as the default location for runtime texture assets.")]
        public string TexturesFolder
        {
            get => _texturesFolder;
            set => SetField(ref _texturesFolder, value);
        }
        [Category("Networking")]
        [Description("Determines whether the application runs as Server, Client, or Local-only.")]
        public ENetworkingType NetworkingType
        {
            get => _networkingType;
            set => SetField(ref _networkingType, value);
        }

        [Category("Networking")]
        [Description("Direct realtime transport selected by an external handoff payload. NativeUdp is the current supported transport.")]
        public RealtimeTransportKind MultiplayerTransport
        {
            get => _multiplayerTransport;
            set => SetField(ref _multiplayerTransport, value);
        }

        [Category("Networking")]
        [Description("Optional realtime session/room id supplied by an external control plane or launch command.")]
        public Guid? MultiplayerSessionId
        {
            get => _multiplayerSessionId;
            set => SetField(ref _multiplayerSessionId, value);
        }

        [Category("Networking")]
        [Description("Optional opaque realtime session token supplied by an external control plane.")]
        public string? MultiplayerSessionToken
        {
            get => _multiplayerSessionToken;
            set => SetField(ref _multiplayerSessionToken, value);
        }

        [Category("Networking")]
        [Description("Optional expected realtime protocol/build version supplied by an external handoff payload.")]
        public string? ExpectedMultiplayerProtocolVersion
        {
            get => _expectedMultiplayerProtocolVersion;
            set => SetField(ref _expectedMultiplayerProtocolVersion, value);
        }

        [Category("Networking")]
        [Description("Optional expected local world identity supplied by an external handoff payload and validated before realtime connect.")]
        public WorldAssetIdentity? ExpectedMultiplayerWorldAsset
        {
            get => _expectedMultiplayerWorldAsset;
            set => SetField(ref _expectedMultiplayerWorldAsset, value);
        }

        [Category("Networking")]
        [Description("UDP multicast group IP used for LAN discovery / multicast messaging when applicable.")]
        public string UdpMulticastGroupIP
        {
            get => _udpMulticastGroupIP;
            set => SetField(ref _udpMulticastGroupIP, value);
        }

        [Category("Networking")]
        [Description("UDP multicast port used for LAN discovery / multicast messaging when applicable.")]
        public int UdpMulticastPort
        {
            get => _udpMulticastPort;
            set => SetField(ref _udpMulticastPort, value);
        }

        [Category("Networking")]
        [Description("Local UDP port that a server binds for inbound realtime client packets.")]
        public int UdpServerBindPort
        {
            get => _udpServerBindPort;
            set => SetField(ref _udpServerBindPort, value);
        }

        private int _udpClientReceivePort = 5001;

        [Category("Networking")]
        [Description("Local UDP port that the client listens on for inbound packets.")]
        public int UdpClientRecievePort
        {
            get => _udpClientReceivePort;
            set => SetField(ref _udpClientReceivePort, value);
        }
        private int _udpServerSendPort = 5000;

        [Category("Networking")]
        [Description("UDP port that the server sends from / expects clients to target.")]
        public int UdpServerSendPort
        {
            get => _udpServerSendPort;
            set => SetField(ref _udpServerSendPort, value);
        }
        //public string TcpListenerIP
        //{
        //    get => _tcpListenerIP;
        //    set => SetField(ref _tcpListenerIP, value);
        //}
        //public int TcpListenerPort
        //{
        //    get => _tcpListenerPort;
        //    set => SetField(ref _tcpListenerPort, value);
        //}
        [Category("Networking")]
        [Description("Server IP address or hostname the client should connect to in Client mode.")]
        public string ServerIP
        {
            get => _serverIP;
            set => SetField(ref _serverIP, value);
        }

        [Category("Time")]
        [Description("Target game-logic update rate (ticks per second). Null uses engine default.")]
        public float? TargetUpdatesPerSecond
        {
            get => _targetUpdatesPerSecond;
            set => SetField(ref _targetUpdatesPerSecond, value);
        }

        [Category("Time")]
        [Description("Target render frames per second. Null disables the cap (engine default applies).")]
        public float? TargetFramesPerSecond
        {
            get => _targetFramesPerSecond;
            set => SetField(ref _targetFramesPerSecond, value);
        }

        [Category("Time")]
        [Description("Optional target FPS while unfocused. Null inherits TargetFramesPerSecond.")]
        public float? UnfocusedTargetFramesPerSecond
        {
            get => _unfocusedTargetFramesPerSecond;
            set => SetField(ref _unfocusedTargetFramesPerSecond, value);
        }

        [Category("Time")]
        [Description("Fixed physics tick rate (frames per second).")]
        public float FixedFramesPerSecond
        {
            get => _fixedFramesPerSecond;
            set => SetField(ref _fixedFramesPerSecond, value);
        }
        /// <summary>
        /// If true, the VR system will start in the same application as the game itself.
        /// This means VR cannot be turned off without restarting the game.
        /// </summary>
        [Category("VR")]
        [Description("If true, VR starts in-process and cannot be turned off without restarting.")]
        public bool RunVRInPlace
        {
            get => _runVRInPlace;
            set => SetField(ref _runVRInPlace, value);
        }

        [Category("Layers")]
        [Description("Mapping from layer index to display name used by editor/UI.")]
        public Dictionary<int, string> LayerNames { get; set; } = DefaultLayers.All;

        [Category("Build")]
        [Description("Build/publish configuration for this project.")]
        public BuildSettings BuildSettings
        {
            get => _buildSettings;
            set => SetField(ref _buildSettings, value ?? new BuildSettings());
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            if (propName == nameof(BuildSettings))
            {
                if (prev is BuildSettings previous)
                    previous.PropertyChanged -= HandleSubSettingsChanged;

                if (field is BuildSettings current)
                    current.PropertyChanged += HandleSubSettingsChanged;
            }

            if (propName == nameof(DefaultUserSettings))
            {
                if (prev is UserSettings previous)
                    previous.PropertyChanged -= HandleSubSettingsChanged;

                if (field is UserSettings current)
                    current.PropertyChanged += HandleSubSettingsChanged;
            }

            if (propName == nameof(Rendering))
            {
                if (prev is GameRenderingOverrides previous)
                    previous.PropertyChanged -= HandleSubSettingsChanged;

                if (field is GameRenderingOverrides current)
                    current.PropertyChanged += HandleSubSettingsChanged;
            }
        }

        private void AttachSubSettings(BuildSettings? buildSettings, UserSettings? defaultUserSettings, GameRenderingOverrides? rendering)
        {
            if (buildSettings is not null)
                buildSettings.PropertyChanged += HandleSubSettingsChanged;

            if (defaultUserSettings is not null)
                defaultUserSettings.PropertyChanged += HandleSubSettingsChanged;

            if (rendering is not null)
                rendering.PropertyChanged += HandleSubSettingsChanged;
        }

        private void HandleSubSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (IsSubSettingsMetadataProperty(e.PropertyName))
                return;

            if (ReferenceEquals(sender, _rendering))
                OnPropertyChanged(e.PropertyName, e.PreviousValue, e.NewValue);

            if (!IsDirty)
                MarkDirty();
        }

        private static bool IsSubSettingsMetadataProperty(string? propertyName)
            => propertyName is nameof(XRObjectBase.Name)
                or nameof(XRAsset.FilePath)
                or nameof(XRAsset.IsDirty)
                or nameof(XRAsset.SourceAsset)
                or nameof(XRAsset.EmbeddedAssets)
                or nameof(XRAsset.OriginalPath)
                or nameof(XRAsset.OriginalLastWriteTimeUtc);

        /// <summary>
        /// The maximum number of times a mirror can reflect another mirror.
        /// </summary>
        public enum EMaxMirrorRecursionCount
        {
            /// <summary>
            /// No recursion is allowed.
            /// </summary>
            None = 0,
            /// <summary>
            /// One recursion is allowed.
            /// </summary>
            One = 1,
            /// <summary>
            /// Two recursions are allowed.
            /// </summary>
            Two = 2,
            /// <summary>
            /// Four recursions are allowed.
            /// </summary>
            Four = 4,
            /// <summary>
            /// Eight recursions are allowed.
            /// </summary>
            Eight = 8,
            /// <summary>
            /// Sixteen recursions are allowed.
            /// </summary>
            Sixteen = 16,
        }

        /// <summary>
        /// The maximum number of times a mirror can reflect another mirror.
        /// </summary>
        [Category("Rendering")]
        [Description("Limits recursive mirror rendering to avoid runaway recursion and cost.")]
        public EMaxMirrorRecursionCount MaxMirrorRecursionCount { get; set; } = EMaxMirrorRecursionCount.Eight;

        #region Overrideable Settings (Project > Engine cascade)

        private OverrideableSetting<int> _jobWorkersOverride = new();
        private OverrideableSetting<int> _jobWorkerCapOverride = new();
        private OverrideableSetting<int> _jobQueueLimitOverride = new();
        private OverrideableSetting<int> _jobQueueWarningThresholdOverride = new();
        private OverrideableSetting<EOutputVerbosity> _outputVerbosityOverride = new();
        private OverrideableSetting<bool> _enableGpuIndirectDebugLoggingOverride = new();
        private OverrideableSetting<bool> _enableGpuIndirectCpuFallbackOverride = new();
        private OverrideableSetting<bool> _enableGpuIndirectValidationLoggingOverride = new();
        private OverrideableSetting<bool> _enableZeroReadbackMaterialScatterOverride = new();
        private OverrideableSetting<EZeroReadbackMaterialDrawPath> _zeroReadbackMaterialDrawPathOverride = new();
        private OverrideableSetting<bool> _useGpuBvhOverride = new();
        private OverrideableSetting<ECpuSceneCullingStructure> _cpuSceneCullingStructureOverride = new();
        private OverrideableSetting<uint> _bvhLeafMaxPrimsOverride = new();
        private OverrideableSetting<EBvhMode> _bvhModeOverride = new();
        private OverrideableSetting<bool> _bvhRefitOnlyWhenStableOverride = new();
        private OverrideableSetting<uint> _raycastBufferSizeOverride = new();
        private OverrideableSetting<bool> _enableGpuBvhTimingQueriesOverride = new();

        // Full cascade settings (Project > Engine, user can override)
        private OverrideableSetting<EAntiAliasingMode> _antiAliasingModeOverride = new();
        private OverrideableSetting<uint> _msaaSampleCountOverride = new();
        private OverrideableSetting<EVSyncMode> _vSyncOverride = new();
        private OverrideableSetting<EGlobalIlluminationMode> _globalIlluminationModeOverride = new();
        private OverrideableSetting<bool> _tickGroupedItemsInParallelOverride = new();
        private OverrideableSetting<bool> _enableNvidiaDlssOverride = new();
        private OverrideableSetting<EDlssQualityMode> _dlssQualityOverride = new();
        private OverrideableSetting<bool> _enableNvidiaDlssFrameGenerationOverride = new();
        private OverrideableSetting<ENvidiaDlssFrameGenerationMode> _nvidiaDlssFrameGenerationModeOverride = new();
        private OverrideableSetting<bool> _enableIntelXessOverride = new();
        private OverrideableSetting<EXessQualityMode> _xessQualityOverride = new();
        private OverrideableSetting<XRCamera.EDepthMode> _depthModeOverride = new();

        private OverrideableSetting<float> _transformReplicationKeyframeIntervalSecOverride = new();
        private OverrideableSetting<float> _timeBetweenReplicationsOverride = new();

        // Audio overrides (Game > User cascade)
        private OverrideableSetting<EAudioTransport> _audioTransportOverride = new();
        private OverrideableSetting<EAudioEffects> _audioEffectsOverride = new();
        private OverrideableSetting<bool> _audioArchitectureV2Override = new();
        private OverrideableSetting<int> _audioSampleRateOverride = new();

        /// <summary>
        /// Project override for the number of job worker threads.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("Project override for the number of job worker threads.")]
        public OverrideableSetting<int> JobWorkersOverride
        {
            get => _jobWorkersOverride;
            set => SetField(ref _jobWorkersOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the maximum job worker thread cap.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("Project override for the maximum job worker thread cap.")]
        public OverrideableSetting<int> JobWorkerCapOverride
        {
            get => _jobWorkerCapOverride;
            set => SetField(ref _jobWorkerCapOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the job queue limit.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("Project override for the job queue limit.")]
        public OverrideableSetting<int> JobQueueLimitOverride
        {
            get => _jobQueueLimitOverride;
            set => SetField(ref _jobQueueLimitOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the job queue warning threshold.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("Project override for the job queue warning threshold.")]
        public OverrideableSetting<int> JobQueueWarningThresholdOverride
        {
            get => _jobQueueWarningThresholdOverride;
            set => SetField(ref _jobQueueWarningThresholdOverride, value ?? new());
        }

        /// <summary>
        /// Project override for output verbosity level.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("Project override for output verbosity level.")]
        public OverrideableSetting<EOutputVerbosity> OutputVerbosityOverride
        {
            get => _outputVerbosityOverride;
            set => SetField(ref _outputVerbosityOverride, value ?? new());
        }

        /// <summary>
        /// Project override for GPU indirect debug logging.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Debug Overrides")]
        [Description("Project override for GPU indirect debug logging.")]
        public OverrideableSetting<bool> EnableGpuIndirectDebugLoggingOverride
        {
            get => _enableGpuIndirectDebugLoggingOverride;
            set => SetField(ref _enableGpuIndirectDebugLoggingOverride, value ?? new());
        }

        /// <summary>
        /// Project override for GPU indirect CPU fallback.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Debug Overrides")]
        [Description("Project override for GPU indirect CPU fallback.")]
        public OverrideableSetting<bool> EnableGpuIndirectCpuFallbackOverride
        {
            get => _enableGpuIndirectCpuFallbackOverride;
            set => SetField(ref _enableGpuIndirectCpuFallbackOverride, value ?? new());
        }

        /// <summary>
        /// Project override for GPU indirect validation logging.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Debug Overrides")]
        [Description("Project override for GPU indirect validation logging.")]
        public OverrideableSetting<bool> EnableGpuIndirectValidationLoggingOverride
        {
            get => _enableGpuIndirectValidationLoggingOverride;
            set => SetField(ref _enableGpuIndirectValidationLoggingOverride, value ?? new());
        }

        /// <summary>
        /// Project override for zero-readback material scatter.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Debug Overrides")]
        [Description("Project override for zero-readback material scatter.")]
        public OverrideableSetting<bool> EnableZeroReadbackMaterialScatterOverride
        {
            get => _enableZeroReadbackMaterialScatterOverride;
            set => SetField(ref _enableZeroReadbackMaterialScatterOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the zero-readback material draw path.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Debug Overrides")]
        [Description("Project override for the zero-readback material draw path.")]
        public OverrideableSetting<EZeroReadbackMaterialDrawPath> ZeroReadbackMaterialDrawPathOverride
        {
            get => _zeroReadbackMaterialDrawPathOverride;
            set => SetField(ref _zeroReadbackMaterialDrawPathOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the interval in seconds between full keyframes sent to the network for transforms.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// </summary>
        [Category("Networking Overrides")]
        [Description("Project override for the interval in seconds between full keyframes sent to the network for transforms.")]
        public OverrideableSetting<float> TransformReplicationKeyframeIntervalSecOverride
        {
            get => _transformReplicationKeyframeIntervalSecOverride;
            set => SetField(ref _transformReplicationKeyframeIntervalSecOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the minimum interval in seconds between replicated tick updates for world objects.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// </summary>
        [Category("Networking Overrides")]
        [Description("Project override for the minimum interval in seconds between replicated tick updates for world objects.")]
        public OverrideableSetting<float> TimeBetweenReplicationsOverride
        {
            get => _timeBetweenReplicationsOverride;
            set => SetField(ref _timeBetweenReplicationsOverride, value ?? new());
        }

        /// <summary>
        /// Project override for GPU BVH usage.
        /// Takes precedence over engine defaults when HasOverride is true. Can be further overridden by user settings.
        /// </summary>
        [Category("BVH Overrides")]
        [Description("Project override for GPU BVH usage.")]
        public OverrideableSetting<bool> UseGpuBvhOverride
        {
            get => _useGpuBvhOverride;
            set => SetField(ref _useGpuBvhOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the CPU spatial structure used by CPU render visibility.
        /// Takes precedence over engine defaults when HasOverride is true. Can be further overridden by environment settings.
        /// </summary>
        [Category("BVH Overrides")]
        [Description("Project override for the CPU spatial structure used by CPU render visibility.")]
        public OverrideableSetting<ECpuSceneCullingStructure> CpuSceneCullingStructureOverride
        {
            get => _cpuSceneCullingStructureOverride;
            set => SetField(ref _cpuSceneCullingStructureOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the BVH leaf primitive budget.
        /// Takes precedence over engine defaults when HasOverride is true. Can be further overridden by user settings.
        /// </summary>
        [Category("BVH Overrides")]
        [Description("Project override for the BVH leaf primitive budget.")]
        public OverrideableSetting<uint> BvhLeafMaxPrimsOverride
        {
            get => _bvhLeafMaxPrimsOverride;
            set => SetField(ref _bvhLeafMaxPrimsOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the BVH build strategy.
        /// Takes precedence over engine defaults when HasOverride is true. Can be further overridden by user settings.
        /// </summary>
        [Category("BVH Overrides")]
        [Description("Project override for the BVH build strategy.")]
        public OverrideableSetting<EBvhMode> BvhModeOverride
        {
            get => _bvhModeOverride;
            set => SetField(ref _bvhModeOverride, value ?? new());
        }

        /// <summary>
        /// Project override for preferring BVH refits only when object counts are stable.
        /// Takes precedence over engine defaults when HasOverride is true. Can be further overridden by user settings.
        /// </summary>
        [Category("BVH Overrides")]
        [Description("Project override for preferring BVH refits only when object counts are stable.")]
        public OverrideableSetting<bool> BvhRefitOnlyWhenStableOverride
        {
            get => _bvhRefitOnlyWhenStableOverride;
            set => SetField(ref _bvhRefitOnlyWhenStableOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the GPU BVH raycast readback buffer size (bytes).
        /// Takes precedence over engine defaults when HasOverride is true. Can be further overridden by user settings.
        /// </summary>
        [Category("BVH Overrides")]
        [Description("Project override for the GPU BVH raycast readback buffer size (bytes).")]
        public OverrideableSetting<uint> RaycastBufferSizeOverride
        {
            get => _raycastBufferSizeOverride;
            set => SetField(ref _raycastBufferSizeOverride, value ?? new());
        }

        /// <summary>
        /// Project override for enabling GPU BVH timestamp queries.
        /// Takes precedence over engine defaults when HasOverride is true. Can be further overridden by user settings.
        /// </summary>
        [Category("BVH Overrides")]
        [Description("Project override for enabling GPU BVH timestamp queries.")]
        public OverrideableSetting<bool> EnableGpuBvhTimingQueriesOverride
        {
            get => _enableGpuBvhTimingQueriesOverride;
            set => SetField(ref _enableGpuBvhTimingQueriesOverride, value ?? new());
        }

        /// <summary>
        /// Project override for the Vulkan GPU-driven runtime profile.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// </summary>
        [Category("Rendering Overrides")]
        [Description("Project override for the Vulkan GPU-driven runtime profile.")]
        public OverrideableSetting<EVulkanGpuDrivenProfile> VulkanGpuDrivenProfileOverride
        {
            get => Rendering.Vulkan.GpuDrivenProfileOverride;
            set => Rendering.Vulkan.GpuDrivenProfileOverride = value ?? new();
        }

        /// <summary>
        /// Project override for the render backend fallback policy.
        /// Takes precedence over engine defaults when HasOverride is true. Can be further overridden by user settings.
        /// </summary>
        [Category("Rendering Overrides")]
        [Description("Project override for render backend fallback behavior during startup.")]
        public OverrideableSetting<RenderBackendFallbackPolicy> RenderBackendFallbackPolicyOverride
        {
            get => Rendering.Common.RenderBackendFallbackPolicyOverride;
            set => Rendering.Common.RenderBackendFallbackPolicyOverride = value ?? new();
        }

        /// <summary>
        /// Project override for Vulkan dynamic-rendering target mode.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// </summary>
        [Category("Rendering Overrides")]
        [Description("Project override for Vulkan dynamic-rendering target mode.")]
        public OverrideableSetting<EVulkanRenderTargetMode> VulkanRenderTargetModeOverride
        {
            get => Rendering.Vulkan.RenderTargetModeOverride;
            set => Rendering.Vulkan.RenderTargetModeOverride = value ?? new();
        }

        /// <summary>
        /// Project override for anti-aliasing mode.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for anti-aliasing mode.")]
        public OverrideableSetting<EAntiAliasingMode> AntiAliasingModeOverride
        {
            get => _antiAliasingModeOverride;
            set => SetField(ref _antiAliasingModeOverride, value ?? new());
        }

        /// <summary>
        /// Project override for MSAA sample count.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for MSAA sample count.")]
        public OverrideableSetting<uint> MsaaSampleCountOverride
        {
            get => _msaaSampleCountOverride;
            set => SetField(ref _msaaSampleCountOverride, value ?? new());
        }

        /// <summary>
        /// Project override for VSync mode.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for VSync mode.")]
        public OverrideableSetting<EVSyncMode> VSyncOverride
        {
            get => _vSyncOverride;
            set => SetField(ref _vSyncOverride, value ?? new());
        }

        /// <summary>
        /// Project override for global illumination mode.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for global illumination mode.")]
        public OverrideableSetting<EGlobalIlluminationMode> GlobalIlluminationModeOverride
        {
            get => _globalIlluminationModeOverride;
            set => SetField(ref _globalIlluminationModeOverride, value ?? new());
        }

        /// <summary>
        /// Project override for parallel tick processing.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("Project override for parallel tick processing.")]
        public OverrideableSetting<bool> TickGroupedItemsInParallelOverride
        {
            get => _tickGroupedItemsInParallelOverride;
            set => SetField(ref _tickGroupedItemsInParallelOverride, value ?? new());
        }

        /// <summary>
        /// Project override for NVIDIA DLSS.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for NVIDIA DLSS.")]
        public OverrideableSetting<bool> EnableNvidiaDlssOverride
        {
            get => _enableNvidiaDlssOverride;
            set => SetField(ref _enableNvidiaDlssOverride, value ?? new());
        }

        /// <summary>
        /// Project override for DLSS quality mode.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for DLSS quality mode.")]
        public OverrideableSetting<EDlssQualityMode> DlssQualityOverride
        {
            get => _dlssQualityOverride;
            set => SetField(ref _dlssQualityOverride, value ?? new());
        }

        /// <summary>
        /// Project override for NVIDIA DLSS frame generation.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for NVIDIA DLSS frame generation.")]
        public OverrideableSetting<bool> EnableNvidiaDlssFrameGenerationOverride
        {
            get => _enableNvidiaDlssFrameGenerationOverride;
            set => SetField(ref _enableNvidiaDlssFrameGenerationOverride, value ?? new());
        }

        /// <summary>
        /// Project override for NVIDIA DLSS frame generation mode.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for NVIDIA DLSS frame generation mode.")]
        public OverrideableSetting<ENvidiaDlssFrameGenerationMode> NvidiaDlssFrameGenerationModeOverride
        {
            get => _nvidiaDlssFrameGenerationModeOverride;
            set => SetField(ref _nvidiaDlssFrameGenerationModeOverride, value ?? new());
        }

        /// <summary>
        /// Project override for Intel XeSS.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for Intel XeSS.")]
        public OverrideableSetting<bool> EnableIntelXessOverride
        {
            get => _enableIntelXessOverride;
            set => SetField(ref _enableIntelXessOverride, value ?? new());
        }

        /// <summary>
        /// Project override for XeSS quality mode.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Can be further overridden by user settings.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("Project override for XeSS quality mode.")]
        public OverrideableSetting<EXessQualityMode> XessQualityOverride
        {
            get => _xessQualityOverride;
            set => SetField(ref _xessQualityOverride, value ?? new());
        }

        /// <summary>
        /// Project override for scene camera depth buffer mode.
        /// When enabled, cameras default to the selected normal or reversed depth mode.
        /// The editor preference can still override this for development workflows.
        /// </summary>
        [Category("Rendering Overrides")]
        [Description("Project override for scene camera depth buffer mode.")]
        public OverrideableSetting<XRCamera.EDepthMode> DepthModeOverride
        {
            get => _depthModeOverride;
            set => SetField(ref _depthModeOverride, value ?? new());
        }

        /// <summary>
        /// Project override for shader pipelines.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for shader pipelines (technical).")]
        public OverrideableSetting<bool> AllowShaderPipelinesOverride
        {
            get => Rendering.OpenGL.AllowProgramPipelinesOverride;
            set => Rendering.OpenGL.AllowProgramPipelinesOverride = value ?? new();
        }

        /// <summary>
        /// Project override for skeletal skinning.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for skeletal skinning (technical).")]
        public OverrideableSetting<bool> AllowSkinningOverride
        {
            get => Rendering.Technical.AllowSkinningOverride;
            set => Rendering.Technical.AllowSkinningOverride = value ?? new();
        }

        /// <summary>
        /// Project override for integer weighting IDs.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for integer weighting IDs (technical).")]
        public OverrideableSetting<bool> UseIntegerWeightingIdsOverride
        {
            get => Rendering.Technical.UseIntegerWeightingIdsOverride;
            set => Rendering.Technical.UseIntegerWeightingIdsOverride = value ?? new();
        }

        /// <summary>
        /// Project override for child matrix recalculation loop type.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for child matrix recalculation loop type (technical).")]
        public OverrideableSetting<ELoopType> RecalcChildMatricesLoopTypeOverride
        {
            get => Rendering.Technical.RecalcChildMatricesLoopTypeOverride;
            set => Rendering.Technical.RecalcChildMatricesLoopTypeOverride = value ?? new();
        }

        /// <summary>
        /// Project override for skinned mesh bounds recompute policy.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for skinned mesh bounds recompute policy (technical).")]
        public OverrideableSetting<ESkinnedBoundsRecomputePolicy> SkinnedBoundsRecomputePolicyOverride
        {
            get => Rendering.Technical.SkinnedBoundsRecomputePolicyOverride;
            set => Rendering.Technical.SkinnedBoundsRecomputePolicyOverride = value ?? new();
        }

        /// <summary>
        /// Project override for allowing one initial runtime skinned-bounds build while the Never policy is active.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for allowing one initial runtime skinned-bounds build while the Never policy is active (technical).")]
        public OverrideableSetting<bool> AllowInitialSkinnedBoundsBuildWhenNeverOverride
        {
            get => Rendering.Technical.AllowInitialSkinnedBoundsBuildWhenNeverOverride;
            set => Rendering.Technical.AllowInitialSkinnedBoundsBuildWhenNeverOverride = value ?? new();
        }

        /// <summary>
        /// Project override for compute shader skinning.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for compute shader skinning (technical).")]
        public OverrideableSetting<bool> CalculateSkinningInComputeShaderOverride
        {
            get => Rendering.Technical.CalculateSkinningInComputeShaderOverride;
            set => Rendering.Technical.CalculateSkinningInComputeShaderOverride = value ?? new();
        }

        /// <summary>
        /// Project override for compute shader blendshapes.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for compute shader blendshapes (technical).")]
        public OverrideableSetting<bool> CalculateBlendshapesInComputeShaderOverride
        {
            get => Rendering.Technical.CalculateBlendshapesInComputeShaderOverride;
            set => Rendering.Technical.CalculateBlendshapesInComputeShaderOverride = value ?? new();
        }

        /// <summary>
        /// Project override for detail-preserving compute mipmap generation.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// Unsupported formats and non-2D paths fall back to standard GL mip generation.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for detail-preserving compute mipmap generation on eligible OpenGL 2D textures (technical).")]
        public OverrideableSetting<bool> UseDetailPreservingComputeMipmapsOverride
        {
            get => Rendering.OpenGL.UseDetailPreservingComputeMipmapsOverride;
            set => Rendering.OpenGL.UseDetailPreservingComputeMipmapsOverride = value ?? new();
        }

        /// <summary>
        /// Game override for audio transport backend.
        /// When set, the game requires a specific transport regardless of user preference.
        /// Can be further overridden by editor preferences for dev/testing.
        /// </summary>
        [Category("Audio Overrides")]
        [Description("Game override for audio transport backend (e.g. game requires NAudio).")]
        public OverrideableSetting<EAudioTransport> AudioTransportOverride
        {
            get => _audioTransportOverride;
            set => SetField(ref _audioTransportOverride, value ?? new());
        }

        /// <summary>
        /// Game override for audio effects processor.
        /// When set, the game requires a specific effects pipeline regardless of user preference.
        /// Can be further overridden by editor preferences for dev/testing.
        /// </summary>
        [Category("Audio Overrides")]
        [Description("Game override for audio effects processor (e.g. game requires SteamAudio).")]
        public OverrideableSetting<EAudioEffects> AudioEffectsOverride
        {
            get => _audioEffectsOverride;
            set => SetField(ref _audioEffectsOverride, value ?? new());
        }

        /// <summary>
        /// Game override for the V2 streaming audio architecture.
        /// When set, overrides the user's preference for V2 architecture.
        /// </summary>
        [Category("Audio Overrides")]
        [Description("Game override for V2 streaming audio architecture.")]
        public OverrideableSetting<bool> AudioArchitectureV2Override
        {
            get => _audioArchitectureV2Override;
            set => SetField(ref _audioArchitectureV2Override, value ?? new());
        }

        /// <summary>
        /// Game override for audio sample rate.
        /// When set, overrides the user's preferred sample rate (e.g. 48000 for high-fidelity audio games).
        /// </summary>
        [Category("Audio Overrides")]
        [Description("Game override for audio sample rate in Hz.")]
        public OverrideableSetting<int> AudioSampleRateOverride
        {
            get => _audioSampleRateOverride;
            set => SetField(ref _audioSampleRateOverride, value ?? new());
        }

        #endregion
    }
}

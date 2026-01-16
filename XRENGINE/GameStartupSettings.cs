using MemoryPack;
using System.ComponentModel;
using XREngine.Components.Scene.Transforms;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine
{
    /// <summary>
    /// Project-level game configuration settings.
    /// Contains startup, networking, and build configuration.
    /// Also includes optional overrides for engine-level settings (Project > Engine cascade).
    /// </summary>
    [MemoryPackable]
    public partial class GameStartupSettings : XRAsset
    {
        private BuildSettings _buildSettings = new();
        private ENetworkingType _networkingType = ENetworkingType.Local;
        private List<GameWindowStartupSettings> _startupWindows = [];
        private ETwoPlayerPreference _twoPlayerViewportPreference;
        private EThreePlayerPreference _threePlayerViewportPreference;
        private bool _logOutputToFile = true;
        private UserSettings _defaultUserSettings = new();
        private string _texturesFolder = "";
        private float? _targetUpdatesPerSecond = 90.0f;
        private float _fixedFramesPerSecond = 90.0f;
        private float? _targetFramesPerSecond = 90.0f;
        private float? _unfocusedTargetFramesPerSecond = null;
        private bool _runVRInPlace = true;
        private bool _gpuRenderDispatch = false;

        private string _udpMulticastGroupIP = "239.0.0.222";
        private int _udpMulticastPort = 5000;
        //private string _tcpListenerIP = "0.0.0.0";
        //private int _tcpListenerPort = 5001;
        private string _serverIP = "127.0.0.1";

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
        public enum ENetworkingType
        {
            /// <summary>
            /// The application is a server.
            /// Clients will connect to this server.
            /// </summary>
            Server,
            /// <summary>
            /// The application is a client.
            /// The client will connect to a server.
            /// </summary>
            Client,
            /// <summary>
            /// The application is a peer-to-peer client.
            /// The client will connect to other peer-to-peer clients.
            /// </summary>
            P2PClient,
            /// <summary>
            /// The application is a local client.
            /// No network connection is used.
            /// </summary>
            Local,
        }

        [Category("Networking")]
        [Description("Determines whether the application runs as Server, Client, P2P Client, or Local-only.")]
        public ENetworkingType NetworkingType
        {
            get => _networkingType;
            set => SetField(ref _networkingType, value);
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
        private OverrideableSetting<bool> _useGpuBvhOverride = new();
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
        private OverrideableSetting<bool> _enableIntelXessOverride = new();
        private OverrideableSetting<EXessQualityMode> _xessQualityOverride = new();

        // Project > Engine only (technical, not user-facing)
        private OverrideableSetting<bool> _allowShaderPipelinesOverride = new();
        private OverrideableSetting<bool> _useIntegerWeightingIdsOverride = new();
        private OverrideableSetting<ELoopType> _recalcChildMatricesLoopTypeOverride = new();
        private OverrideableSetting<bool> _calculateSkinningInComputeShaderOverride = new();
        private OverrideableSetting<bool> _calculateBlendshapesInComputeShaderOverride = new();

        private OverrideableSetting<float> _transformReplicationKeyframeIntervalSecOverride = new();
        private OverrideableSetting<float> _timeBetweenReplicationsOverride = new();

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
        /// Project override for shader pipelines.
        /// Takes precedence over engine defaults when HasOverride is true.
        /// Technical setting not typically exposed to end users.
        /// </summary>
        [Category("Technical Overrides")]
        [Description("Project override for shader pipelines (technical).")]
        public OverrideableSetting<bool> AllowShaderPipelinesOverride
        {
            get => _allowShaderPipelinesOverride;
            set => SetField(ref _allowShaderPipelinesOverride, value ?? new());
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
            get => _useIntegerWeightingIdsOverride;
            set => SetField(ref _useIntegerWeightingIdsOverride, value ?? new());
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
            get => _recalcChildMatricesLoopTypeOverride;
            set => SetField(ref _recalcChildMatricesLoopTypeOverride, value ?? new());
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
            get => _calculateSkinningInComputeShaderOverride;
            set => SetField(ref _calculateSkinningInComputeShaderOverride, value ?? new());
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
            get => _calculateBlendshapesInComputeShaderOverride;
            set => SetField(ref _calculateBlendshapesInComputeShaderOverride, value ?? new());
        }

        #endregion
    }
}

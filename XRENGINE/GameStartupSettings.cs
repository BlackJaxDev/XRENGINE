using MemoryPack;
using XREngine.Components.Scene.Transforms;
using XREngine.Core.Files;
using XREngine.Data.Rendering;

namespace XREngine
{
    [MemoryPackable]
    public partial class GameStartupSettings : XRAsset
    {
        private BuildSettings _buildSettings = new();
        private ENetworkingType _networkingType = ENetworkingType.Local;
        private List<GameWindowStartupSettings> _startupWindows = [];
        private ETwoPlayerPreference _twoPlayerViewportPreference;
        private EThreePlayerPreference _threePlayerViewportPreference;
        private EOutputVerbosity _outputVerbosity = EOutputVerbosity.Verbose;
        private bool _logOutputToFile = true;
        private bool _useIntegerWeightingIds = true;
        private UserSettings _defaultUserSettings = new();
        private string _texturesFolder = "";
        private float? _targetUpdatesPerSecond = 90.0f;
        private float _fixedFramesPerSecond = 90.0f;
        private bool _runVRInPlace = false;

        private int? _jobWorkers = null;
        private int? _jobWorkerCap = null;
        private int? _jobQueueLimit = null;
        private int? _jobQueueWarningThreshold = null;

        private string _udpMulticastGroupIP = "239.0.0.222";
        private int _udpMulticastPort = 5000;
        //private string _tcpListenerIP = "0.0.0.0";
        //private int _tcpListenerPort = 5001;
        private string _serverIP = "127.0.0.1";

        public List<GameWindowStartupSettings> StartupWindows
        {
            get => _startupWindows;
            set => SetField(ref _startupWindows, value);
        }
        public EOutputVerbosity OutputVerbosity
        {
            get => _outputVerbosity;
            set => SetField(ref _outputVerbosity, value);
        }
        public bool LogOutputToFile
        {
            get => _logOutputToFile;
            set => SetField(ref _logOutputToFile, value);
        }
        public bool UseIntegerWeightingIds
        {
            get => _useIntegerWeightingIds;
            set => SetField(ref _useIntegerWeightingIds, value);
        }
        public UserSettings DefaultUserSettings
        {
            get => _defaultUserSettings;
            set => SetField(ref _defaultUserSettings, value);
        }
        public ETwoPlayerPreference TwoPlayerViewportPreference
        {
            get => _twoPlayerViewportPreference;
            set => SetField(ref _twoPlayerViewportPreference, value);
        }
        public EThreePlayerPreference ThreePlayerViewportPreference
        {
            get => _threePlayerViewportPreference;
            set => SetField(ref _threePlayerViewportPreference, value);
        }
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
        public ENetworkingType NetworkingType
        {
            get => _networkingType;
            set => SetField(ref _networkingType, value);
        }
        public string UdpMulticastGroupIP
        {
            get => _udpMulticastGroupIP;
            set => SetField(ref _udpMulticastGroupIP, value);
        }
        public int UdpMulticastPort
        {
            get => _udpMulticastPort;
            set => SetField(ref _udpMulticastPort, value);
        }
        private int _udpClientReceivePort = 0;
        public int UdpClientRecievePort
        {
            get => _udpClientReceivePort;
            set => SetField(ref _udpClientReceivePort, value);
        }
        private int _udpServerSendPort = 5000;
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
        public string ServerIP
        {
            get => _serverIP;
            set => SetField(ref _serverIP, value);
        }
        public float? TargetUpdatesPerSecond
        {
            get => _targetUpdatesPerSecond;
            set => SetField(ref _targetUpdatesPerSecond, value);
        }
        public float FixedFramesPerSecond
        {
            get => _fixedFramesPerSecond;
            set => SetField(ref _fixedFramesPerSecond, value);
        }
        /// <summary>
        /// If true, the VR system will start in the same application as the game itself.
        /// This means VR cannot be turned off without restarting the game.
        /// </summary>
        public bool RunVRInPlace
        {
            get => _runVRInPlace;
            set => SetField(ref _runVRInPlace, value);
        }

        /// <summary>
        /// Optional override for the number of job worker threads. If null, defaults are used.
        /// </summary>
        public int? JobWorkers
        {
            get => _jobWorkers;
            set => SetField(ref _jobWorkers, value);
        }

        /// <summary>
        /// Optional cap for the maximum number of job worker threads.
        /// </summary>
        public int? JobWorkerCap
        {
            get => _jobWorkerCap;
            set => SetField(ref _jobWorkerCap, value);
        }

        /// <summary>
        /// Optional limit on queued jobs; if null, the JobManager default or environment override is used.
        /// </summary>
        public int? JobQueueLimit
        {
            get => _jobQueueLimit;
            set => SetField(ref _jobQueueLimit, value);
        }

        /// <summary>
        /// Optional threshold at which queue length warnings are emitted.
        /// </summary>
        public int? JobQueueWarningThreshold
        {
            get => _jobQueueWarningThreshold;
            set => SetField(ref _jobQueueWarningThreshold, value);
        }
        public Dictionary<int, string> LayerNames { get; set; } = DefaultLayers.All;

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
        public EMaxMirrorRecursionCount MaxMirrorRecursionCount { get; set; } = EMaxMirrorRecursionCount.Eight;
    }
}
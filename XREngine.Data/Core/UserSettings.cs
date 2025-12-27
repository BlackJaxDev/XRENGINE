using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Data.Vectors;

namespace XREngine
{
    /// <summary>
    /// User-changeable preferences that persist per-user (not per-project).
    /// Contains display, quality, and audio preference settings.
    /// Also includes optional overrides for project/engine-level settings (User > Project > Engine cascade).
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class UserSettings : XRBase
    {
        private EWindowState _windowState = EWindowState.Windowed;
        private EVSyncMode _vSyncMode = EVSyncMode.Adaptive;
        private EEngineQuality _textureQuality = EEngineQuality.Highest;
        private EEngineQuality _modelQuality = EEngineQuality.Highest;
        private EEngineQuality _soundQuality = EEngineQuality.Highest;

        //Preferred libraries - will use whichever is available if the preferred one is not.
        private ERenderLibrary _renderLibrary = ERenderLibrary.OpenGL;
        private EAudioLibrary _audioLibrary = EAudioLibrary.OpenAL;
        private EPhysicsLibrary _physicsLibrary = EPhysicsLibrary.PhysX;

        private float? _targetFramesPerSecond = 90.0f;
        private IVector2 _windowedResolution = new(1920, 1080);
        private bool _disableAudioOnDefocus = false;
        private float _audioDisableFadeSeconds = 0.5f;
        private float? _unfocusedTargetFramesPerSecond = null;
        private EGlobalIlluminationMode _globalIlluminationMode = EGlobalIlluminationMode.LightProbesAndIbl;

        public EVSyncMode VSync
        {
            get => _vSyncMode;
            set => SetField(ref _vSyncMode, value);
        }
        public EEngineQuality TextureQuality
        {
            get => _textureQuality;
            set => SetField(ref _textureQuality, value);
        }
        public EEngineQuality ModelQuality
        {
            get => _modelQuality;
            set => SetField(ref _modelQuality, value);
        }
        public EEngineQuality SoundQuality
        {
            get => _soundQuality;
            set => SetField(ref _soundQuality, value);
        }
        public ERenderLibrary RenderLibrary
        {
            get => _renderLibrary;
            set => SetField(ref _renderLibrary, value);
        }
        public EAudioLibrary AudioLibrary
        {
            get => _audioLibrary;
            set => SetField(ref _audioLibrary, value);
        }
        public EPhysicsLibrary PhysicsLibrary
        {
            get => _physicsLibrary;
            set => SetField(ref _physicsLibrary, value);
        }
        public float? TargetFramesPerSecond
        {
            get => _targetFramesPerSecond;
            set => SetField(ref _targetFramesPerSecond, value);
        }
        public bool DisableAudioOnDefocus
        {
            get => _disableAudioOnDefocus;
            set => SetField(ref _disableAudioOnDefocus, value);
        }
        public float AudioDisableFadeSeconds
        {
            get => _audioDisableFadeSeconds;
            set => SetField(ref _audioDisableFadeSeconds, value);
        }
        public float? UnfocusedTargetFramesPerSecond
        {
            get => _unfocusedTargetFramesPerSecond;
            set => SetField(ref _unfocusedTargetFramesPerSecond, value);
        }
        public EGlobalIlluminationMode GlobalIlluminationMode
        {
            get => _globalIlluminationMode;
            set => SetField(ref _globalIlluminationMode, value);
        }
        public IVector2 WindowedResolution
        {
            get => _windowedResolution;
            set => SetField(ref _windowedResolution, value);
        }
        public EWindowState WindowState
        {
            get => _windowState;
            set => SetField(ref _windowState, value);
        }

        #region Overrideable Settings (User > Project > Engine cascade)

        private OverrideableSetting<int> _jobWorkersOverride = new();
        private OverrideableSetting<int> _jobWorkerCapOverride = new();
        private OverrideableSetting<int> _jobQueueLimitOverride = new();
        private OverrideableSetting<int> _jobQueueWarningThresholdOverride = new();
        private OverrideableSetting<bool> _gpuRenderDispatchOverride = new();
        private OverrideableSetting<EOutputVerbosity> _outputVerbosityOverride = new();
        private OverrideableSetting<bool> _enableGpuIndirectDebugLoggingOverride = new();
        private OverrideableSetting<bool> _enableGpuIndirectCpuFallbackOverride = new();

        // Full cascade settings (User > Project > Engine)
        private OverrideableSetting<EAntiAliasingMode> _antiAliasingModeOverride = new();
        private OverrideableSetting<uint> _msaaSampleCountOverride = new();
        private OverrideableSetting<EVSyncMode> _vSyncOverride = new();
        private OverrideableSetting<EGlobalIlluminationMode> _globalIlluminationModeOverride = new();
        private OverrideableSetting<bool> _tickGroupedItemsInParallelOverride = new();
        private OverrideableSetting<bool> _enableNvidiaDlssOverride = new();
        private OverrideableSetting<EDlssQualityMode> _dlssQualityOverride = new();
        private OverrideableSetting<bool> _enableIntelXessOverride = new();
        private OverrideableSetting<EXessQualityMode> _xessQualityOverride = new();

        // User > Project only (project defines base, user can override)
        private OverrideableSetting<float> _targetUpdatesPerSecondOverride = new();
        private OverrideableSetting<float> _fixedFramesPerSecondOverride = new();

        /// <summary>
        /// User override for the number of job worker threads.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("User override for the number of job worker threads.")]
        public OverrideableSetting<int> JobWorkersOverride
        {
            get => _jobWorkersOverride;
            set => SetField(ref _jobWorkersOverride, value ?? new());
        }

        /// <summary>
        /// User override for the maximum job worker thread cap.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("User override for the maximum job worker thread cap.")]
        public OverrideableSetting<int> JobWorkerCapOverride
        {
            get => _jobWorkerCapOverride;
            set => SetField(ref _jobWorkerCapOverride, value ?? new());
        }

        /// <summary>
        /// User override for the job queue limit.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("User override for the job queue limit.")]
        public OverrideableSetting<int> JobQueueLimitOverride
        {
            get => _jobQueueLimitOverride;
            set => SetField(ref _jobQueueLimitOverride, value ?? new());
        }

        /// <summary>
        /// User override for the job queue warning threshold.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("User override for the job queue warning threshold.")]
        public OverrideableSetting<int> JobQueueWarningThresholdOverride
        {
            get => _jobQueueWarningThresholdOverride;
            set => SetField(ref _jobQueueWarningThresholdOverride, value ?? new());
        }

        /// <summary>
        /// User override for GPU-driven render dispatch.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("User override for GPU-driven render dispatch.")]
        public OverrideableSetting<bool> GPURenderDispatchOverride
        {
            get => _gpuRenderDispatchOverride;
            set => SetField(ref _gpuRenderDispatchOverride, value ?? new());
        }

        /// <summary>
        /// User override for output verbosity level.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("User override for output verbosity level.")]
        public OverrideableSetting<EOutputVerbosity> OutputVerbosityOverride
        {
            get => _outputVerbosityOverride;
            set => SetField(ref _outputVerbosityOverride, value ?? new());
        }

        /// <summary>
        /// User override for GPU indirect debug logging.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Debug Overrides")]
        [Description("User override for GPU indirect debug logging.")]
        public OverrideableSetting<bool> EnableGpuIndirectDebugLoggingOverride
        {
            get => _enableGpuIndirectDebugLoggingOverride;
            set => SetField(ref _enableGpuIndirectDebugLoggingOverride, value ?? new());
        }

        /// <summary>
        /// User override for GPU indirect CPU fallback.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Debug Overrides")]
        [Description("User override for GPU indirect CPU fallback.")]
        public OverrideableSetting<bool> EnableGpuIndirectCpuFallbackOverride
        {
            get => _enableGpuIndirectCpuFallbackOverride;
            set => SetField(ref _enableGpuIndirectCpuFallbackOverride, value ?? new());
        }

        /// <summary>
        /// User override for anti-aliasing mode.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("User override for anti-aliasing mode.")]
        public OverrideableSetting<EAntiAliasingMode> AntiAliasingModeOverride
        {
            get => _antiAliasingModeOverride;
            set => SetField(ref _antiAliasingModeOverride, value ?? new());
        }

        /// <summary>
        /// User override for MSAA sample count.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("User override for MSAA sample count.")]
        public OverrideableSetting<uint> MsaaSampleCountOverride
        {
            get => _msaaSampleCountOverride;
            set => SetField(ref _msaaSampleCountOverride, value ?? new());
        }

        /// <summary>
        /// User override for VSync mode.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("User override for VSync mode.")]
        public OverrideableSetting<EVSyncMode> VSyncOverride
        {
            get => _vSyncOverride;
            set => SetField(ref _vSyncOverride, value ?? new());
        }

        /// <summary>
        /// User override for global illumination mode.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("User override for global illumination mode.")]
        public OverrideableSetting<EGlobalIlluminationMode> GlobalIlluminationModeOverride
        {
            get => _globalIlluminationModeOverride;
            set => SetField(ref _globalIlluminationModeOverride, value ?? new());
        }

        /// <summary>
        /// User override for parallel tick processing.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("User override for parallel tick processing.")]
        public OverrideableSetting<bool> TickGroupedItemsInParallelOverride
        {
            get => _tickGroupedItemsInParallelOverride;
            set => SetField(ref _tickGroupedItemsInParallelOverride, value ?? new());
        }

        /// <summary>
        /// User override for NVIDIA DLSS.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("User override for NVIDIA DLSS.")]
        public OverrideableSetting<bool> EnableNvidiaDlssOverride
        {
            get => _enableNvidiaDlssOverride;
            set => SetField(ref _enableNvidiaDlssOverride, value ?? new());
        }

        /// <summary>
        /// User override for DLSS quality mode.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("User override for DLSS quality mode.")]
        public OverrideableSetting<EDlssQualityMode> DlssQualityOverride
        {
            get => _dlssQualityOverride;
            set => SetField(ref _dlssQualityOverride, value ?? new());
        }

        /// <summary>
        /// User override for Intel XeSS.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("User override for Intel XeSS.")]
        public OverrideableSetting<bool> EnableIntelXessOverride
        {
            get => _enableIntelXessOverride;
            set => SetField(ref _enableIntelXessOverride, value ?? new());
        }

        /// <summary>
        /// User override for XeSS quality mode.
        /// Takes precedence over project and engine defaults when HasOverride is true.
        /// </summary>
        [Category("Quality Overrides")]
        [Description("User override for XeSS quality mode.")]
        public OverrideableSetting<EXessQualityMode> XessQualityOverride
        {
            get => _xessQualityOverride;
            set => SetField(ref _xessQualityOverride, value ?? new());
        }

        /// <summary>
        /// User override for target updates per second.
        /// Takes precedence over project setting when HasOverride is true.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("User override for target updates per second (game logic tick rate).")]
        public OverrideableSetting<float> TargetUpdatesPerSecondOverride
        {
            get => _targetUpdatesPerSecondOverride;
            set => SetField(ref _targetUpdatesPerSecondOverride, value ?? new());
        }

        /// <summary>
        /// User override for fixed frames per second.
        /// Takes precedence over project setting when HasOverride is true.
        /// </summary>
        [Category("Performance Overrides")]
        [Description("User override for fixed frames per second (physics tick rate).")]
        public OverrideableSetting<float> FixedFramesPerSecondOverride
        {
            get => _fixedFramesPerSecondOverride;
            set => SetField(ref _fixedFramesPerSecondOverride, value ?? new());
        }

        #endregion
    }
}

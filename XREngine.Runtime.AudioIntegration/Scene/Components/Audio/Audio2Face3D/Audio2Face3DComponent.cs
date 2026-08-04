using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Reflection.Attributes;
using XREngine.Data;
using XREngine.Data.Components;

namespace XREngine.Components
{
    /// <summary>
    /// Drives blendshapes from a CSV exported by NVIDIA Audio2Face-3D sample tooling.
    /// This component mirrors the scene setup pattern used by <see cref="OVRLipSyncComponent"/>,
    /// but consumes precomputed Audio2Face-3D animation frames instead of a local native runtime.
    /// </summary>
    [XRComponentEditor("XREngine.Editor.ComponentEditors.Audio2Face3DComponentEditor")]
    public sealed class Audio2Face3DComponent : XRComponent
    {
        /// <summary>
        /// Defines the time window in ticks during which recent audio activity is considered valid for playback control.
        /// </summary>
        private static readonly long AudioActivityWindowTicks = RuntimeAudioIntegrationServices.SecondsToElapsedTicks(0.25f);
        /// <summary>
        /// Defines the characters used to separate multiple blendshape targets in the emotion target strings.
        /// </summary>
        private static readonly char[] EmotionTargetSeparators = [',', ';', '|'];

        /// <summary>
        /// Defines an object used for synchronizing access to live frame data received from a connected Audio2Face-3D live adapter.
        /// </summary>
        private readonly object _liveFrameSync = new();
        /// <summary>
        /// Gets or sets the <see cref="AudioSourceComponent"/> that will provide audio data for this component.
        /// </summary>
        private AudioSourceComponent? _audioSource;
        /// <summary>
        /// Gets or sets the <see cref="ModelComponent"/> that will receive blendshape weight updates from this component.
        /// </summary>
        private ModelComponent? _modelComponent;
        /// <summary>
        /// Gets or sets the source of blendshape data for this component.
        /// EAudio2Face3DSourceMode.CsvPlayback: Reads from the CSV specified in AnimationCsvPath.
        /// EAudio2Face3DSourceMode.LiveStream: Connects to a live Audio2Face-3D adapter and receives blendshape data in real-time.
        /// </summary>
        private EAudio2Face3DSourceMode _sourceMode;
        /// <summary>
        /// Gets or sets the path to the CSV file exported by NVIDIA Audio2Face-3D sample tooling.
        /// </summary>
        private string _animationCsvPath = string.Empty;
        /// <summary>
        /// Gets or sets the endpoint URL used by an externally registered Audio2Face-3D live adapter.
        /// Format: "http://hostname:port" or "https://hostname:port"
        /// </summary>
        private string _liveEndpoint = "http://127.0.0.1:50051";
        /// <summary>
        /// Gets or sets the prefix string that will be prepended to each blendshape name before applying weights to the <see cref="ModelComponent"/>.
        /// </summary>
        private string _blendshapeNamePrefix = string.Empty;
        /// <summary>
        /// Gets or sets the suffix string that will be appended to each blendshape name before applying weights to the <see cref="ModelComponent"/>.
        /// </summary>
        private string _blendshapeNameSuffix = string.Empty;
        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports anger.
        /// </summary>
        private string _angryBlendshapeTargets = $"{ARKitBlendshapeNames.BrowDownLeft},{ARKitBlendshapeNames.BrowDownRight},{ARKitBlendshapeNames.NoseSneerLeft},{ARKitBlendshapeNames.NoseSneerRight}";
        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports disgust.
        /// </summary>
        private string _disgustBlendshapeTargets = $"{ARKitBlendshapeNames.NoseSneerLeft},{ARKitBlendshapeNames.NoseSneerRight},{ARKitBlendshapeNames.MouthUpperUpLeft},{ARKitBlendshapeNames.MouthUpperUpRight}";
        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports fear.
        /// </summary>
        private string _fearBlendshapeTargets = $"{ARKitBlendshapeNames.EyeWideLeft},{ARKitBlendshapeNames.EyeWideRight},{ARKitBlendshapeNames.MouthStretchLeft},{ARKitBlendshapeNames.MouthStretchRight}";
        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports happiness.
        /// </summary>
        private string _happyBlendshapeTargets = $"{ARKitBlendshapeNames.MouthSmileLeft},{ARKitBlendshapeNames.MouthSmileRight},{ARKitBlendshapeNames.CheekSquintLeft},{ARKitBlendshapeNames.CheekSquintRight}";
        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports neutral emotion.
        /// </summary>
        private string _neutralBlendshapeTargets = string.Empty;
        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports sadness.
        /// </summary>
        private string _sadBlendshapeTargets = $"{ARKitBlendshapeNames.MouthFrownLeft},{ARKitBlendshapeNames.MouthFrownRight},{ARKitBlendshapeNames.BrowInnerUp}";
        /// <summary>
        /// Gets or sets the speed at which input weights are smoothed toward target weights. Higher values result in faster smoothing.
        /// </summary>
        private float _inputSmoothSpeed = 12.0f;
        /// <summary>
        /// Gets or sets the multiplier applied to all input weights before they are applied to the <see cref="ModelComponent"/>.
        /// </summary>
        private float _weightMultiplier = 1.0f;
        /// <summary>
        /// Gets or sets the speed at which emotion weights are smoothed toward target weights. Higher values result in faster smoothing.
        /// </summary>
        private float _emotionSmoothSpeed = 8.0f;
        /// <summary>
        /// Gets or sets the multiplier applied to all emotion weights before they are applied to the <see cref="ModelComponent"/>.
        /// </summary>
        private float _emotionWeightMultiplier = 0.75f;
        /// <summary>
        /// Gets or sets the speed at which weights are reset to zero when no audio data has been received for a short period of time.
        /// </summary>
        private float _silenceResetSpeed = 10.0f;
        /// <summary>
        /// Gets or sets a value indicating whether playback should automatically start when audio data is received.
        /// </summary>
        private bool _autoPlayOnAudio = true;
        /// <summary>
        /// Gets or sets a value indicating whether the component should automatically attempt to connect to a live Audio2Face-3D adapter when activated.
        /// </summary>
        private bool _autoConnectLiveOnActivation = true;
        /// <summary>
        /// Gets or sets a value indicating whether playback should loop when the end of the animation is reached.
        /// </summary>
        private bool _loop;
        /// <summary>
        /// Gets or sets a value indicating whether the animation should be reloaded from the CSV file when the component is activated.
        /// </summary>
        private bool _reloadOnActivation = true;
        /// <summary>
        /// Gets or sets the last time in ticks when audio data was received from the associated <see cref="AudioSourceComponent"/>.
        /// </summary>
        private long _lastAudioTicks;
        /// <summary>
        /// Gets or sets the last time in ticks when live frame data was received from a connected Audio2Face-3D live adapter.
        /// </summary>
        private long _lastLiveFrameTicks;
        /// <summary>
        /// Gets or sets the last time in ticks when live emotion data was received from a connected Audio2Face-3D live adapter.
        /// </summary>
        private long _lastLiveEmotionTicks;
        /// <summary>
        /// Gets or sets the current playback time of the animation from the loaded CSV file.
        /// </summary>
        private float _playbackTime;
        /// <summary>
        /// Indicates whether the component is currently playing back an animation from the loaded CSV file.
        /// </summary>
        private bool _isPlaying;
        /// <summary>
        /// Indicates whether the component is currently connected to a live Audio2Face-3D adapter.
        /// </summary>
        private bool _isLiveConnected;
        /// <summary>
        /// Gets or sets the loaded animation data parsed from the CSV file specified in <see cref="AnimationCsvPath"/>.
        /// </summary>
        private Audio2Face3DAnimation? _animation;
        /// <summary>
        /// Gets or sets the target blendshape weights that will be applied to the <see cref="ModelComponent"/> after mapping from source blendshape names.
        /// </summary>
        private float[]? _targetWeights;
        /// <summary>
        /// Gets or sets the applied blendshape weights that are currently being applied to the <see cref="ModelComponent"/>.
        /// </summary>
        private float[]? _appliedWeights;
        /// <summary>
        /// Gets or sets the target emotion weights that will be applied to the <see cref="ModelComponent"/> after mapping from source blendshape names.
        /// </summary>
        private float[]? _targetEmotionWeights;
        /// <summary>
        /// Gets or sets the applied emotion weights that are currently being applied to the <see cref="ModelComponent"/>.
        /// </summary>
        private float[]? _appliedEmotionWeights;
        /// <summary>
        /// Gets or sets the live blendshape names received from a connected Audio2Face-3D live adapter.
        /// </summary>
        private string[]? _liveBlendshapeNames;
        /// <summary>
        /// Gets or sets the live blendshape weights received from a connected Audio2Face-3D live adapter.
        /// </summary>
        private float[]? _liveWeights;
        /// <summary>
        /// Gets or sets the live emotion weights received from a connected Audio2Face-3D live adapter.
        /// </summary>
        private float[]? _liveEmotionWeights;
        /// <summary>
        /// Gets or sets the cached list of source blendshape names that will be used when applying weights to the <see cref="ModelComponent"/>.
        /// </summary>
        private string[][] _emotionTargetNames = CreateEmotionTargetNameCache();
        /// <summary>
        /// Gets or sets the cached list of output blendshape names that will be used when applying weights to the <see cref="ModelComponent"/>.
        /// </summary>
        private string[] _outputBlendshapeNames = [];
        /// <summary>
        /// Gets or sets the cached mapping of source blendshape names to output blendshape names, used for efficient weight application.
        /// </summary>
        private string[]? _mappedSourceBlendshapeNames;
        /// <summary>
        /// Gets or sets the cached indices of output blendshapes corresponding to each source blendshape, used for efficient weight application.
        /// </summary>
        private int[] _sourceOutputIndices = [];
        /// <summary>
        /// Gets or sets the cached indices of output blendshapes corresponding to each emotion, used for efficient weight application.
        /// </summary>
        private int[][] _emotionOutputIndices = CreateEmotionOutputIndexCache();
        /// <summary>
        /// Gets or sets the output weights that will be applied to the <see cref="ModelComponent"/> after mapping from source blendshape names.
        /// </summary>
        private float[] _outputWeights = [];
        /// <summary>
        /// Indicates whether the mapping between source blendshape names and output blendshape names needs to be recalculated.
        /// </summary>
        private bool _outputBlendshapeMappingDirty = true;

        public Audio2Face3DComponent()
        {
            RefreshAllEmotionTargetCaches();
        }

        /// <summary>
        /// Gets the <see cref="AudioSourceComponent"/> that will provide audio data for this component.
        /// </summary>
        /// <returns>The <see cref="AudioSourceComponent"/> that will provide audio data for this component.</returns>
        public AudioSourceComponent? GetAudioSource()
            => AudioSource ?? GetSiblingComponent<AudioSourceComponent>(false);

        /// <summary>
        /// Gets or sets the <see cref="AudioSourceComponent"/> that will provide audio data for this component.
        /// </summary>
        public AudioSourceComponent? AudioSource
        {
            get => _audioSource;
            set => SetField(ref _audioSource, value);
        }

        /// <summary>
        /// Gets the <see cref="ModelComponent"/> that will receive blendshape weight updates from this component.
        /// </summary>
        /// <returns>The <see cref="ModelComponent"/> that will receive blendshape weight updates from this component.</returns>
        public ModelComponent? GetModelComponent() 
            => ModelComponent ?? GetSiblingComponent<ModelComponent>(false);

        /// <summary>
        /// Gets or sets the <see cref="ModelComponent"/> that will receive blendshape weight updates from this component.
        /// </summary>
        public ModelComponent? ModelComponent
        {
            get => _modelComponent;
            set => SetField(ref _modelComponent, value);
        }

        /// <summary>
        /// Specifies the source of blendshape data for this component. 
        /// When set to <see cref="EAudio2Face3DSourceMode.CsvPlayback"/>, 
        /// the component will read from the CSV specified in <see cref="AnimationCsvPath"/>. 
        /// When set to <see cref="EAudio2Face3DSourceMode.LiveStream"/>, 
        /// the component will attempt to connect to a live Audio2Face-3D adapter and receive blendshape data in real-time.
        /// </summary>
        [DefaultValue(EAudio2Face3DSourceMode.CsvPlayback)]
        public EAudio2Face3DSourceMode SourceMode
        {
            get => _sourceMode;
            set
            {
                if (!SetField(ref _sourceMode, value))
                    return;

                ResetRuntimeState(clearWeights: true, disconnectLiveClient: value != EAudio2Face3DSourceMode.LiveStream);
                if (!IsActive)
                    return;

                if (value == EAudio2Face3DSourceMode.CsvPlayback)
                {
                    if (ReloadOnActivation)
                        ReloadAnimation();
                }
                else if (AutoConnectLiveOnActivation)
                {
                    TryConnectLiveClient();
                }
            }
        }

        /// <summary>
        /// Gets or sets the path to the CSV file exported by NVIDIA Audio2Face-3D sample tooling.
        /// </summary>
        [Description("Path to the animation_frames.csv exported by NVIDIA Audio2Face-3D sample tooling.")]
        [InspectorPath(
            InspectorPathKind.File, 
            InspectorPathFormat.Both, 
            DialogMode = InspectorPathDialogMode.Open, 
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", 
            Title = "Choose Audio2Face Animation CSV")]
        public string AnimationCsvPath
        {
            get => _animationCsvPath;
            set
            {
                string path = value?.Trim() ?? string.Empty;
                if (!SetField(ref _animationCsvPath, path))
                    return;

                ClearAnimationState(clearWeights: SourceMode == EAudio2Face3DSourceMode.CsvPlayback);
                if (IsActive && ReloadOnActivation && SourceMode == EAudio2Face3DSourceMode.CsvPlayback)
                    ReloadAnimation();
            }
        }

        /// <summary>
        /// Gets or sets the endpoint URL used by an externally registered Audio2Face-3D live adapter.
        /// Format: "http://hostname:port" or "https://hostname:port"
        /// </summary>
        [Description("Endpoint used by an externally registered Audio2Face-3D live adapter. Format: \"http://hostname:port\" or \"https://hostname:port\"")]
        public string LiveEndpoint
        {
            get => _liveEndpoint;
            set => SetField(ref _liveEndpoint, value?.Trim() ?? string.Empty);
        }

        /// <summary>
        /// Gets or sets the prefix string that will be prepended to each blendshape name before applying weights to the <see cref="ModelComponent"/>.
        /// </summary>
        public string BlendshapeNamePrefix
        {
            get => _blendshapeNamePrefix;
            set
            {
                if (SetField(ref _blendshapeNamePrefix, value ?? string.Empty))
                    InvalidateOutputBlendshapeMapping(clearCurrentWeights: true);
            }
        }

        /// <summary>
        /// Gets or sets the suffix string that will be appended to each blendshape name before applying weights to the <see cref="ModelComponent"/>.
        /// </summary>
        public string BlendshapeNameSuffix
        {
            get => _blendshapeNameSuffix;
            set
            {
                if (SetField(ref _blendshapeNameSuffix, value ?? string.Empty))
                    InvalidateOutputBlendshapeMapping(clearCurrentWeights: true);
            }
        }

        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports anger.
        /// </summary>
        [Description("Comma-separated blendshape targets used when Audio2Emotion reports anger.")]
        public string AngryBlendshapeTargets
        {
            get => _angryBlendshapeTargets;
            set => SetEmotionTargetString(ref _angryBlendshapeTargets, value, EAudio2Face3DEmotion.Angry);
        }

        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports disgust.
        /// </summary>
        [Description("Comma-separated blendshape targets used when Audio2Emotion reports disgust.")]
        public string DisgustBlendshapeTargets
        {
            get => _disgustBlendshapeTargets;
            set => SetEmotionTargetString(ref _disgustBlendshapeTargets, value, EAudio2Face3DEmotion.Disgust);
        }

        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports fear.
        /// </summary>
        [Description("Comma-separated blendshape targets used when Audio2Emotion reports fear.")]
        public string FearBlendshapeTargets
        {
            get => _fearBlendshapeTargets;
            set => SetEmotionTargetString(ref _fearBlendshapeTargets, value, EAudio2Face3DEmotion.Fear);
        }

        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports happiness.
        /// </summary>
        [Description("Comma-separated blendshape targets used when Audio2Emotion reports happiness.")]
        public string HappyBlendshapeTargets
        {
            get => _happyBlendshapeTargets;
            set => SetEmotionTargetString(ref _happyBlendshapeTargets, value, EAudio2Face3DEmotion.Happy);
        }

        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports neutral emotion.
        /// </summary>
        [Description("Comma-separated blendshape targets used when Audio2Emotion reports neutral emotion.")]
        public string NeutralBlendshapeTargets
        {
            get => _neutralBlendshapeTargets;
            set => SetEmotionTargetString(ref _neutralBlendshapeTargets, value, EAudio2Face3DEmotion.Neutral);
        }

        /// <summary>
        /// Gets or sets the comma-separated list of blendshape targets that will be used when Audio2Emotion reports sadness.
        /// </summary>
        [Description("Comma-separated blendshape targets used when Audio2Emotion reports sadness.")]
        public string SadBlendshapeTargets
        {
            get => _sadBlendshapeTargets;
            set => SetEmotionTargetString(ref _sadBlendshapeTargets, value, EAudio2Face3DEmotion.Sad);
        }

        /// <summary>
        /// Gets or sets the speed at which input weights are smoothed toward target weights. Higher values result in faster smoothing.
        /// </summary>
        [Range(0.0f, 50.0f)]
        public float InputSmoothSpeed
        {
            get => _inputSmoothSpeed;
            set => SetField(ref _inputSmoothSpeed, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the multiplier applied to all input weights before they are applied to the <see cref="ModelComponent"/>.
        /// </summary>
        [Range(0.0f, 4.0f)]
        public float WeightMultiplier
        {
            get => _weightMultiplier;
            set => SetField(ref _weightMultiplier, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the speed at which emotion weights are smoothed toward target weights. Higher values result in faster smoothing.
        /// </summary>
        [Range(0.0f, 50.0f)]
        public float EmotionSmoothSpeed
        {
            get => _emotionSmoothSpeed;
            set => SetField(ref _emotionSmoothSpeed, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the multiplier applied to all emotion weights before they are applied to the <see cref="ModelComponent"/>.
        /// </summary>
        [Range(0.0f, 4.0f)]
        public float EmotionWeightMultiplier
        {
            get => _emotionWeightMultiplier;
            set => SetField(ref _emotionWeightMultiplier, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the speed at which weights are reset to zero when no audio data has been received for a short period of time. 
        /// Higher values result in faster resetting.
        /// </summary>
        [Range(0.0f, 50.0f)]
        public float SilenceResetSpeed
        {
            get => _silenceResetSpeed;
            set => SetField(ref _silenceResetSpeed, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets a value indicating whether playback should automatically start when audio data is received.
        /// </summary>
        [DefaultValue(true)]
        public bool AutoPlayOnAudio
        {
            get => _autoPlayOnAudio;
            set => SetField(ref _autoPlayOnAudio, value);
        }

        /// <summary>
        /// Gets or sets a value indicating whether the component should automatically attempt to connect to a live Audio2Face-3D adapter when activated.
        /// </summary>
        [DefaultValue(true)]
        public bool AutoConnectLiveOnActivation
        {
            get => _autoConnectLiveOnActivation;
            set => SetField(ref _autoConnectLiveOnActivation, value);
        }

        /// <summary>
        /// Gets or sets a value indicating whether playback should loop when the end of the animation is reached.
        /// </summary>
        [DefaultValue(false)]
        public bool Loop
        {
            get => _loop;
            set => SetField(ref _loop, value);
        }

        /// <summary>
        /// Gets or sets a value indicating whether the animation should be reloaded from the CSV file when the component is activated.
        /// </summary>
        [DefaultValue(true)]
        public bool ReloadOnActivation
        {
            get => _reloadOnActivation;
            set => SetField(ref _reloadOnActivation, value);
        }

        /// <summary>
        /// Gets a value indicating whether the component is currently playing back an animation.
        /// </summary>
        [Browsable(false)]
        public bool IsPlaying => _isPlaying;

        /// <summary>
        /// Gets a value indicating whether the component is currently connected to a live Audio2Face-3D adapter.
        /// </summary>
        [Browsable(false)]
        public bool IsLiveConnected => _isLiveConnected;

        /// <summary>
        /// Gets the current playback time in seconds when in CSV playback mode. Returns 0.0f when not playing or when in live stream mode.
        /// </summary>
        [Browsable(false)]
        public float PlaybackTime => _playbackTime;

        /// <summary>
        /// Gets the duration of the loaded animation in seconds when in CSV playback mode. 
        /// Returns 0.0f when no animation is loaded or when in live stream mode.
        /// </summary>
        [Browsable(false)]
        public float Duration => SourceMode == EAudio2Face3DSourceMode.CsvPlayback ? _animation?.Duration ?? 0.0f : 0.0f;

        /// <summary>
        /// Gets the number of blendshapes defined in the loaded animation when in CSV playback mode.
        /// </summary>
        [Browsable(false)]
        public int BlendshapeCount => GetActiveBlendshapeNames()?.Length ?? 0;

        /// <summary>
        /// Gets the number of emotions defined in the loaded animation when in CSV playback mode.
        /// </summary>
        [Browsable(false)]
        public int EmotionCurveCount => SourceMode == EAudio2Face3DSourceMode.CsvPlayback
            ? _animation?.EmotionCount ?? 0
            : _liveEmotionWeights is null ? 0 : Audio2Face3DRegistry.Count;

        /// <summary>
        /// Gets the last error message encountered when attempting to load an animation from CSV. 
        /// Returns an empty string if no error has occurred.
        /// </summary>
        [Browsable(false)]
        public string LastLoadError { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the last error message encountered when attempting to connect to a live Audio2Face-3D adapter. 
        /// Returns an empty string if no error has occurred.
        /// </summary>
        [Browsable(false)]
        public string LastLiveError { get; private set; } = string.Empty;

        /// <summary>
        /// Determines whether recent audio data has been received within the defined activity window.
        /// </summary>
        /// <param name="currentTicks">The current time in ticks.</param>
        /// <param name="lastAudioTicks">The time in ticks when the last audio data was received.</param>
        /// <returns>True if recent audio data has been received; otherwise, false.</returns>
        internal static bool HasRecentAudioData(long currentTicks, long lastAudioTicks)
            => Math.Max(0L, currentTicks - lastAudioTicks) < AudioActivityWindowTicks;

        /// <summary>
        /// Calculates a smoothing factor based on the elapsed time and the specified smoothing speed.
        /// </summary>
        /// <param name="deltaSeconds">The elapsed time in seconds.</param>
        /// <param name="smoothingSpeed">The smoothing speed factor.</param>
        /// <returns>A smoothing factor clamped between 0.0 and 1.0.</returns>
        internal static float GetSmoothingFactor(float deltaSeconds, float smoothingSpeed)
            => Math.Clamp(deltaSeconds * smoothingSpeed, 0.0f, 1.0f);

        /// <summary>
        /// Resolves the full path to the animation CSV file based on the provided path and optional project or current directory.
        /// </summary>
        /// <param name="animationCsvPath">The relative or absolute path to the animation CSV file.</param>
        /// <param name="projectDirectory">The optional project directory to resolve relative paths against.</param>
        /// <param name="currentDirectory">The optional current directory to resolve relative paths against if the project directory is not provided.</param>
        /// <returns>The resolved full path to the animation CSV file.</returns>
        public static string ResolveAnimationCsvPath(string animationCsvPath, string? projectDirectory, string? currentDirectory)
        {
            if (string.IsNullOrWhiteSpace(animationCsvPath))
                return string.Empty;

            if (Path.IsPathRooted(animationCsvPath))
                return Path.GetFullPath(animationCsvPath);

            string baseDirectory = !string.IsNullOrWhiteSpace(projectDirectory)
                ? projectDirectory
                : !string.IsNullOrWhiteSpace(currentDirectory)
                    ? currentDirectory
                    : Directory.GetCurrentDirectory();

            return Path.GetFullPath(animationCsvPath, baseDirectory);
        }

        /// <summary>
        /// Attempts to parse the provided CSV text into an <see cref="Audio2Face3DAnimation"/> instance.
        /// </summary>
        /// <param name="csvText">The CSV text to parse.</param>
        /// <param name="animation">The resulting <see cref="Audio2Face3DAnimation"/> instance if parsing is successful; otherwise, null.</param>
        /// <param name="error">The error message if parsing fails; otherwise, null.</param>
        /// <returns>True if parsing is successful; otherwise, false.</returns>
        internal static bool TryParseCsvText(string csvText, out Audio2Face3DAnimation? animation, out string? error)
            => Audio2Face3DAnimation.TryParse(csvText, out animation, out error);

        /// <summary>
        /// Parses the provided CSV text into an <see cref="Audio2Face3DAnimation"/> instance.
        /// </summary>
        /// <param name="csvText">The CSV text to parse.</param>
        /// <returns>The resulting <see cref="Audio2Face3DAnimation"/> instance.</returns>
        internal static Audio2Face3DAnimation ParseCsvText(string csvText)
            => Audio2Face3DAnimation.Parse(csvText);

        /// <summary>
        /// Attempts to reload the animation from the CSV file specified in <see cref="AnimationCsvPath"/>.
        /// </summary>
        /// <returns>True if the animation was successfully reloaded; otherwise, false.</returns>
        public bool ReloadAnimation()
        {
            if (string.IsNullOrWhiteSpace(AnimationCsvPath))
            {
                LastLoadError = "AnimationCsvPath is empty.";
                return false;
            }

            string resolvedPath = ResolveAnimationCsvPath(AnimationCsvPath, RuntimeAudioIntegrationServices.Current.ProjectDirectory, Directory.GetCurrentDirectory());

            if (!File.Exists(resolvedPath))
            {
                LastLoadError = $"Animation CSV not found: {resolvedPath}";
                return false;
            }

            try
            {
                string csvText = File.ReadAllText(resolvedPath);
                Audio2Face3DAnimation animation = ParseCsvText(csvText);
                SetAnimation(animation);
                LastLoadError = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                LastLoadError = ex.Message;
                Debug.AudioWarning($"[Audio2Face3D] Failed to load '{resolvedPath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to start playback of the loaded animation from the beginning.
        /// </summary>
        /// <returns>True if playback was successfully started; otherwise, false.</returns>
        public bool PlayFromStart()
        {
            if (SourceMode != EAudio2Face3DSourceMode.CsvPlayback)
                return false;

            if (_animation is null && !ReloadAnimation())
                return false;

            _playbackTime = 0.0f;
            _isPlaying = _animation is not null;
            return _isPlaying;
        }

        /// <summary>
        /// Stops playback of the loaded animation and optionally clears all applied blendshape weights.
        /// </summary>
        /// <param name="clearWeights">True to clear all applied blendshape weights; otherwise, false.</param>
        public void StopPlayback(bool clearWeights = true)
        {
            _isPlaying = false;
            _playbackTime = 0.0f;
            if (clearWeights)
                ClearAppliedWeights();
        }

        /// <summary>
        /// Called when the component is activated. 
        /// This method sets up event handlers for audio data reception, 
        /// initializes the model component, 
        /// and manages playback or live connection based on the source mode.
        /// </summary>
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            AudioSource = GetAudioSource();
            if (AudioSource is not null)
            {
                AudioSource.StreamingBufferEnqueuedByte += OnAudioDataReceived;
                AudioSource.StreamingBufferEnqueued += OnAudioDataReceived;
                AudioSource.StreamingBufferEnqueuedShort += OnAudioDataReceived;
                AudioSource.StreamingBufferEnqueuedFloat += OnAudioDataReceived;
            }
            else
            {
                Debug.Audio("[Audio2Face3D] No AudioSourceComponent found.");
            }

            ModelComponent = GetModelComponent();

            if (SourceMode == EAudio2Face3DSourceMode.CsvPlayback && ReloadOnActivation)
                ReloadAnimation();
            else if (SourceMode == EAudio2Face3DSourceMode.LiveStream && AutoConnectLiveOnActivation)
                TryConnectLiveClient();

            RegisterTick(ETickGroup.Late, ETickOrder.Animation, UpdateBlendshapes);
        }

        /// <summary>
        /// Called when the component is deactivated.
        /// </summary>
        protected override void OnComponentDeactivated()
        {
            if (AudioSource is not null)
            {
                AudioSource.StreamingBufferEnqueuedByte -= OnAudioDataReceived;
                AudioSource.StreamingBufferEnqueued -= OnAudioDataReceived;
                AudioSource.StreamingBufferEnqueuedShort -= OnAudioDataReceived;
                AudioSource.StreamingBufferEnqueuedFloat -= OnAudioDataReceived;
            }

            UnregisterTick(ETickGroup.Late, ETickOrder.Animation, UpdateBlendshapes);
            ResetRuntimeState(clearWeights: true, disconnectLiveClient: true);
            base.OnComponentDeactivated();
        }

        /// <summary>
        /// Called when audio data is received from the associated <see cref="AudioSourceComponent"/>.
        /// </summary>
        /// <param name="data">The audio data received from the <see cref="AudioSourceComponent"/>.</param>
        private void OnAudioDataReceived((int frequency, bool stereo, byte[] buffer) data)
        {
            if (data.buffer.Length != 0)
                MarkAudioActivity();
        }

        /// <summary>
        /// Called when audio data is received from the associated <see cref="AudioSourceComponent"/>.
        /// </summary>
        /// <param name="data">The audio data received from the <see cref="AudioSourceComponent"/>.</param>
        private void OnAudioDataReceived((int frequency, bool stereo, short[] buffer) data)
        {
            if (data.buffer.Length != 0)
                MarkAudioActivity();
        }

        /// <summary>
        /// Called when audio data is received from the associated <see cref="AudioSourceComponent"/>.
        /// </summary>
        /// <param name="data">The audio data received from the <see cref="AudioSourceComponent"/>.</param>
        private void OnAudioDataReceived((int frequency, bool stereo, float[] buffer) data)
        {
            if (data.buffer.Length != 0)
                MarkAudioActivity();
        }

        /// <summary>
        /// Called when audio data is received from the associated <see cref="AudioSourceComponent"/> in the form of an <see cref="XREngine.Data.AudioData"/> object.
        /// </summary>
        /// <param name="data">The audio data received from the <see cref="AudioSourceComponent"/>.</param>
        private void OnAudioDataReceived(XREngine.Data.AudioData data)
        {
            if (data.Data is not null)
                MarkAudioActivity();
        }

        /// <summary>
        /// Marks the current time as the last time audio data was received from the audio source.
        /// </summary>
        private void MarkAudioActivity()
        {
            if (SourceMode != EAudio2Face3DSourceMode.CsvPlayback)
                return;

            _lastAudioTicks = RuntimeAudioIntegrationServices.Current.ElapsedTicks;
            if (AutoPlayOnAudio && !_isPlaying)
                PlayFromStart();
        }

        /// <summary>
        /// Updates the blendshape weights on the associated <see cref="ModelComponent"/> based on the current source mode and playback state.
        /// </summary>
        private void UpdateBlendshapes()
        {
            ModelComponent? model = GetModelComponent();
            if (model is null)
                return;

            if (SourceMode == EAudio2Face3DSourceMode.LiveStream)
            {
                UpdateLiveBlendshapes(model);
                return;
            }

            if (_animation is null)
                return;

            bool hasRecentAudio = HasRecentAudioData(RuntimeAudioIntegrationServices.Current.ElapsedTicks, _lastAudioTicks);
            if (hasRecentAudio && _isPlaying)
            {
                AdvancePlaybackTime();
                if (_targetWeights is not null && _appliedWeights is not null)
                {
                    _animation.Sample(_playbackTime, _targetWeights);
                    SmoothTowardTarget(_targetWeights, _appliedWeights, GetSmoothingFactor(RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds, InputSmoothSpeed), WeightMultiplier);
                }

                if (_targetEmotionWeights is not null && _appliedEmotionWeights is not null)
                {
                    _animation.SampleEmotions(_playbackTime, _targetEmotionWeights);
                    SmoothTowardTarget(_targetEmotionWeights, _appliedEmotionWeights, GetSmoothingFactor(RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds, EmotionSmoothSpeed), EmotionWeightMultiplier);
                }
            }
            else
            {
                if (_appliedWeights is not null)
                    FadeOutAppliedWeights(_appliedWeights, GetSmoothingFactor(RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds, SilenceResetSpeed));
                if (_appliedEmotionWeights is not null)
                    FadeOutAppliedWeights(_appliedEmotionWeights, GetSmoothingFactor(RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds, SilenceResetSpeed));

                if (AreWeightsAtRest(_appliedWeights) && AreWeightsAtRest(_appliedEmotionWeights))
                {
                    _isPlaying = false;
                    _playbackTime = 0.0f;
                }
            }

            ApplyCombinedBlendshapeWeights(model, _animation.BlendshapeNames, _appliedWeights, _appliedEmotionWeights);
        }

        /// <summary>
        /// Updates the blendshape weights on the associated <see cref="ModelComponent"/> based on the latest live frame data received from a connected Audio2Face-3D live adapter.
        /// </summary>
        /// <param name="model">The model component whose blendshape weights will be updated.</param>
        private void UpdateLiveBlendshapes(ModelComponent model)
        {
            string[]? liveBlendshapeNames;
            float[]? liveWeights;
            float[]? liveEmotionWeights;
            lock (_liveFrameSync)
            {
                liveBlendshapeNames = _liveBlendshapeNames;
                liveWeights = _liveWeights;
                liveEmotionWeights = _liveEmotionWeights;
            }

            if (liveBlendshapeNames is not null && liveWeights is not null)
                EnsureAppliedWeightBuffer(liveWeights.Length);

            if (liveEmotionWeights is not null)
                EnsureEmotionWeightBuffers();

            if (_appliedWeights is null && _appliedEmotionWeights is null)
                return;

            bool hasRecentLiveFrame = liveBlendshapeNames is not null && liveWeights is not null && HasRecentAudioData(RuntimeAudioIntegrationServices.Current.ElapsedTicks, _lastLiveFrameTicks);
            if (_appliedWeights is not null)
            {
                if (hasRecentLiveFrame && liveWeights is not null)
                    SmoothTowardTarget(liveWeights, _appliedWeights, GetSmoothingFactor(RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds, InputSmoothSpeed), WeightMultiplier);
                else
                    FadeOutAppliedWeights(_appliedWeights, GetSmoothingFactor(RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds, SilenceResetSpeed));
            }

            bool hasRecentLiveEmotion = liveEmotionWeights is not null && HasRecentAudioData(RuntimeAudioIntegrationServices.Current.ElapsedTicks, _lastLiveEmotionTicks);
            if (_appliedEmotionWeights is not null)
            {
                if (hasRecentLiveEmotion && liveEmotionWeights is not null)
                    SmoothTowardTarget(liveEmotionWeights, _appliedEmotionWeights, GetSmoothingFactor(RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds, EmotionSmoothSpeed), EmotionWeightMultiplier);
                else
                    FadeOutAppliedWeights(_appliedEmotionWeights, GetSmoothingFactor(RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds, SilenceResetSpeed));
            }

            ApplyCombinedBlendshapeWeights(model, liveBlendshapeNames, _appliedWeights, _appliedEmotionWeights);
        }

        /// <summary>
        /// Advances the playback time of the loaded animation based on the elapsed time since the last update.
        /// </summary>
        private void AdvancePlaybackTime()
        {
            if (_animation is null)
                return;

            _playbackTime += RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds;
            if (_animation.Duration <= 0.0f)
            {
                _playbackTime = 0.0f;
                return;
            }

            if (Loop)
            {
                while (_playbackTime > _animation.Duration)
                    _playbackTime -= _animation.Duration;
            }
            else if (_playbackTime > _animation.Duration)
            {
                _playbackTime = _animation.Duration;
            }
        }

        /// <summary>
        /// Sets the current animation to the specified <see cref="Audio2Face3DAnimation"/> instance, 
        /// clearing any previously applied weights and initializing target and applied weight buffers for both blendshapes and emotions.
        /// </summary>
        /// <param name="animation">The animation to set as the current animation.</param>
        private void SetAnimation(Audio2Face3DAnimation animation)
        {
            if (_animation is not null)
                ClearAppliedWeights();

            _animation = animation;
            _targetWeights = animation.BlendshapeNames.Length == 0 ? null : new float[animation.BlendshapeNames.Length];
            _appliedWeights = animation.BlendshapeNames.Length == 0 ? null : new float[animation.BlendshapeNames.Length];
            _targetEmotionWeights = animation.EmotionCount == 0 ? null : new float[Audio2Face3DRegistry.Count];
            _appliedEmotionWeights = animation.EmotionCount == 0 ? null : new float[Audio2Face3DRegistry.Count];
            _playbackTime = 0.0f;
            _isPlaying = false;
            InvalidateOutputBlendshapeMapping(clearCurrentWeights: false);
        }

        /// <summary>
        /// Attempts to connect to a live Audio2Face-3D adapter using the registered adapter in <see cref="Audio2Face3DRegistry"/>.
        /// </summary>
        /// <returns>True if the connection was successful; otherwise, false.</returns>
        public bool TryConnectLiveClient()
        {
            if (SourceMode != EAudio2Face3DSourceMode.LiveStream)
            {
                LastLiveError = "SourceMode must be LiveStream before connecting a live client.";
                return false;
            }

            DisconnectLiveClient();

            IAudio2Face3DLiveClientAdapter? adapter = Audio2Face3DRegistry.Adapter;
            if (adapter is null)
            {
                LastLiveError = Audio2Face3DRegistry.MissingAdapterMessage;
                return false;
            }

            if (!adapter.TryConnect(this, out string? error))
            {
                _isLiveConnected = false;
                LastLiveError = string.IsNullOrWhiteSpace(error) ? "Audio2Face-3D live client failed to connect." : error;
                return false;
            }

            _isLiveConnected = true;
            LastLiveError = string.Empty;
            return true;
        }

        /// <summary>
        /// Disconnects from the currently connected live Audio2Face-3D adapter, if any.
        /// </summary>
        public void DisconnectLiveClient()
        {
            if (_isLiveConnected)
                Audio2Face3DRegistry.Adapter?.Disconnect(this);

            _isLiveConnected = false;
        }

        /// <summary>
        /// Marks the live client as connected, indicating that live frame data can be received and processed.
        /// </summary>
        public void MarkLiveClientConnected()
        {
            _isLiveConnected = true;
            LastLiveError = string.Empty;
        }

        /// <summary>
        /// Marks the live client as disconnected, optionally providing an error message indicating the reason for disconnection.
        /// </summary>
        /// <param name="error">The error message indicating the reason for disconnection, if any.</param>
        public void MarkLiveClientDisconnected(string? error = null)
        {
            _isLiveConnected = false;
            if (!string.IsNullOrWhiteSpace(error))
                LastLiveError = error;
        }

        /// <summary>
        /// Attempts to update the current live frame with the provided blendshape names and weights, 
        /// validating the input and ensuring that the internal state is updated accordingly.
        /// </summary>
        /// <param name="blendshapeNames">The list of blendshape names in the live frame.</param>
        /// <param name="weights">The corresponding weights for the blendshape names.</param>
        /// <param name="error">An error message if the update fails.</param>
        /// <returns>True if the live frame was successfully updated; otherwise, false.</returns>
        public bool TryUpdateLiveFrame(IReadOnlyList<string> blendshapeNames, IReadOnlyList<float> weights, out string? error)
        {
            error = null;
            if (blendshapeNames is null || blendshapeNames.Count == 0)
            {
                error = "Live frame must provide at least one blendshape name.";
                return false;
            }

            if (weights is null || weights.Count != blendshapeNames.Count)
            {
                error = "Live frame weight count must match the blendshape name count.";
                return false;
            }

            string[] copiedNames = new string[blendshapeNames.Count];
            float[] copiedWeights = new float[weights.Count];
            for (int i = 0; i < blendshapeNames.Count; i++)
            {
                string? blendshapeName = blendshapeNames[i];
                if (string.IsNullOrWhiteSpace(blendshapeName))
                {
                    error = $"Live frame blendshape name at index {i} is empty.";
                    return false;
                }

                copiedNames[i] = blendshapeName;
                copiedWeights[i] = Math.Clamp(weights[i], 0.0f, 1.0f);
            }

            bool sourceNamesChanged;
            lock (_liveFrameSync)
            {
                sourceNamesChanged = !AreSameNames(_liveBlendshapeNames, copiedNames);
                _liveBlendshapeNames = copiedNames;
                _liveWeights = copiedWeights;
            }

            EnsureAppliedWeightBuffer(copiedWeights.Length);
            _lastLiveFrameTicks = RuntimeAudioIntegrationServices.Current.ElapsedTicks;
            _isLiveConnected = true;
            LastLiveError = string.Empty;
            if (sourceNamesChanged)
                InvalidateOutputBlendshapeMapping(clearCurrentWeights: true);
            return true;
        }

        /// <summary>
        /// Attempts to update the current live emotion frame with the provided emotion names and weights,
        /// validating the input and ensuring that the internal state is updated accordingly.
        /// </summary>
        /// <param name="emotionNames">The list of emotion names in the live emotion frame.</param>
        /// <param name="weights">The corresponding weights for the emotion names.</param>
        /// <param name="error">An error message if the update fails.</param>
        /// <returns>True if the live emotion frame was successfully updated; otherwise, false.</returns>
        public bool TryUpdateLiveEmotionFrame(IReadOnlyList<string> emotionNames, IReadOnlyList<float> weights, out string? error)
        {
            error = null;
            if (emotionNames is null || emotionNames.Count == 0)
            {
                error = "Live emotion frame must provide at least one emotion name.";
                return false;
            }

            if (weights is null || weights.Count != emotionNames.Count)
            {
                error = "Live emotion weight count must match the emotion name count.";
                return false;
            }

            float[] mappedWeights = new float[Audio2Face3DRegistry.Count];
            for (int i = 0; i < emotionNames.Count; i++)
            {
                if (!Audio2Face3DRegistry.TryGetIndex(emotionNames[i], out int emotionIndex))
                {
                    error = $"Unsupported Audio2Emotion channel '{emotionNames[i]}'.";
                    return false;
                }

                mappedWeights[emotionIndex] = Math.Clamp(weights[i], 0.0f, 1.0f);
            }

            lock (_liveFrameSync)
                _liveEmotionWeights = mappedWeights;

            EnsureEmotionWeightBuffers();
            _lastLiveEmotionTicks = RuntimeAudioIntegrationServices.Current.ElapsedTicks;
            _isLiveConnected = true;
            LastLiveError = string.Empty;
            return true;
        }

        /// <summary>
        /// Clears the current animation state, including applied and target weights for both blendshapes and emotions, and resets playback state.
        /// </summary>
        /// <param name="clearWeights">Indicates whether to clear the applied weights.</param>
        private void ClearAnimationState(bool clearWeights)
        {
            if (clearWeights)
                ClearAppliedWeights();

            _animation = null;
            _targetWeights = null;
            _appliedWeights = null;
            _targetEmotionWeights = null;
            _appliedEmotionWeights = null;
            _isPlaying = false;
            _playbackTime = 0.0f;
            InvalidateOutputBlendshapeMapping(clearCurrentWeights: false);
        }

        /// <summary>
        /// Clears the current live state, including applied and target weights for both blendshapes and emotions, and resets live connection state.
        /// </summary>
        /// <param name="clearWeights">Indicates whether to clear the applied weights.</param>
        private void ClearLiveState(bool clearWeights)
        {
            if (clearWeights)
                ClearAppliedWeights();

            lock (_liveFrameSync)
            {
                _liveBlendshapeNames = null;
                _liveWeights = null;
                _liveEmotionWeights = null;
            }

            _lastLiveFrameTicks = 0L;
            _lastLiveEmotionTicks = 0L;
            _isLiveConnected = false;
            InvalidateOutputBlendshapeMapping(clearCurrentWeights: false);
        }

        /// <summary>
        /// Resets the runtime state of the component, stopping playback, clearing animation and live states, and optionally clearing applied weights and disconnecting from a live client.
        /// </summary>
        /// <param name="clearWeights">Indicates whether to clear the applied weights.</param>
        /// <param name="disconnectLiveClient">Indicates whether to disconnect from the live client.</param>
        private void ResetRuntimeState(bool clearWeights, bool disconnectLiveClient)
        {
            StopPlayback(clearWeights: clearWeights);
            ClearAnimationState(clearWeights: false);
            ClearLiveState(clearWeights: false);
            if (clearWeights)
                ClearAppliedWeights();
            if (disconnectLiveClient)
                DisconnectLiveClient();
        }

        /// <summary>
        /// Clears all applied and target weights for both blendshapes and emotions, resetting them to zero.
        /// </summary>
        private void ClearAppliedWeights()
        {
            ModelComponent? model = ModelComponent;
            if (model is null && SceneNode is not null)
                model = GetModelComponent();

            if (model is not null && _outputBlendshapeNames.Length > 0)
                for (int i = 0; i < _outputBlendshapeNames.Length; i++)
                    model.SetBlendShapeWeightNormalized(_outputBlendshapeNames[i], 0.0f);

            if (_appliedWeights is not null)
                Array.Clear(_appliedWeights, 0, _appliedWeights.Length);
            if (_targetWeights is not null)
                Array.Clear(_targetWeights, 0, _targetWeights.Length);
            if (_appliedEmotionWeights is not null)
                Array.Clear(_appliedEmotionWeights, 0, _appliedEmotionWeights.Length);
            if (_targetEmotionWeights is not null)
                Array.Clear(_targetEmotionWeights, 0, _targetEmotionWeights.Length);
            if (_outputWeights.Length > 0)
                Array.Clear(_outputWeights, 0, _outputWeights.Length);
        }

        /// <summary>
        /// Gets the active blendshape names based on the current source mode.
        /// </summary>
        /// <returns>The active blendshape names, or null if none are available.</returns>
        private string[]? GetActiveBlendshapeNames()
        {
            if (SourceMode == EAudio2Face3DSourceMode.LiveStream)
            {
                lock (_liveFrameSync)
                    return _liveBlendshapeNames;
            }

            return _animation?.BlendshapeNames;
        }

    
        private void EnsureAppliedWeightBuffer(int count)
        {
            if (_appliedWeights is null || _appliedWeights.Length != count)
                _appliedWeights = new float[count];
        }

        private void EnsureEmotionWeightBuffers()
        {
            if (_appliedEmotionWeights is null || _appliedEmotionWeights.Length != Audio2Face3DRegistry.Count)
                _appliedEmotionWeights = new float[Audio2Face3DRegistry.Count];
        }

        private void ApplyCombinedBlendshapeWeights(ModelComponent model, string[]? sourceBlendshapeNames, float[]? sourceWeights, float[]? emotionWeights)
        {
            EnsureOutputBlendshapeMapping(model, sourceBlendshapeNames);
            if (_outputBlendshapeNames.Length == 0)
                return;

            Array.Clear(_outputWeights, 0, _outputWeights.Length);

            if (sourceBlendshapeNames is not null && sourceWeights is not null)
                for (int i = 0; i < sourceWeights.Length && i < _sourceOutputIndices.Length; i++)
                    _outputWeights[_sourceOutputIndices[i]] = sourceWeights[i];

            if (emotionWeights is not null)
            {
                for (int emotionIndex = 0; emotionIndex < _emotionOutputIndices.Length && emotionIndex < emotionWeights.Length; emotionIndex++)
                {
                    float emotionWeight = emotionWeights[emotionIndex];
                    if (emotionWeight <= 0.0f)
                        continue;

                    int[] targets = _emotionOutputIndices[emotionIndex];
                    for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
                    {
                        int outputIndex = targets[targetIndex];
                        _outputWeights[outputIndex] = Math.Clamp(_outputWeights[outputIndex] + emotionWeight, 0.0f, 1.0f);
                    }
                }
            }

            for (int i = 0; i < _outputBlendshapeNames.Length; i++)
                model.SetBlendShapeWeightNormalized(_outputBlendshapeNames[i], _outputWeights[i]);
        }

        private string GetBlendshapeName(string sourceName)
        {
            bool hasPrefix = BlendshapeNamePrefix.Length > 0;
            bool hasSuffix = BlendshapeNameSuffix.Length > 0;
            if (!hasPrefix && !hasSuffix)
                return sourceName;
            if (hasPrefix && !hasSuffix)
                return string.Concat(BlendshapeNamePrefix, sourceName);
            if (!hasPrefix)
                return string.Concat(sourceName, BlendshapeNameSuffix);
            return string.Concat(BlendshapeNamePrefix, sourceName, BlendshapeNameSuffix);
        }

        /// <summary>
        /// Sets the target blendshape names for a specific emotion, updating the internal cache and invalidating the output blendshape mapping if the value has changed.
        /// </summary>
        /// <param name="backingField">The backing field for the target blendshape names.</param>
        /// <param name="value">The new target blendshape names as a single string.</param>
        /// <param name="emotion">The emotion for which to set the target blendshape names.</param>
        private void SetEmotionTargetString(ref string backingField, string? value, EAudio2Face3DEmotion emotion)
        {
            if (!SetField(ref backingField, value ?? string.Empty))
                return;

            RefreshEmotionTargetCache(emotion, backingField);
            InvalidateOutputBlendshapeMapping(clearCurrentWeights: true);
        }

        /// <summary>
        /// Refreshes the cached target blendshape names for all emotions based on their respective target strings, ensuring that the internal caches are up-to-date and unique.
        /// </summary>
        private void RefreshAllEmotionTargetCaches()
        {
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Angry, _angryBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Disgust, _disgustBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Fear, _fearBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Happy, _happyBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Neutral, _neutralBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Sad, _sadBlendshapeTargets);
        }

        /// <summary>
        /// Refreshes the cached target blendshape names for the specified emotion based on the provided target string, splitting it by the defined separators and ensuring uniqueness.
        /// </summary>
        /// <param name="emotion">The emotion for which to refresh the target cache.</param>
        /// <param name="targets">The target blendshape names as a single string, separated by the defined separators.</param>
        private void RefreshEmotionTargetCache(EAudio2Face3DEmotion emotion, string targets)
        {
            string[] parsed = targets.Split(EmotionTargetSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parsed.Length == 0)
            {
                _emotionTargetNames[(int)emotion] = [];
                return;
            }

            var uniqueTargets = new List<string>(parsed.Length);
            for (int i = 0; i < parsed.Length; i++)
            {
                string target = parsed[i];
                bool exists = false;
                for (int j = 0; j < uniqueTargets.Count; j++)
                {
                    if (string.Equals(uniqueTargets[j], target, StringComparison.Ordinal))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                    uniqueTargets.Add(target);
            }

            _emotionTargetNames[(int)emotion] = [.. uniqueTargets];
        }

        /// <summary>
        /// Invalidates the output blendshape mapping, optionally clearing the current applied weights. 
        /// This method is called when the source blendshape names or emotion target names change, ensuring that the output mapping is rebuilt on the next update.
        /// </summary>
        /// <param name="clearCurrentWeights">Whether to clear the current applied weights.</param>
        private void InvalidateOutputBlendshapeMapping(bool clearCurrentWeights)
        {
            if (clearCurrentWeights)
                ClearAppliedWeights();

            _outputBlendshapeMappingDirty = true;
        }

        /// <summary>
        /// Ensures that the output blendshape mapping is up-to-date based on the provided source blendshape names.
        /// </summary>
        /// <param name="model">The model component containing the blendshapes.</param>
        /// <param name="sourceBlendshapeNames">The source blendshape names to map to the output blendshape mapping.</param>
        private void EnsureOutputBlendshapeMapping(ModelComponent model, string[]? sourceBlendshapeNames)
        {
            if (_outputBlendshapeMappingDirty || !AreSameNames(_mappedSourceBlendshapeNames, sourceBlendshapeNames))
                RebuildOutputBlendshapeMapping(model, sourceBlendshapeNames);
        }

        /// <summary>
        /// Rebuilds the output blendshape mapping based on the provided source blendshape names and the cached emotion target names.
        /// </summary>
        /// <param name="model">The model component containing the blendshapes.</param>
        /// <param name="sourceBlendshapeNames">The source blendshape names to map to the output blendshape mapping.</param>
        private void RebuildOutputBlendshapeMapping(ModelComponent model, string[]? sourceBlendshapeNames)
        {
            string[] previousOutputNames = _outputBlendshapeNames;
            var outputNames = new List<string>((sourceBlendshapeNames?.Length ?? 0) + 16);

            int[] sourceOutputIndices = sourceBlendshapeNames is null ? [] : new int[sourceBlendshapeNames.Length];
            if (sourceBlendshapeNames is not null)
                for (int i = 0; i < sourceBlendshapeNames.Length; i++)
                    sourceOutputIndices[i] = GetOrAddOutputIndex(outputNames, GetBlendshapeName(sourceBlendshapeNames[i]));

            int[][] emotionOutputIndices = CreateEmotionOutputIndexCache();
            for (int emotionIndex = 0; emotionIndex < _emotionTargetNames.Length; emotionIndex++)
            {
                string[] targets = _emotionTargetNames[emotionIndex];
                if (targets.Length == 0)
                    continue;

                int[] indices = new int[targets.Length];
                for (int i = 0; i < targets.Length; i++)
                    indices[i] = GetOrAddOutputIndex(outputNames, GetBlendshapeName(targets[i]));

                emotionOutputIndices[emotionIndex] = indices;
            }

            string[] nextOutputNames = [.. outputNames];
            ClearRemovedOutputNames(model, previousOutputNames, nextOutputNames);

            _outputBlendshapeNames = nextOutputNames;
            _mappedSourceBlendshapeNames = sourceBlendshapeNames is null ? null : [.. sourceBlendshapeNames];
            _sourceOutputIndices = sourceOutputIndices;
            _emotionOutputIndices = emotionOutputIndices;
            _outputWeights = new float[_outputBlendshapeNames.Length];
            _outputBlendshapeMappingDirty = false;
        }

        /// <summary>
        /// Clears any blendshape weights on the model that were previously applied but are no longer present in the next set of output names.
        /// </summary>
        /// <param name="model">The model component containing the blendshapes.</param>
        /// <param name="previousOutputNames">The previous set of output blendshape names.</param>
        /// <param name="nextOutputNames">The next set of output blendshape names.</param>
        private static void ClearRemovedOutputNames(ModelComponent model, string[] previousOutputNames, string[] nextOutputNames)
        {
            for (int i = 0; i < previousOutputNames.Length; i++)
                if (!ContainsName(nextOutputNames, previousOutputNames[i]))
                    model.SetBlendShapeWeightNormalized(previousOutputNames[i], 0.0f);
        }

        /// <summary>
        /// Determines whether the specified target name is present in the provided array of names, using ordinal string comparison.
        /// </summary>
        /// <param name="names">The array of names to search.</param>
        /// <param name="target">The target name to look for.</param>
        /// <returns>True if the target name is found in the array; otherwise, false.</returns>
        private static bool ContainsName(string[] names, string target)
        {
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(names[i], target, StringComparison.Ordinal))
                    return true;

            return false;
        }

        /// <summary>
        /// Determines whether two arrays of names are the same, using reference equality and ordinal string comparison for each element.
        /// </summary>
        /// <param name="left">The first array of names to compare.</param>
        /// <param name="right">The second array of names to compare.</param>
        /// <returns>True if the arrays are the same; otherwise, false.</returns>
        private static bool AreSameNames(string[]? left, string[]? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                    return false;

            return true;
        }

        /// <summary>
        /// Gets the index of the specified output name in the provided list of output names, adding it to the list if it does not already exist.
        /// </summary>
        /// <param name="outputNames">The list of output names to search and potentially add to.</param>
        /// <param name="name">The output name to find or add.</param>
        /// <returns>The index of the output name in the list.</returns>
        private static int GetOrAddOutputIndex(List<string> outputNames, string name)
        {
            for (int i = 0; i < outputNames.Count; i++)
                if (string.Equals(outputNames[i], name, StringComparison.Ordinal))
                    return i;

            outputNames.Add(name);
            return outputNames.Count - 1;
        }

        /// <summary>
        /// Creates a cache of target blendshape names for each emotion, initializing the arrays to empty arrays for each emotion index.
        /// </summary>
        /// <returns>A jagged array of target blendshape names for each emotion.</returns>
        private static string[][] CreateEmotionTargetNameCache()
        {
            string[][] result = new string[Audio2Face3DRegistry.Count][];
            for (int i = 0; i < result.Length; i++)
                result[i] = [];
            return result;
        }

        /// <summary>
        /// Creates a cache of output indices for each emotion, initializing the arrays to empty arrays for each emotion index.
        /// </summary>
        /// <returns>A jagged array of output indices for each emotion.</returns>
        private static int[][] CreateEmotionOutputIndexCache()
        {
            int[][] result = new int[Audio2Face3DRegistry.Count][];
            for (int i = 0; i < result.Length; i++)
                result[i] = [];
            return result;
        }

        /// <summary>
        /// Smoothly interpolates the applied weights toward the target weights based on the specified lerp amount and weight multiplier, ensuring that the applied weights remain within the range [0.0, 1.0].
        /// </summary>
        /// <param name="targetWeights">The target blendshape weights to interpolate toward.</param>
        /// <param name="appliedWeights">The currently applied blendshape weights that will be updated.</param>
        /// <param name="lerpAmount">The interpolation amount between the applied and target weights.</param>
        /// <param name="weightMultiplier">A multiplier applied to the target weights before interpolation.</param>
        private static void SmoothTowardTarget(float[] targetWeights, float[] appliedWeights, float lerpAmount, float weightMultiplier)
        {
            for (int i = 0; i < targetWeights.Length; i++)
            {
                float target = Math.Clamp(targetWeights[i] * weightMultiplier, 0.0f, 1.0f);
                appliedWeights[i] = Interp.Lerp(appliedWeights[i], target, lerpAmount);
            }
        }

        /// <summary>
        /// Smoothly fades out the applied weights toward zero based on the specified lerp amount, ensuring that the applied weights remain within the range [0.0, 1.0].
        /// </summary>
        /// <param name="appliedWeights">The currently applied blendshape weights that will be updated.</param>
        /// <param name="lerpAmount">The interpolation amount toward zero.</param>
        private static void FadeOutAppliedWeights(float[] appliedWeights, float lerpAmount)
        {
            for (int i = 0; i < appliedWeights.Length; i++)
                appliedWeights[i] = Interp.Lerp(appliedWeights[i], 0.0f, lerpAmount);
        }

        /// <summary>
        /// Determines whether the provided weights array is considered to be at rest, meaning that all weights are effectively zero (below a small threshold).
        /// </summary>
        /// <param name="weights">The array of blendshape weights to check.</param>
        /// <returns>True if all weights are effectively zero; otherwise, false.</returns>
        private static bool AreWeightsAtRest(float[]? weights)
        {
            if (weights is null || weights.Length == 0)
                return true;

            for (int i = 0; i < weights.Length; i++)
                if (weights[i] > 0.001f)
                    return false;

            return true;
        }
    }
}

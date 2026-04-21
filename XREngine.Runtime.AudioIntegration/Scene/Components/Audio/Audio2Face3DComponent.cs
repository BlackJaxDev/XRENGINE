using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Reflection.Attributes;
using XREngine.Data;
using XREngine.Data.Components;
using XREngine.Timers;

namespace XREngine.Components
{
    public enum EAudio2Face3DSourceMode
    {
        CsvPlayback,
        LiveStream,
    }

    public interface IAudio2Face3DLiveClientAdapter
    {
        bool TryConnect(Audio2Face3DComponent component, out string? error);
        void Disconnect(Audio2Face3DComponent component);
    }

    public enum EAudio2Face3DEmotion
    {
        Angry,
        Disgust,
        Fear,
        Happy,
        Neutral,
        Sad,
    }

    public static class Audio2Face3DRegistry
    {
        public const int Count = 6;

        public static readonly string[] Names =
        [
            "angry",
            "disgust",
            "fear",
            "happy",
            "neutral",
            "sad",
        ];

        public static bool TryGetIndex(string? emotionName, out int index)
        {
            if (string.IsNullOrWhiteSpace(emotionName))
            {
                index = -1;
                return false;
            }

            for (int i = 0; i < Names.Length; i++)
            {
                if (string.Equals(Names[i], emotionName, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }
        public const string MissingAdapterMessage = "No Audio2Face-3D live client adapter is registered. Add an Audio2Face3DNativeBridgeComponent beside the runtime component or register a custom adapter through Audio2Face3DRegistry.Adapter.";

        public static IAudio2Face3DLiveClientAdapter? Adapter { get; set; }
        public static bool HasAdapter => Adapter is not null;
    }

    /// <summary>
    /// Drives blendshapes from a CSV exported by NVIDIA Audio2Face-3D sample tooling.
    /// This component mirrors the scene setup pattern used by <see cref="OVRLipSyncComponent"/>,
    /// but consumes precomputed Audio2Face-3D animation frames instead of a local native runtime.
    /// </summary>
    [XRComponentEditor("XREngine.Editor.ComponentEditors.Audio2Face3DComponentEditor")]
    public sealed class Audio2Face3DComponent : XRComponent
    {
        private static readonly long AudioActivityWindowTicks = EngineTimer.SecondsToStopwatchTicks(0.25f);
        private static readonly char[] EmotionTargetSeparators = [',', ';', '|'];

        private readonly object _liveFrameSync = new();
        private AudioSourceComponent? _audioSource;
        private ModelComponent? _modelComponent;
        private EAudio2Face3DSourceMode _sourceMode;
        private string _animationCsvPath = string.Empty;
        private string _liveEndpoint = "http://127.0.0.1:50051";
        private string _blendshapeNamePrefix = string.Empty;
        private string _blendshapeNameSuffix = string.Empty;
        private string _angryBlendshapeTargets = $"{ARKitBlendshapeNames.BrowDownLeft},{ARKitBlendshapeNames.BrowDownRight},{ARKitBlendshapeNames.NoseSneerLeft},{ARKitBlendshapeNames.NoseSneerRight}";
        private string _disgustBlendshapeTargets = $"{ARKitBlendshapeNames.NoseSneerLeft},{ARKitBlendshapeNames.NoseSneerRight},{ARKitBlendshapeNames.MouthUpperUpLeft},{ARKitBlendshapeNames.MouthUpperUpRight}";
        private string _fearBlendshapeTargets = $"{ARKitBlendshapeNames.EyeWideLeft},{ARKitBlendshapeNames.EyeWideRight},{ARKitBlendshapeNames.MouthStretchLeft},{ARKitBlendshapeNames.MouthStretchRight}";
        private string _happyBlendshapeTargets = $"{ARKitBlendshapeNames.MouthSmileLeft},{ARKitBlendshapeNames.MouthSmileRight},{ARKitBlendshapeNames.CheekSquintLeft},{ARKitBlendshapeNames.CheekSquintRight}";
        private string _neutralBlendshapeTargets = string.Empty;
        private string _sadBlendshapeTargets = $"{ARKitBlendshapeNames.MouthFrownLeft},{ARKitBlendshapeNames.MouthFrownRight},{ARKitBlendshapeNames.BrowInnerUp}";
        private float _inputSmoothSpeed = 12.0f;
        private float _weightMultiplier = 1.0f;
        private float _emotionSmoothSpeed = 8.0f;
        private float _emotionWeightMultiplier = 0.75f;
        private float _silenceResetSpeed = 10.0f;
        private bool _autoPlayOnAudio = true;
        private bool _autoConnectLiveOnActivation = true;
        private bool _loop;
        private bool _reloadOnActivation = true;
        private long _lastAudioTicks;
        private long _lastLiveFrameTicks;
        private long _lastLiveEmotionTicks;
        private float _playbackTime;
        private bool _isPlaying;
        private bool _isLiveConnected;
        private Audio2Face3DAnimation? _animation;
        private float[]? _targetWeights;
        private float[]? _appliedWeights;
        private float[]? _targetEmotionWeights;
        private float[]? _appliedEmotionWeights;
        private string[]? _liveBlendshapeNames;
        private float[]? _liveWeights;
        private float[]? _liveEmotionWeights;
        private string[][] _emotionTargetNames = CreateEmotionTargetNameCache();
        private string[] _outputBlendshapeNames = [];
        private string[]? _mappedSourceBlendshapeNames;
        private int[] _sourceOutputIndices = [];
        private int[][] _emotionOutputIndices = CreateEmotionOutputIndexCache();
        private float[] _outputWeights = [];
        private bool _outputBlendshapeMappingDirty = true;

        public Audio2Face3DComponent()
        {
            RefreshAllEmotionTargetCaches();
        }

        public AudioSourceComponent? GetAudioSource() => AudioSource ?? GetSiblingComponent<AudioSourceComponent>(false);

        public AudioSourceComponent? AudioSource
        {
            get => _audioSource;
            set => SetField(ref _audioSource, value);
        }

        public ModelComponent? GetModelComponent() => ModelComponent ?? GetSiblingComponent<ModelComponent>(false);

        public ModelComponent? ModelComponent
        {
            get => _modelComponent;
            set => SetField(ref _modelComponent, value);
        }

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

        [Description("Path to the animation_frames.csv exported by NVIDIA Audio2Face-3D sample tooling.")]
        [InspectorPath(InspectorPathKind.File, InspectorPathFormat.Both, DialogMode = InspectorPathDialogMode.Open, Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", Title = "Choose Audio2Face Animation CSV")]
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

        [Description("Endpoint used by an externally registered Audio2Face-3D live adapter.")]
        public string LiveEndpoint
        {
            get => _liveEndpoint;
            set => SetField(ref _liveEndpoint, value?.Trim() ?? string.Empty);
        }

        public string BlendshapeNamePrefix
        {
            get => _blendshapeNamePrefix;
            set
            {
                if (SetField(ref _blendshapeNamePrefix, value ?? string.Empty))
                    InvalidateOutputBlendshapeMapping(clearCurrentWeights: true);
            }
        }

        public string BlendshapeNameSuffix
        {
            get => _blendshapeNameSuffix;
            set
            {
                if (SetField(ref _blendshapeNameSuffix, value ?? string.Empty))
                    InvalidateOutputBlendshapeMapping(clearCurrentWeights: true);
            }
        }

        [Description("Comma-separated blendshape targets used when Audio2Emotion reports anger.")]
        public string AngryBlendshapeTargets
        {
            get => _angryBlendshapeTargets;
            set => SetEmotionTargetString(ref _angryBlendshapeTargets, value, EAudio2Face3DEmotion.Angry);
        }

        [Description("Comma-separated blendshape targets used when Audio2Emotion reports disgust.")]
        public string DisgustBlendshapeTargets
        {
            get => _disgustBlendshapeTargets;
            set => SetEmotionTargetString(ref _disgustBlendshapeTargets, value, EAudio2Face3DEmotion.Disgust);
        }

        [Description("Comma-separated blendshape targets used when Audio2Emotion reports fear.")]
        public string FearBlendshapeTargets
        {
            get => _fearBlendshapeTargets;
            set => SetEmotionTargetString(ref _fearBlendshapeTargets, value, EAudio2Face3DEmotion.Fear);
        }

        [Description("Comma-separated blendshape targets used when Audio2Emotion reports happiness.")]
        public string HappyBlendshapeTargets
        {
            get => _happyBlendshapeTargets;
            set => SetEmotionTargetString(ref _happyBlendshapeTargets, value, EAudio2Face3DEmotion.Happy);
        }

        [Description("Comma-separated blendshape targets used when Audio2Emotion reports neutral emotion.")]
        public string NeutralBlendshapeTargets
        {
            get => _neutralBlendshapeTargets;
            set => SetEmotionTargetString(ref _neutralBlendshapeTargets, value, EAudio2Face3DEmotion.Neutral);
        }

        [Description("Comma-separated blendshape targets used when Audio2Emotion reports sadness.")]
        public string SadBlendshapeTargets
        {
            get => _sadBlendshapeTargets;
            set => SetEmotionTargetString(ref _sadBlendshapeTargets, value, EAudio2Face3DEmotion.Sad);
        }

        [Range(0.0f, 50.0f)]
        public float InputSmoothSpeed
        {
            get => _inputSmoothSpeed;
            set => SetField(ref _inputSmoothSpeed, Math.Max(0.0f, value));
        }

        [Range(0.0f, 4.0f)]
        public float WeightMultiplier
        {
            get => _weightMultiplier;
            set => SetField(ref _weightMultiplier, Math.Max(0.0f, value));
        }

        [Range(0.0f, 50.0f)]
        public float EmotionSmoothSpeed
        {
            get => _emotionSmoothSpeed;
            set => SetField(ref _emotionSmoothSpeed, Math.Max(0.0f, value));
        }

        [Range(0.0f, 4.0f)]
        public float EmotionWeightMultiplier
        {
            get => _emotionWeightMultiplier;
            set => SetField(ref _emotionWeightMultiplier, Math.Max(0.0f, value));
        }

        [Range(0.0f, 50.0f)]
        public float SilenceResetSpeed
        {
            get => _silenceResetSpeed;
            set => SetField(ref _silenceResetSpeed, Math.Max(0.0f, value));
        }

        [DefaultValue(true)]
        public bool AutoPlayOnAudio
        {
            get => _autoPlayOnAudio;
            set => SetField(ref _autoPlayOnAudio, value);
        }

        [DefaultValue(true)]
        public bool AutoConnectLiveOnActivation
        {
            get => _autoConnectLiveOnActivation;
            set => SetField(ref _autoConnectLiveOnActivation, value);
        }

        [DefaultValue(false)]
        public bool Loop
        {
            get => _loop;
            set => SetField(ref _loop, value);
        }

        [DefaultValue(true)]
        public bool ReloadOnActivation
        {
            get => _reloadOnActivation;
            set => SetField(ref _reloadOnActivation, value);
        }

        [Browsable(false)]
        public bool IsPlaying => _isPlaying;

        [Browsable(false)]
        public bool IsLiveConnected => _isLiveConnected;

        [Browsable(false)]
        public float PlaybackTime => _playbackTime;

        [Browsable(false)]
        public float Duration => SourceMode == EAudio2Face3DSourceMode.CsvPlayback ? _animation?.Duration ?? 0.0f : 0.0f;

        [Browsable(false)]
        public int BlendshapeCount => GetActiveBlendshapeNames()?.Length ?? 0;

        [Browsable(false)]
        public int EmotionCurveCount => SourceMode == EAudio2Face3DSourceMode.CsvPlayback
            ? _animation?.EmotionCount ?? 0
            : _liveEmotionWeights is null ? 0 : Audio2Face3DRegistry.Count;

        [Browsable(false)]
        public string LastLoadError { get; private set; } = string.Empty;

        [Browsable(false)]
        public string LastLiveError { get; private set; } = string.Empty;

        internal static bool HasRecentAudioData(long currentTicks, long lastAudioTicks)
            => Math.Max(0L, currentTicks - lastAudioTicks) < AudioActivityWindowTicks;

        internal static float GetSmoothingFactor(float deltaSeconds, float smoothingSpeed)
            => Math.Clamp(deltaSeconds * smoothingSpeed, 0.0f, 1.0f);

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

        internal static bool TryParseCsvText(string csvText, out Audio2Face3DAnimation? animation, out string? error)
            => Audio2Face3DAnimation.TryParse(csvText, out animation, out error);

        internal static Audio2Face3DAnimation ParseCsvText(string csvText)
            => Audio2Face3DAnimation.Parse(csvText);

        public bool ReloadAnimation()
        {
            if (string.IsNullOrWhiteSpace(AnimationCsvPath))
            {
                LastLoadError = "AnimationCsvPath is empty.";
                return false;
            }

            string resolvedPath = ResolveAnimationCsvPath(AnimationCsvPath, Engine.CurrentProject?.ProjectDirectory, Directory.GetCurrentDirectory());

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

        public void StopPlayback(bool clearWeights = true)
        {
            _isPlaying = false;
            _playbackTime = 0.0f;
            if (clearWeights)
                ClearAppliedWeights();
        }

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

        private void OnAudioDataReceived((int frequency, bool stereo, byte[] buffer) data)
        {
            if (data.buffer.Length == 0)
                return;

            MarkAudioActivity();
        }

        private void OnAudioDataReceived((int frequency, bool stereo, short[] buffer) data)
        {
            if (data.buffer.Length == 0)
                return;

            MarkAudioActivity();
        }

        private void OnAudioDataReceived((int frequency, bool stereo, float[] buffer) data)
        {
            if (data.buffer.Length == 0)
                return;

            MarkAudioActivity();
        }

        private void OnAudioDataReceived(XREngine.Data.AudioData data)
        {
            if (data.Data is null)
                return;

            MarkAudioActivity();
        }

        private void MarkAudioActivity()
        {
            if (SourceMode != EAudio2Face3DSourceMode.CsvPlayback)
                return;

            _lastAudioTicks = Engine.ElapsedTicks;
            if (AutoPlayOnAudio && !_isPlaying)
                PlayFromStart();
        }

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

            bool hasRecentAudio = HasRecentAudioData(Engine.ElapsedTicks, _lastAudioTicks);
            if (hasRecentAudio && _isPlaying)
            {
                AdvancePlaybackTime();
                if (_targetWeights is not null && _appliedWeights is not null)
                {
                    _animation.Sample(_playbackTime, _targetWeights);
                    SmoothTowardTarget(_targetWeights, _appliedWeights, GetSmoothingFactor(Engine.Delta, InputSmoothSpeed), WeightMultiplier);
                }

                if (_targetEmotionWeights is not null && _appliedEmotionWeights is not null)
                {
                    _animation.SampleEmotions(_playbackTime, _targetEmotionWeights);
                    SmoothTowardTarget(_targetEmotionWeights, _appliedEmotionWeights, GetSmoothingFactor(Engine.Delta, EmotionSmoothSpeed), EmotionWeightMultiplier);
                }
            }
            else
            {
                if (_appliedWeights is not null)
                    FadeOutAppliedWeights(_appliedWeights, GetSmoothingFactor(Engine.Delta, SilenceResetSpeed));
                if (_appliedEmotionWeights is not null)
                    FadeOutAppliedWeights(_appliedEmotionWeights, GetSmoothingFactor(Engine.Delta, SilenceResetSpeed));

                if (AreWeightsAtRest(_appliedWeights) && AreWeightsAtRest(_appliedEmotionWeights))
                {
                    _isPlaying = false;
                    _playbackTime = 0.0f;
                }
            }

            ApplyCombinedBlendshapeWeights(model, _animation.BlendshapeNames, _appliedWeights, _appliedEmotionWeights);
        }

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

            bool hasRecentLiveFrame = liveBlendshapeNames is not null && liveWeights is not null && HasRecentAudioData(Engine.ElapsedTicks, _lastLiveFrameTicks);
            if (_appliedWeights is not null)
            {
                if (hasRecentLiveFrame && liveWeights is not null)
                    SmoothTowardTarget(liveWeights, _appliedWeights, GetSmoothingFactor(Engine.Delta, InputSmoothSpeed), WeightMultiplier);
                else
                    FadeOutAppliedWeights(_appliedWeights, GetSmoothingFactor(Engine.Delta, SilenceResetSpeed));
            }

            bool hasRecentLiveEmotion = liveEmotionWeights is not null && HasRecentAudioData(Engine.ElapsedTicks, _lastLiveEmotionTicks);
            if (_appliedEmotionWeights is not null)
            {
                if (hasRecentLiveEmotion && liveEmotionWeights is not null)
                    SmoothTowardTarget(liveEmotionWeights, _appliedEmotionWeights, GetSmoothingFactor(Engine.Delta, EmotionSmoothSpeed), EmotionWeightMultiplier);
                else
                    FadeOutAppliedWeights(_appliedEmotionWeights, GetSmoothingFactor(Engine.Delta, SilenceResetSpeed));
            }

            ApplyCombinedBlendshapeWeights(model, liveBlendshapeNames, _appliedWeights, _appliedEmotionWeights);
        }

        private void AdvancePlaybackTime()
        {
            if (_animation is null)
                return;

            _playbackTime += Engine.Delta;
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

        public void DisconnectLiveClient()
        {
            if (_isLiveConnected)
                Audio2Face3DRegistry.Adapter?.Disconnect(this);

            _isLiveConnected = false;
        }

        public void MarkLiveClientConnected()
        {
            _isLiveConnected = true;
            LastLiveError = string.Empty;
        }

        public void MarkLiveClientDisconnected(string? error = null)
        {
            _isLiveConnected = false;
            if (!string.IsNullOrWhiteSpace(error))
                LastLiveError = error;
        }

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
            _lastLiveFrameTicks = Engine.ElapsedTicks;
            _isLiveConnected = true;
            LastLiveError = string.Empty;
            if (sourceNamesChanged)
                InvalidateOutputBlendshapeMapping(clearCurrentWeights: true);
            return true;
        }

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
            _lastLiveEmotionTicks = Engine.ElapsedTicks;
            _isLiveConnected = true;
            LastLiveError = string.Empty;
            return true;
        }

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

        private void ClearAppliedWeights()
        {
            ModelComponent? model = ModelComponent;
            if (model is null && SceneNode is not null)
                model = GetModelComponent();

            if (model is not null && _outputBlendshapeNames.Length > 0)
            {
                for (int i = 0; i < _outputBlendshapeNames.Length; i++)
                    model.SetBlendShapeWeightNormalized(_outputBlendshapeNames[i], 0.0f);
            }

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
            {
                for (int i = 0; i < sourceWeights.Length && i < _sourceOutputIndices.Length; i++)
                    _outputWeights[_sourceOutputIndices[i]] = sourceWeights[i];
            }

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

        private void SetEmotionTargetString(ref string backingField, string? value, EAudio2Face3DEmotion emotion)
        {
            if (!SetField(ref backingField, value ?? string.Empty))
                return;

            RefreshEmotionTargetCache(emotion, backingField);
            InvalidateOutputBlendshapeMapping(clearCurrentWeights: true);
        }

        private void RefreshAllEmotionTargetCaches()
        {
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Angry, _angryBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Disgust, _disgustBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Fear, _fearBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Happy, _happyBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Neutral, _neutralBlendshapeTargets);
            RefreshEmotionTargetCache(EAudio2Face3DEmotion.Sad, _sadBlendshapeTargets);
        }

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

        private void InvalidateOutputBlendshapeMapping(bool clearCurrentWeights)
        {
            if (clearCurrentWeights)
                ClearAppliedWeights();

            _outputBlendshapeMappingDirty = true;
        }

        private void EnsureOutputBlendshapeMapping(ModelComponent model, string[]? sourceBlendshapeNames)
        {
            if (!_outputBlendshapeMappingDirty && AreSameNames(_mappedSourceBlendshapeNames, sourceBlendshapeNames))
                return;

            RebuildOutputBlendshapeMapping(model, sourceBlendshapeNames);
        }

        private void RebuildOutputBlendshapeMapping(ModelComponent model, string[]? sourceBlendshapeNames)
        {
            string[] previousOutputNames = _outputBlendshapeNames;
            var outputNames = new List<string>((sourceBlendshapeNames?.Length ?? 0) + 16);

            int[] sourceOutputIndices = sourceBlendshapeNames is null ? [] : new int[sourceBlendshapeNames.Length];
            if (sourceBlendshapeNames is not null)
            {
                for (int i = 0; i < sourceBlendshapeNames.Length; i++)
                    sourceOutputIndices[i] = GetOrAddOutputIndex(outputNames, GetBlendshapeName(sourceBlendshapeNames[i]));
            }

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

        private static void ClearRemovedOutputNames(ModelComponent model, string[] previousOutputNames, string[] nextOutputNames)
        {
            for (int i = 0; i < previousOutputNames.Length; i++)
            {
                if (!ContainsName(nextOutputNames, previousOutputNames[i]))
                    model.SetBlendShapeWeightNormalized(previousOutputNames[i], 0.0f);
            }
        }

        private static bool ContainsName(string[] names, string target)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], target, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool AreSameNames(string[]? left, string[]? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static int GetOrAddOutputIndex(List<string> outputNames, string name)
        {
            for (int i = 0; i < outputNames.Count; i++)
            {
                if (string.Equals(outputNames[i], name, StringComparison.Ordinal))
                    return i;
            }

            outputNames.Add(name);
            return outputNames.Count - 1;
        }

        private static string[][] CreateEmotionTargetNameCache()
        {
            string[][] result = new string[Audio2Face3DRegistry.Count][];
            for (int i = 0; i < result.Length; i++)
                result[i] = [];
            return result;
        }

        private static int[][] CreateEmotionOutputIndexCache()
        {
            int[][] result = new int[Audio2Face3DRegistry.Count][];
            for (int i = 0; i < result.Length; i++)
                result[i] = [];
            return result;
        }

        private static void SmoothTowardTarget(float[] targetWeights, float[] appliedWeights, float lerpAmount, float weightMultiplier)
        {
            for (int i = 0; i < targetWeights.Length; i++)
            {
                float target = Math.Clamp(targetWeights[i] * weightMultiplier, 0.0f, 1.0f);
                appliedWeights[i] = Interp.Lerp(appliedWeights[i], target, lerpAmount);
            }
        }

        private static void FadeOutAppliedWeights(float[] appliedWeights, float lerpAmount)
        {
            for (int i = 0; i < appliedWeights.Length; i++)
                appliedWeights[i] = Interp.Lerp(appliedWeights[i], 0.0f, lerpAmount);
        }

        private static bool AreWeightsAtRest(float[]? weights)
        {
            if (weights is null || weights.Length == 0)
                return true;

            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] > 0.001f)
                    return false;
            }

            return true;
        }
    }

    internal sealed class Audio2Face3DAnimation
    {
        private readonly float[] _timecodes;
        private readonly float[][] _blendshapeFrames;
        private readonly float[][]? _emotionFrames;

        private Audio2Face3DAnimation(string[] blendshapeNames, string[] emotionNames, float[] timecodes, float[][] blendshapeFrames, float[][]? emotionFrames)
        {
            BlendshapeNames = blendshapeNames;
            EmotionNames = emotionNames;
            _timecodes = timecodes;
            _blendshapeFrames = blendshapeFrames;
            _emotionFrames = emotionFrames;
        }

        public string[] BlendshapeNames { get; }
        public string[] EmotionNames { get; }
        public int FrameCount => _timecodes.Length;
        public int EmotionCount => EmotionNames.Length;
        public float Duration => FrameCount == 0 ? 0.0f : _timecodes[^1];

        public static Audio2Face3DAnimation Parse(string csvText)
        {
            if (!TryParse(csvText, out Audio2Face3DAnimation? animation, out string? error) || animation is null)
                throw new FormatException(error ?? "Invalid Audio2Face-3D CSV.");

            return animation;
        }

        public static bool TryParse(string csvText, out Audio2Face3DAnimation? animation, out string? error)
        {
            animation = null;
            error = null;

            if (string.IsNullOrWhiteSpace(csvText))
            {
                error = "CSV is empty.";
                return false;
            }

            string[] lines = csvText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
            {
                error = "CSV must contain a header and at least one frame.";
                return false;
            }

            string[] headerColumns = lines[0].Split(',', StringSplitOptions.TrimEntries);
            if (headerColumns.Length < 2)
            {
                error = "CSV header must contain a timecode column and at least one blendshape or emotion column.";
                return false;
            }

            if (!string.Equals(headerColumns[0], "timecode", StringComparison.OrdinalIgnoreCase))
            {
                error = "CSV header must start with 'timecode'.";
                return false;
            }

            var blendshapeNames = new List<string>(headerColumns.Length - 1);
            var blendshapeColumnIndices = new List<int>(headerColumns.Length - 1);
            bool[] activeEmotionColumns = new bool[Audio2Face3DRegistry.Count];
            int[] emotionColumnIndices = new int[headerColumns.Length];
            Array.Fill(emotionColumnIndices, -1);

            for (int columnIndex = 1; columnIndex < headerColumns.Length; columnIndex++)
            {
                string columnName = headerColumns[columnIndex];
                if (Audio2Face3DRegistry.TryGetIndex(columnName, out int emotionIndex))
                {
                    if (activeEmotionColumns[emotionIndex])
                    {
                        error = $"CSV emotion column '{columnName}' is duplicated.";
                        return false;
                    }

                    activeEmotionColumns[emotionIndex] = true;
                    emotionColumnIndices[columnIndex] = emotionIndex;
                }
                else
                {
                    blendshapeNames.Add(columnName);
                    blendshapeColumnIndices.Add(columnIndex);
                }
            }

            int emotionCount = 0;
            for (int i = 0; i < activeEmotionColumns.Length; i++)
            {
                if (activeEmotionColumns[i])
                    emotionCount++;
            }

            if (blendshapeNames.Count == 0 && emotionCount == 0)
            {
                error = "CSV header must contain at least one blendshape or emotion column.";
                return false;
            }

            var timecodes = new List<float>(lines.Length - 1);
            var blendshapeFrames = new List<float[]>(lines.Length - 1);
            List<float[]>? emotionFrames = emotionCount == 0 ? null : new List<float[]>(lines.Length - 1);
            float previousTime = float.NegativeInfinity;

            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                string[] values = lines[lineIndex].Split(',', StringSplitOptions.TrimEntries);
                if (values.Length != headerColumns.Length)
                {
                    error = $"Line {lineIndex + 1} expected {headerColumns.Length} columns but found {values.Length}.";
                    return false;
                }

                if (!float.TryParse(values[0], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float timecode))
                {
                    error = $"Line {lineIndex + 1} has an invalid timecode '{values[0]}'.";
                    return false;
                }

                if (timecode < previousTime)
                {
                    error = $"Line {lineIndex + 1} timecode {timecode} is earlier than the previous frame time {previousTime}.";
                    return false;
                }

                float[] blendshapeFrame = new float[blendshapeNames.Count];
                float[]? emotionFrame = emotionFrames is null ? null : new float[Audio2Face3DRegistry.Count];
                for (int columnIndex = 1; columnIndex < values.Length; columnIndex++)
                {
                    if (!float.TryParse(values[columnIndex], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float weight))
                    {
                        error = $"Line {lineIndex + 1} column '{headerColumns[columnIndex]}' has an invalid float '{values[columnIndex]}'.";
                        return false;
                    }

                    int emotionIndex = emotionColumnIndices[columnIndex];
                    if (emotionIndex >= 0)
                    {
                        emotionFrame![emotionIndex] = weight;
                    }
                    else
                    {
                        int blendshapeIndex = blendshapeColumnIndices.IndexOf(columnIndex);
                        blendshapeFrame[blendshapeIndex] = weight;
                    }
                }

                previousTime = timecode;
                timecodes.Add(timecode);
                blendshapeFrames.Add(blendshapeFrame);
                emotionFrames?.Add(emotionFrame!);
            }

            if (timecodes.Count == 0)
            {
                error = "CSV did not contain any animation frames.";
                return false;
            }

            string[] emotionNames = emotionCount == 0
                ? []
                : [.. Audio2Face3DRegistry.Names.Where((_, emotionIndex) => activeEmotionColumns[emotionIndex])];

            animation = new Audio2Face3DAnimation([.. blendshapeNames], emotionNames, [.. timecodes], [.. blendshapeFrames], emotionFrames is null ? null : [.. emotionFrames]);
            return true;
        }

        public void Sample(float timecode, float[] output)
        {
            if (output.Length != BlendshapeNames.Length)
                throw new ArgumentException("Output buffer length must match blendshape count.", nameof(output));

            if (FrameCount == 0)
            {
                Array.Clear(output, 0, output.Length);
                return;
            }

            if (FrameCount == 1 || timecode <= _timecodes[0])
            {
                CopyFrame(0, output);
                return;
            }

            if (timecode >= _timecodes[^1])
            {
                CopyFrame(FrameCount - 1, output);
                return;
            }

            int frameIndex = Array.BinarySearch(_timecodes, timecode);
            if (frameIndex >= 0)
            {
                CopyFrame(frameIndex, output);
                return;
            }

            int nextIndex = ~frameIndex;
            int previousIndex = Math.Max(0, nextIndex - 1);
            float startTime = _timecodes[previousIndex];
            float endTime = _timecodes[nextIndex];
            float factor = endTime <= startTime
                ? 0.0f
                : Math.Clamp((timecode - startTime) / (endTime - startTime), 0.0f, 1.0f);

            float[] previousFrame = _blendshapeFrames[previousIndex];
            float[] nextFrame = _blendshapeFrames[nextIndex];
            for (int i = 0; i < output.Length; i++)
                output[i] = Interp.Lerp(previousFrame[i], nextFrame[i], factor);
        }

        public void SampleEmotions(float timecode, float[] output)
        {
            if (output.Length != Audio2Face3DRegistry.Count)
                throw new ArgumentException("Emotion output buffer length must match the supported Audio2Emotion channel count.", nameof(output));

            if (_emotionFrames is null || FrameCount == 0)
            {
                Array.Clear(output, 0, output.Length);
                return;
            }

            if (FrameCount == 1 || timecode <= _timecodes[0])
            {
                CopyEmotionFrame(0, output);
                return;
            }

            if (timecode >= _timecodes[^1])
            {
                CopyEmotionFrame(FrameCount - 1, output);
                return;
            }

            int frameIndex = Array.BinarySearch(_timecodes, timecode);
            if (frameIndex >= 0)
            {
                CopyEmotionFrame(frameIndex, output);
                return;
            }

            int nextIndex = ~frameIndex;
            int previousIndex = Math.Max(0, nextIndex - 1);
            float startTime = _timecodes[previousIndex];
            float endTime = _timecodes[nextIndex];
            float factor = endTime <= startTime
                ? 0.0f
                : Math.Clamp((timecode - startTime) / (endTime - startTime), 0.0f, 1.0f);

            float[] previousFrame = _emotionFrames[previousIndex];
            float[] nextFrame = _emotionFrames[nextIndex];
            for (int i = 0; i < output.Length; i++)
                output[i] = Interp.Lerp(previousFrame[i], nextFrame[i], factor);
        }

        private void CopyFrame(int frameIndex, float[] output)
            => Array.Copy(_blendshapeFrames[frameIndex], output, output.Length);

        private void CopyEmotionFrame(int frameIndex, float[] output)
            => Array.Copy(_emotionFrames![frameIndex], output, output.Length);
    }
}

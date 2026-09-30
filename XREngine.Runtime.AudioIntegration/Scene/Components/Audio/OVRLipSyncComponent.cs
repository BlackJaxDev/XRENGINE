using System.ComponentModel.DataAnnotations;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using static XREngine.Data.AudioData;

namespace XREngine.Components
{
    /// <summary>
    /// This component exposes access to Meta's OVRLipSync for facial animation using audio input.
    /// https://developers.meta.com/horizon/licenses/oculussdk/
    /// </summary>
    public class OVRLipSyncComponent : XRComponent
    {
        private readonly string[] _resolvedVisemeNames = new string[LipSyncVisemeInfo.Count];
        public AudioSourceComponent? GetAudioSource() => GetSiblingComponent<AudioSourceComponent>(false);

        private AudioSourceComponent? _audioSource;
        public AudioSourceComponent? AudioSource
        {
            get => _audioSource;
            set => SetField(ref _audioSource, value);
        }

        public ModelComponent? GetModelComponent() => ModelComponent ?? GetSiblingComponent<ModelComponent>(false);

        private ModelComponent? _modelComponent;
        public ModelComponent? ModelComponent
        {
            get => _modelComponent;
            set => SetField(ref _modelComponent, value);
        }

        private ILipSyncSession? _session;

        private static readonly long DirtyWindowTicks = RuntimeAudioIntegrationServices.SecondsToElapsedTicks(0.2f);
        private long _lastDirtyTicks;
        private float _inputSmoothSpeed = 10.0f;
        private float _visemeExaggeration = 1.5f;
        private float _laughExaggeration = 1.5f;

        private readonly float[] _lastInputVisemes = new float[LipSyncVisemeInfo.Count];
        private float _lastInputLaughterScore = 0.0f;

        private readonly float[] _visemes = new float[LipSyncVisemeInfo.Count];
        private float _laughterScore = 0.0f;

        private float _laughterThreshold = 0.5f;
        [Range(0.0f, 1.0f)]
        public float LaughterThreshold
        {
            get => _laughterThreshold;
            set => SetField(ref _laughterThreshold, value);
        }

        private float _laughterMultiplier = 1.5f;
        [Range(0.0f, 3.0f)]
        public float LaughterMultiplier
        {
            get => _laughterMultiplier;
            set => SetField(ref _laughterMultiplier, value);
        }

        private int _smoothAmount = 70;
        [Range(1, 100)]
        public int SmoothAmount
        {
            get => _smoothAmount;
            set => SetField(ref _smoothAmount, value);
        }

        private string _laughterBlendshapeName = "Laughter";
        public string LaughterBlendshapeName
        {
            get => _laughterBlendshapeName;
            set
            {
                if (SetField(ref _laughterBlendshapeName, value))
                    RefreshBlendshapeNameCache();
            }
        }

        private string _visemeNamePrefix = "";
        public string VisemeNamePrefix
        {
            get => _visemeNamePrefix;
            set
            {
                if (SetField(ref _visemeNamePrefix, value))
                    RefreshBlendshapeNameCache();
            }
        }

        private string _visemeNameSuffix = "";
        public string VisemeNameSuffix
        {
            get => _visemeNameSuffix;
            set
            {
                if (SetField(ref _visemeNameSuffix, value))
                    RefreshBlendshapeNameCache();
            }
        }

        internal static bool HasRecentAudioData(long currentTicks, long lastDirtyTicks)
            => Math.Max(0L, currentTicks - lastDirtyTicks) < DirtyWindowTicks;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            AudioSource = GetAudioSource();
            if (AudioSource is null)
            {
                Debug.Audio("No AudioSourceComponent found.");
                return;
            }

            // Match the analysis buffer size used by the native integration.
            int sampleRate = RuntimeAudioIntegrationServices.Current.SampleRate;
            int bufferSize = sampleRate / 10;
            _session = LipSyncSessionRegistry.Create(sampleRate, bufferSize);
            try
            {
                _session.SetSmoothing(_smoothAmount);
            }
            catch
            {
                _session.Dispose();
                _session = null;
                throw;
            }

            ModelComponent = GetModelComponent();
            RefreshBlendshapeNameCache();

            AudioSource.StreamingBufferEnqueuedByte += OnAudioDataReceived;
            AudioSource.StreamingBufferEnqueued += OnAudioDataReceived;
            AudioSource.StreamingBufferEnqueuedShort += OnAudioDataReceived;
            AudioSource.StreamingBufferEnqueuedFloat += OnAudioDataReceived;

            RegisterTick(ETickGroup.Late, ETickOrder.Animation, UpdateModel);
        }

        private void OnAudioDataReceived((int frequency, bool stereo, byte[] buffer) data)
        {
            if (data.buffer.Length == 0 || _session is null)
                return;
            _session.Process(data.buffer, data.stereo, _lastInputVisemes, ref _lastInputLaughterScore);
            _lastDirtyTicks = RuntimeAudioIntegrationServices.Current.ElapsedTicks;
        }

        private void OnAudioDataReceived((int frequency, bool stereo, short[] buffer) data)
        {
            if (data.buffer.Length == 0 || _session is null)
                return;
            _session.Process(data.buffer, data.stereo, _lastInputVisemes, ref _lastInputLaughterScore);
            _lastDirtyTicks = RuntimeAudioIntegrationServices.Current.ElapsedTicks;
        }

        private void OnAudioDataReceived((int frequency, bool stereo, float[] buffer) data)
        {
            if (data.buffer.Length == 0 || _session is null)
                return;
            _session.Process(data.buffer, data.stereo, _lastInputVisemes, ref _lastInputLaughterScore);
            _lastDirtyTicks = RuntimeAudioIntegrationServices.Current.ElapsedTicks;
        }

        private void OnAudioDataReceived(AudioData data)
        {
            if (data.Data is null)
                return;

            switch (data.Type)
            {
                case EPCMType.Byte:
                    OnAudioDataReceived((data.Frequency, data.Stereo, data.Data.GetBytes()));
                    break;
                case EPCMType.Short:
                    OnAudioDataReceived((data.Frequency, data.Stereo, data.Data!.GetShorts()));
                    break;
                case EPCMType.Float:
                    OnAudioDataReceived((data.Frequency, data.Stereo, data.Data!.GetFloats()));
                    break;
            }
        }

        public float InputSmoothSpeed
        {
            get => _inputSmoothSpeed;
            set => SetField(ref _inputSmoothSpeed, value);
        }
        public float VisemeExaggeration
        {
            get => _visemeExaggeration;
            set => SetField(ref _visemeExaggeration, value);
        }
        public float LaughExaggeration
        {
            get => _laughExaggeration;
            set => SetField(ref _laughExaggeration, value);
        }

        internal static float GetSmoothingFactor(float deltaSeconds, float smoothingSpeed)
            => Math.Clamp(deltaSeconds * smoothingSpeed, 0.0f, 1.0f);

        private void UpdateModel()
        {
            bool hasDataUpdated = HasRecentAudioData(RuntimeAudioIntegrationServices.Current.ElapsedTicks, _lastDirtyTicks);
            if (hasDataUpdated)
            {
                float dt = GetSmoothingFactor(RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds, InputSmoothSpeed);
                for (int i = 0; i < LipSyncVisemeInfo.Count; i++)
                    _visemes[i] = Interp.Lerp(_visemes[i], _lastInputVisemes[i] * VisemeExaggeration, dt);

                _laughterScore = Interp.Lerp(_laughterScore, _lastInputLaughterScore * LaughExaggeration, dt);
            }
            else // No input, move back to silence
            {
                float dt = RuntimeAudioIntegrationServices.Current.UpdateDeltaSeconds;
                if (_laughterScore > 0.0f)
                    _laughterScore = MathF.Max(0.0f, _laughterScore - dt);
                if (_visemes[0] < 1.0f) // Silence
                    _visemes[0] = MathF.Min(1.0f, _visemes[0] + dt);
                for (int i = 1; i < LipSyncVisemeInfo.Count; i++)
                {
                    if (_visemes[i] > 0.0f)
                        _visemes[i] = MathF.Max(0.0f, _visemes[i] - dt);
                }
            }

            // Apply visemes to model
            var modelComp = ModelComponent ?? GetModelComponent();
            if (modelComp is null)
                return;
            ModelComponent = modelComp;
            
            for (int i = 0; i < _visemes.Length; i++)
            {
                modelComp.SetBlendShapeWeightNormalized(_resolvedVisemeNames[i], _visemes[i]);
            }

            //if (laughterScore > 0.0f)
            //    Debug.Audio($"Laughter: {laughterScore}");
            modelComp.SetBlendShapeWeightNormalized(_laughterBlendshapeName, _laughterScore);
        }

        private void ConvertLaughterScore(ref float laughterScore)
        {
            laughterScore = laughterScore < _laughterThreshold ? 0.0f : laughterScore - _laughterThreshold;
            laughterScore = MathF.Min(laughterScore * _laughterMultiplier, 1.0f);
            laughterScore *= 1.0f / _laughterThreshold;
        }

        private void RefreshBlendshapeNameCache()
        {
            for (int i = 0; i < LipSyncVisemeInfo.Count; i++)
                _resolvedVisemeNames[i] = ResolveBlendshapeName(LipSyncVisemeInfo.Names[i]);
        }

        private string ResolveBlendshapeName(string sourceName)
        {
            bool hasPrefix = !string.IsNullOrEmpty(VisemeNamePrefix);
            bool hasSuffix = !string.IsNullOrEmpty(VisemeNameSuffix);
            if (!hasPrefix && !hasSuffix)
                return sourceName;
            if (hasPrefix && !hasSuffix)
                return string.Concat(VisemeNamePrefix, sourceName);
            if (!hasPrefix)
                return string.Concat(sourceName, VisemeNameSuffix);
            return string.Concat(VisemeNamePrefix, sourceName, VisemeNameSuffix);
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

            UnregisterTick(ETickGroup.Late, ETickOrder.Animation, UpdateModel);

            _session?.Dispose();
            _session = null;
            base.OnComponentDeactivated();
        }
    }
}

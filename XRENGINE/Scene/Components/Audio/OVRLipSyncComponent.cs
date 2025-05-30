using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using static XREngine.Components.OVRLipSync;
using static XREngine.Data.AudioData;

namespace XREngine.Components
{
    /// <summary>
    /// This component exposes access to Meta's OVRLipSync for facial animation using audio input.
    /// https://developers.meta.com/horizon/licenses/oculussdk/
    /// </summary>
    public class OVRLipSyncComponent : XRComponent
    {
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

        private ovrLipSyncContext _ctx = new();

        private float _lastDirtyTime = 0;
        private float _inputSmoothSpeed = 10.0f;
        private float _visemeExaggeration = 1.5f;
        private float _laughExaggeration = 1.5f;

        private readonly float[] _lastInputVisemes = new float[VisemeCount];
        private float _lastInputLaughterScore = 0.0f;

        private readonly float[] _visemes = new float[VisemeCount];
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
            set => SetField(ref _laughterBlendshapeName, value);
        }

        private string _visemeNamePrefix = "";
        public string VisemeNamePrefix
        {
            get => _visemeNamePrefix;
            set => SetField(ref _visemeNamePrefix, value);
        }

        private string _visemeNameSuffix = "";
        public string VisemeNameSuffix
        {
            get => _visemeNameSuffix;
            set => SetField(ref _visemeNameSuffix, value);
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            // Initialize the OVRLipSync library
            // Feed data to the LipSync engine in 10 ms chunks (100 Hz)
            var bufferSize = (int)(Engine.Audio.SampleRate * 0.1f);
            if (ovrLipSyncDll_Initialize(Engine.Audio.SampleRate, bufferSize) == 0)
            {
                Debug.Out("OVRLipSync library initialized.");
            }
            else
            {
                Debug.Out("Failed to initialize OVRLipSync library.");
                return;
            }

            // Create a new OVRLipSync context
            ovrLipSyncResult result = ovrLipSyncDll_CreateContextEx(
                ref _ctx,
                ovrLipSyncContextProvider.ovrLipSyncContextProvider_EnhancedWithLaughter,
                Engine.Audio.SampleRate,
                true);

            if (result == ovrLipSyncResult.ovrLipSyncSuccess)
            {
                Debug.Out("OVRLipSync context created.");
            }
            else
            {
                Debug.Out("Failed to create OVRLipSync context.");
                return;
            }

            AudioSource = GetAudioSource();
            if (AudioSource is null)
            {
                Debug.Out("No AudioSourceComponent found.");
                return;
            }

            AudioSource.StreamingBufferEnqueuedByte += OnAudioDataReceived;
            AudioSource.StreamingBufferEnqueued += OnAudioDataReceived;
            AudioSource.StreamingBufferEnqueuedShort += OnAudioDataReceived;
            AudioSource.StreamingBufferEnqueuedFloat += OnAudioDataReceived;

            ovrLipSyncDll_SendSignal(_ctx, ovrLipSyncSignals.ovrLipSyncSignals_VisemeSmoothing, _smoothAmount, 0);

            RegisterTick(ETickGroup.Late, ETickOrder.Animation, UpdateModel);
        }

        private unsafe void OnAudioDataReceived((int frequency, bool stereo, byte[] buffer) data)
        {
            if (data.buffer.Length == 0)
                return;

            // Convert each byte to a float
            float* samples = stackalloc float[data.buffer.Length];
            for (int i = 0; i < data.buffer.Length; i++)
                samples[i] = data.buffer[i] / 255.0f;

            int frameNumber = 0;
            int frameDelay = 0;
            var result = ovrLipSyncDll_ProcessFrameEx(
                _ctx.handle,
                (nint)samples,
                (uint)data.buffer.Length,
                ovrLipSyncAudioDataType.ovrLipSyncAudioDataType_F32_Mono,
                ref frameNumber,
                ref frameDelay,
                _lastInputVisemes,
                VisemeCount,
                ref _lastInputLaughterScore,
                null,
                0);

            if (result != ovrLipSyncResult.ovrLipSyncSuccess)
                Debug.LogWarning("Failed to process audio data.");
            else
                _lastDirtyTime = Engine.ElapsedTime;
        }

        private unsafe void OnAudioDataReceived((int frequency, bool stereo, short[] buffer) data)
        {
            if (data.buffer.Length == 0)
                return;

            var handle = GCHandle.Alloc(data.buffer, GCHandleType.Pinned);
            int frameNumber = 0;
            int frameDelay = 0;
            var result = ovrLipSyncDll_ProcessFrameEx(
                _ctx.handle,
                handle.AddrOfPinnedObject(),
                (uint)data.buffer.Length,
                ovrLipSyncAudioDataType.ovrLipSyncAudioDataType_F32_Mono,
                ref frameNumber,
                ref frameDelay,
                _lastInputVisemes,
                VisemeCount,
                ref _lastInputLaughterScore,
                null,
                0);

            if (result != ovrLipSyncResult.ovrLipSyncSuccess)
                Debug.LogWarning("Failed to process audio data.");
            else
                _lastDirtyTime = Engine.ElapsedTime;
        }

        private unsafe void OnAudioDataReceived((int frequency, bool stereo, float[] buffer) data)
        {
            if (data.buffer.Length == 0)
                return;

            var handle = GCHandle.Alloc(data.buffer, GCHandleType.Pinned);
            int frameNumber = 0;
            int frameDelay = 0;
            var result = ovrLipSyncDll_ProcessFrameEx(
                _ctx.handle,
                handle.AddrOfPinnedObject(),
                (uint)data.buffer.Length,
                ovrLipSyncAudioDataType.ovrLipSyncAudioDataType_F32_Mono,
                ref frameNumber,
                ref frameDelay,
                _lastInputVisemes,
                VisemeCount,
                ref _lastInputLaughterScore,
                null,
                0);

            if (result != ovrLipSyncResult.ovrLipSyncSuccess)
                Debug.LogWarning("Failed to process audio data.");
            else
                _lastDirtyTime = Engine.ElapsedTime;
        }

        private unsafe void OnAudioDataReceived(AudioData data)
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

        private void Callback(IntPtr opaque, IntPtr pFrame, ovrLipSyncResult result)
        {
            if (result != ovrLipSyncResult.ovrLipSyncSuccess)
                return;

            ovrLipSyncFrame frame = Marshal.PtrToStructure<ovrLipSyncFrame>(pFrame);
            UpdateModel(frame);
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

        private unsafe void UpdateModel()
        {
            float time = Engine.ElapsedTime;
            bool hasDataUpdated = time - _lastDirtyTime < 0.2f; // 100 ms * 2
            if (hasDataUpdated)
            {
                float dt = Engine.Delta * InputSmoothSpeed;
                for (int i = 0; i < VisemeCount; i++)
                {
                    _visemes[i] = Interp.Lerp(_visemes[i], _lastInputVisemes[i] * VisemeExaggeration, dt);
                    _laughterScore = Interp.Lerp(_laughterScore, _lastInputLaughterScore * LaughExaggeration, dt);
                }
            }
            else // No input, move back to silence
            {
                float dt = Engine.Delta;
                if (_laughterScore > 0.0f)
                    _laughterScore = MathF.Max(0.0f, _laughterScore - dt);
                if (_visemes[0] < 1.0f) // Silence
                    _visemes[0] = MathF.Min(1.0f, _visemes[0] + dt);
                for (int i = 1; i < VisemeCount; i++)
                {
                    if (_visemes[i] > 0.0f)
                        _visemes[i] = MathF.Max(0.0f, _visemes[i] - dt);
                }
            }

            // Apply visemes to model
            var modelComp = GetModelComponent();
            if (modelComp is null)
                return;
            
            for (int i = 0; i < _visemes.Length; i++)
            {
                //if (visemes[i] > 0.0f)
                //    Debug.Out($"Viseme {VisemeNames[i]}: {visemes[i]}");
                modelComp?.SetBlendShapeWeightNormalized($"{VisemeNamePrefix}{VisemeNames[i]}{VisemeNameSuffix}", _visemes[i]);
            }

            //if (laughterScore > 0.0f)
            //    Debug.Out($"Laughter: {laughterScore}");
            modelComp?.SetBlendShapeWeightNormalized(LaughterBlendshapeName, _laughterScore);
        }

        private void UpdateModel(ovrLipSyncFrame frame)
        {
            SetVisemeToMorphTarget(frame);
            SetLaughterToMorphTarget(frame);
        }

        /// <summary>
        /// Sets the viseme to morph target.
        /// </summary>
        private unsafe void SetVisemeToMorphTarget(ovrLipSyncFrame frame)
        {
            float* visemes = (float*)frame.visemes;
            uint len;
            if (frame.visemesLength != VisemeNames.Length)
            {
                Debug.Out("Viseme length mismatch, using minimum.");
                len = (uint)Math.Min(frame.visemesLength, VisemeNames.Length);
            }
            else
            {
                len = frame.visemesLength;
            }

            for (int i = 0; i < len; i++)
                _visemes[i] = visemes[i];
        }

        void SetLaughterToMorphTarget(ovrLipSyncFrame frame)
        {
            // Laughter score will be raw classifier output in [0,1]
            float laughterScore = frame.laughterScore;
            // Threshold then re-map to [0,1]
            ConvertLaughterScore(ref laughterScore);
            _laughterScore = laughterScore;
        }

        private void ConvertLaughterScore(ref float laughterScore)
        {
            laughterScore = laughterScore < _laughterThreshold ? 0.0f : laughterScore - _laughterThreshold;
            laughterScore = MathF.Min(laughterScore * _laughterMultiplier, 1.0f);
            laughterScore *= 1.0f / _laughterThreshold;
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
        }
    }
}

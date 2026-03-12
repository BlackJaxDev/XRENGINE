using System.ComponentModel;
using XREngine.Core.Attributes;

namespace XREngine.Components
{
    [XRComponentEditor("XREngine.Editor.ComponentEditors.Audio2Face3DNativeBridgeComponentEditor")]
    [RequireComponents(typeof(Audio2Face3DComponent), typeof(MicrophoneComponent))]
    public sealed class Audio2Face3DNativeBridgeComponent : XRComponent
    {
        private static readonly Audio2Face3DNativeBridgeAdapter SharedAdapter = new();

        private string _faceModelPath = string.Empty;
        private string _emotionModelPath = string.Empty;
        private int _inputSampleRate = Audio2Face3DNativeBridge.DefaultInputSampleRate;
        private bool _enableEmotion = true;
        private bool _driveFromMicrophone = true;
        private bool _autoRegisterAdapter = true;
        private bool _autoConnectLiveClient = true;
        private bool _autoStartMicrophone = true;
        private bool _autoConfigureMicrophoneFormat = true;
        private string _lastBridgeError = string.Empty;

        private Audio2Face3DNativeBridgeSession? _session;
        private Audio2Face3DComponent? _connectedComponent;

        public Audio2Face3DComponent Audio2Face => GetSiblingComponent<Audio2Face3DComponent>(true)!;
        public MicrophoneComponent Microphone => GetSiblingComponent<MicrophoneComponent>(true)!;

        [DefaultValue(16000)]
        public int InputSampleRate
        {
            get => _inputSampleRate;
            set => SetField(ref _inputSampleRate, Math.Max(1, value));
        }

        public string FaceModelPath
        {
            get => _faceModelPath;
            set => SetField(ref _faceModelPath, value ?? string.Empty);
        }

        public string EmotionModelPath
        {
            get => _emotionModelPath;
            set => SetField(ref _emotionModelPath, value ?? string.Empty);
        }

        [DefaultValue(true)]
        public bool EnableEmotion
        {
            get => _enableEmotion;
            set => SetField(ref _enableEmotion, value);
        }

        [DefaultValue(true)]
        public bool DriveFromMicrophone
        {
            get => _driveFromMicrophone;
            set => SetField(ref _driveFromMicrophone, value);
        }

        [DefaultValue(true)]
        public bool AutoRegisterAdapter
        {
            get => _autoRegisterAdapter;
            set => SetField(ref _autoRegisterAdapter, value);
        }

        [DefaultValue(true)]
        public bool AutoConnectLiveClient
        {
            get => _autoConnectLiveClient;
            set => SetField(ref _autoConnectLiveClient, value);
        }

        [DefaultValue(true)]
        public bool AutoStartMicrophone
        {
            get => _autoStartMicrophone;
            set => SetField(ref _autoStartMicrophone, value);
        }

        [DefaultValue(true)]
        public bool AutoConfigureMicrophoneFormat
        {
            get => _autoConfigureMicrophoneFormat;
            set => SetField(ref _autoConfigureMicrophoneFormat, value);
        }

        [Browsable(false)]
        public string LastBridgeError
        {
            get => _lastBridgeError;
            private set => SetField(ref _lastBridgeError, value ?? string.Empty);
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            RegisterTick(ETickGroup.Late, ETickOrder.Animation, PumpBridge);
            EnsureAdapterRegistered();

            if (AutoConnectLiveClient
                && Audio2Face.SourceMode == EAudio2Face3DSourceMode.LiveStream
                && !Audio2Face.IsLiveConnected)
            {
                Audio2Face.TryConnectLiveClient();
            }
        }

        protected override void OnComponentDeactivated()
        {
            ReleaseSession(markComponentDisconnected: true, error: null);
            UnregisterTick(ETickGroup.Late, ETickOrder.Animation, PumpBridge);

            if (AutoRegisterAdapter && ReferenceEquals(Audio2Face3DLiveClientRegistry.Adapter, SharedAdapter))
                Audio2Face3DLiveClientRegistry.Adapter = null;

            base.OnComponentDeactivated();
        }

        internal bool TryConnectSession(Audio2Face3DComponent component, out string? error)
        {
            if (!ReferenceEquals(component, Audio2Face))
            {
                error = "Audio2Face3DNativeBridgeComponent only supports the sibling Audio2Face3DComponent on the same scene node.";
                return false;
            }

            if (_session is not null)
            {
                error = null;
                return true;
            }

            EnsureAdapterRegistered();

            if (!Audio2Face3DNativeBridge.TryCreateSession(new Audio2Face3DNativeBridgeSessionConfig
            {
                InputSampleRate = InputSampleRate,
                EnableEmotion = EnableEmotion,
                FaceModelPath = FaceModelPath,
                EmotionModelPath = EmotionModelPath,
            }, out Audio2Face3DNativeBridgeSession? session, out error))
            {
                LastBridgeError = string.IsNullOrWhiteSpace(error) ? "Audio2Face native bridge session creation failed." : error;
                return false;
            }

            _session = session;
            _connectedComponent = component;
            LastBridgeError = string.Empty;

            AttachMicrophone();
            component.MarkLiveClientConnected();
            error = null;
            return true;
        }

        internal void DisconnectSession(Audio2Face3DComponent component, string? error = null)
        {
            if (_connectedComponent is not null && !ReferenceEquals(component, _connectedComponent))
                return;

            ReleaseSession(markComponentDisconnected: true, error);
        }

        private void EnsureAdapterRegistered()
        {
            if (!AutoRegisterAdapter)
                return;

            if (Audio2Face3DLiveClientRegistry.Adapter is null || ReferenceEquals(Audio2Face3DLiveClientRegistry.Adapter, SharedAdapter))
            {
                Audio2Face3DLiveClientRegistry.Adapter = SharedAdapter;
                return;
            }

            LastBridgeError = "Audio2Face3DLiveClientRegistry.Adapter is already assigned to a different live adapter. Clear that adapter or disable AutoRegisterAdapter to avoid the conflict.";
        }

        private void AttachMicrophone()
        {
            Microphone.BufferReceived -= OnMicrophoneBufferReceived;

            if (!DriveFromMicrophone)
                return;

            if (AutoConfigureMicrophoneFormat && !Microphone.IsCapturing)
            {
                Microphone.SampleRate = InputSampleRate;
                Microphone.BitsPerSampleValue = (int)MicrophoneComponent.EBitsPerSample.Sixteen;
            }

            Microphone.BufferReceived += OnMicrophoneBufferReceived;

            if (AutoStartMicrophone && Microphone.Capture && !Microphone.IsCapturing)
                Microphone.StartCapture();
        }

        private void ReleaseSession(bool markComponentDisconnected, string? error)
        {
            Microphone.BufferReceived -= OnMicrophoneBufferReceived;
            _session?.Dispose();
            _session = null;

            if (markComponentDisconnected && _connectedComponent is not null)
                _connectedComponent.MarkLiveClientDisconnected(error);

            _connectedComponent = null;
            if (!string.IsNullOrWhiteSpace(error))
                LastBridgeError = error;
        }

        private void OnMicrophoneBufferReceived(byte[] audioData)
        {
            if (_session is null || !DriveFromMicrophone)
                return;

            short[] pcm16;
            try
            {
                pcm16 = Audio2Face3DNativeBridgeAudioConverter.ConvertToPcm16Mono(
                    audioData,
                    Microphone.BitsPerSampleValue,
                    Microphone.SampleRate,
                    InputSampleRate);
            }
            catch (Exception ex)
            {
                FailBridge($"Audio2Face microphone conversion failed: {ex.Message}");
                return;
            }

            if (!_session.TrySubmitPcm16(pcm16, InputSampleRate, out string? error))
                FailBridge(error);
        }

        private void PumpBridge()
        {
            if (_session is null || _connectedComponent is null)
                return;

            PumpBlendshapeFrame();

            if (EnableEmotion)
                PumpEmotionFrame();
        }

        private void PumpBlendshapeFrame()
        {
            if (_session is null || _connectedComponent is null)
                return;

            EAudio2Face3DNativePollResult result = _session.PollBlendshapeFrame(out string[]? blendshapeNames, out float[]? weights, out string? error);
            if (result == EAudio2Face3DNativePollResult.NoData)
                return;

            if (result == EAudio2Face3DNativePollResult.Error)
            {
                FailBridge(error);
                return;
            }

            if (!_connectedComponent.TryUpdateLiveFrame(blendshapeNames!, weights!, out string? updateError))
                FailBridge(updateError);
        }

        private void PumpEmotionFrame()
        {
            if (_session is null || _connectedComponent is null)
                return;

            EAudio2Face3DNativePollResult result = _session.PollEmotionFrame(out string[]? emotionNames, out float[]? weights, out string? error);
            if (result == EAudio2Face3DNativePollResult.NoData)
                return;

            if (result == EAudio2Face3DNativePollResult.Error)
            {
                FailBridge(error);
                return;
            }

            if (!_connectedComponent.TryUpdateLiveEmotionFrame(emotionNames!, weights!, out string? updateError))
                FailBridge(updateError);
        }

        private void FailBridge(string? error)
        {
            string resolvedError = string.IsNullOrWhiteSpace(error)
                ? "Audio2Face native bridge failed."
                : error;

            Debug.LogWarning(resolvedError);
            ReleaseSession(markComponentDisconnected: true, resolvedError);
        }

        private sealed class Audio2Face3DNativeBridgeAdapter : IAudio2Face3DLiveClientAdapter
        {
            public bool TryConnect(Audio2Face3DComponent component, out string? error)
            {
                Audio2Face3DNativeBridgeComponent? bridge = component.GetSiblingComponent<Audio2Face3DNativeBridgeComponent>(false);
                if (bridge is null)
                {
                    error = "Add Audio2Face3DNativeBridgeComponent to the same scene node as Audio2Face3DComponent and MicrophoneComponent before using LiveStream mode.";
                    return false;
                }

                return bridge.TryConnectSession(component, out error);
            }

            public void Disconnect(Audio2Face3DComponent component)
                => component.GetSiblingComponent<Audio2Face3DNativeBridgeComponent>(false)?.DisconnectSession(component);
        }
    }
}

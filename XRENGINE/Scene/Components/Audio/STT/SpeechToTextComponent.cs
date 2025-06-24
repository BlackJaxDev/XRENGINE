using System.Text;
using System.Text.Json;
using XREngine.Core.Attributes;
using XREngine.Data.Core;

namespace XREngine.Components
{
    [RequireComponents(typeof(MicrophoneComponent))]
    public class SpeechToTextComponent : XRComponent
    {
        public MicrophoneComponent Microphone => GetSiblingComponent<MicrophoneComponent>(true)!;

        private readonly List<byte[]> _audioBuffer = new();
        private readonly object _bufferLock = new();
        private bool _isProcessing = false;
        private DateTime _lastSpeechTime = DateTime.MinValue;
        private readonly TimeSpan _silenceThreshold = TimeSpan.FromSeconds(1.5);

        // Configuration
        private ESTTProvider _selectedProvider = ESTTProvider.Google;
        private string _apiKey = "";
        private string _language = "en-US";
        private bool _enableInterimResults = false;
        private float _confidenceThreshold = 0.7f;
        private int _maxBufferSize = 10; // Number of audio buffers to accumulate before processing
        private bool _autoProcess = true;
        private bool _enableVAD = true; // Voice Activity Detection

        // Events
        public XREvent<(SpeechToTextComponent component, string text, float confidence)>? TextReceived;
        public XREvent<(SpeechToTextComponent component, string text, bool isFinal)>? InterimTextReceived;
        public XREvent<(SpeechToTextComponent component, string error)>? ErrorOccurred;

        // Properties
        public ESTTProvider SelectedProvider
        {
            get => _selectedProvider;
            set => SetField(ref _selectedProvider, value);
        }

        public string ApiKey
        {
            get => _apiKey;
            set => SetField(ref _apiKey, value);
        }

        public string Language
        {
            get => _language;
            set => SetField(ref _language, value);
        }

        public bool EnableInterimResults
        {
            get => _enableInterimResults;
            set => SetField(ref _enableInterimResults, value);
        }

        public float ConfidenceThreshold
        {
            get => _confidenceThreshold;
            set => SetField(ref _confidenceThreshold, Math.Clamp(value, 0f, 1f));
        }

        public int MaxBufferSize
        {
            get => _maxBufferSize;
            set => SetField(ref _maxBufferSize, Math.Max(1, value));
        }

        public bool AutoProcess
        {
            get => _autoProcess;
            set => SetField(ref _autoProcess, value);
        }

        public bool EnableVAD
        {
            get => _enableVAD;
            set => SetField(ref _enableVAD, value);
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            Microphone.BufferReceived += OnBufferReceived;
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Microphone.BufferReceived -= OnBufferReceived;
        }

        private void OnBufferReceived(byte[] audioData)
        {
            if (!AutoProcess || _isProcessing)
                return;

            lock (_bufferLock)
            {
                _audioBuffer.Add(audioData);
                _lastSpeechTime = DateTime.Now;

                // Process if we have enough data or if silence threshold is reached
                if (_audioBuffer.Count >= MaxBufferSize || 
                    (EnableVAD && DateTime.Now - _lastSpeechTime > _silenceThreshold && _audioBuffer.Count > 0))
                {
                    ProcessAudioBuffer();
                }
            }
        }

        private async void ProcessAudioBuffer()
        {
            if (_isProcessing)
                return;

            _isProcessing = true;
            byte[] audioData;

            lock (_bufferLock)
            {
                if (_audioBuffer.Count == 0)
                {
                    _isProcessing = false;
                    return;
                }

                // Combine all audio buffers
                int totalSize = _audioBuffer.Sum(buffer => buffer.Length);
                audioData = new byte[totalSize];
                int offset = 0;

                foreach (var buffer in _audioBuffer)
                {
                    Buffer.BlockCopy(buffer, 0, audioData, offset, buffer.Length);
                    offset += buffer.Length;
                }

                _audioBuffer.Clear();
            }

            try
            {
                var provider = CreateSTTProvider();
                var result = await provider.TranscribeAsync(audioData, Microphone.SampleRate, Microphone.BitsPerSampleValue);

                if (result.Success)
                {
                    if (result.Confidence >= ConfidenceThreshold)
                    {
                        TextReceived?.Invoke((this, result.Text, result.Confidence));
                    }
                    else if (EnableInterimResults)
                    {
                        InterimTextReceived?.Invoke((this, result.Text, result.IsFinal));
                    }
                }
                else
                {
                    ErrorOccurred?.Invoke((this, result.Error ?? "Unknown transcription error"));
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke((this, ex.Message));
            }
            finally
            {
                _isProcessing = false;
            }
        }

        public async Task<string> TranscribeAudioAsync(byte[] audioData)
        {
            var provider = CreateSTTProvider();
            var result = await provider.TranscribeAsync(audioData, Microphone.SampleRate, Microphone.BitsPerSampleValue);
            return result.Success ? result.Text : "";
        }

        public void ProcessBufferedAudio()
        {
            if (!_isProcessing)
                ProcessAudioBuffer();
        }

        public void ClearAudioBuffer()
        {
            lock (_bufferLock)
            {
                _audioBuffer.Clear();
            }
        }

        private ISTTProvider CreateSTTProvider() => _selectedProvider switch
        {
            ESTTProvider.Google => new GoogleSTTProvider(ApiKey, Language),
            ESTTProvider.OpenAI => new OpenAIWhisperProvider(ApiKey, Language),
            ESTTProvider.Azure => new AzureSTTProvider(ApiKey, Language),
            ESTTProvider.Amazon => new AmazonSTTProvider(ApiKey, Language),
            ESTTProvider.Deepgram => new DeepgramSTTProvider(ApiKey, Language),
            ESTTProvider.AssemblyAI => new AssemblyAISTTProvider(ApiKey, Language),
            ESTTProvider.RevAI => new RevAISTTProvider(ApiKey, Language),
            _ => throw new NotSupportedException($"STT provider {_selectedProvider} is not supported")
        };
    }

    public enum ESTTProvider
    {
        Google,
        OpenAI,
        Azure,
        Amazon,
        Deepgram,
        AssemblyAI,
        RevAI
    }
}
